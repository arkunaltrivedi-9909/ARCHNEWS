using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KTA.SmartySheets.Core;

namespace KTA.SmartySheets.UI
{
    internal enum HandlerMode { Idle, Analyse, Export }

    /// <summary>
    /// The only bridge between the modeless window and Revit.
    ///
    /// Every Document and Element call in this add-in happens inside Execute, on Revit's
    /// API thread. Nothing here may be moved onto a Task or an async method: the window
    /// asks by raising the event, and gets answered when Revit is ready to answer.
    /// </summary>
    internal sealed class ExportHandler : IExternalEventHandler
    {
        private ExternalEvent _event;

        private Document _doc;
        private ExportSettings _settings;
        private Ledger _ledger;
        private ChangeAnalyzer _analyzer;
        private AnalysisContext _context;
        private ExportEngine _engine;
        private Queue<SheetRow> _exportQueue;

        private DateTime _runStartedLocal;
        private int _done;
        private int _total;
        private bool _cancelRequested;
        private bool _anyFailure;

        public HandlerMode Mode { get; private set; } = HandlerMode.Idle;
        public bool IsBusy { get { return Mode != HandlerMode.Idle; } }

        public Action<int, int, string> Progress;
        public Action<AnalysisContext> AnalyseFinished;
        public Action<bool, string> ExportFinished;
        public Action<string> Failed;

        public void Bind(ExternalEvent externalEvent) { _event = externalEvent; }

        public string GetName() { return "KTA Smarty Sheets"; }

        /// <summary>Honoured between batches, which is why batches stay small.</summary>
        public void RequestCancel() { _cancelRequested = true; }

        public void QueueAnalyse(Document doc, ExportSettings settings, ChangeAnalyzer analyzer)
        {
            _doc = doc;
            _settings = settings;
            _analyzer = analyzer;
            _cancelRequested = false;
            _anyFailure = false;
            _done = 0;
            _context = null;
            Mode = HandlerMode.Analyse;
            _event.Raise();
        }

        public void QueueExport(ExportSettings settings)
        {
            _settings = settings;
            _cancelRequested = false;
            _anyFailure = false;
            _done = 0;
            _engine = new ExportEngine(settings);
            _exportQueue = null;
            _runStartedLocal = DateTime.Now;
            Mode = HandlerMode.Export;
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                switch (Mode)
                {
                    case HandlerMode.Analyse: RunAnalyseBatch(); break;
                    case HandlerMode.Export: RunExportBatch(); break;
                }
            }
            catch (Exception ex)
            {
                Mode = HandlerMode.Idle;
                Log.Instance.Error("Handler batch failed.", ex);
                var failed = Failed;
                if (failed != null) failed(ex.Message);
            }
        }

        private void RunAnalyseBatch()
        {
            if (_context == null)
            {
                _ledger = new Ledger(_settings.OutputFolder);
                _ledger.Load(ModelId(_doc), _doc.PathName);

                _context = _analyzer.Begin(_doc, _ledger, _settings);
                _total = _context.Total;

                NameTemplate.AssignNames(_doc, _context.Rows, _settings.NameTemplate);
            }

            for (var i = 0; i < _settings.BatchSize && _context.Pending.Count > 0; i++)
            {
                if (_cancelRequested) break;

                var row = _context.Pending.Dequeue();
                _analyzer.Analyze(_context, row);
                _done++;
            }

            Report(_context.Pending.Count > 0 ? "Analysing" : "Analysed");

            if (_context.Pending.Count > 0 && !_cancelRequested)
            {
                // Hand control back to Revit. This return is what keeps the ribbon alive
                // and lets the Cancel button's click actually be delivered.
                _event.Raise();
                return;
            }

            Mode = HandlerMode.Idle;
            var finished = AnalyseFinished;
            if (finished != null) finished(_context);
        }

        private void RunExportBatch()
        {
            if (_context == null)
            {
                Mode = HandlerMode.Idle;
                var failed = Failed;
                if (failed != null) failed("Analyse first, so the tool knows what needs exporting.");
                return;
            }

            if (_exportQueue == null)
            {
                string reason;
                if (!PathSafety.TryProveWritable(_settings.OutputFolder, out reason))
                {
                    Mode = HandlerMode.Idle;
                    var failed = Failed;
                    if (failed != null) failed(reason);
                    return;
                }

                _exportQueue = new Queue<SheetRow>(_context.Rows.Where(r => r.Selected && r.IsExportable));
                _total = _exportQueue.Count;

                foreach (var row in _context.Rows) { row.Result = string.Empty; row.WrittenFiles.Clear(); }

                Log.Instance.Info("Export begun: " + _total + " of " + _context.Rows.Count + " sheet(s) selected.");
            }

            for (var i = 0; i < _settings.BatchSize && _exportQueue.Count > 0; i++)
            {
                if (_cancelRequested) break;

                var row = _exportQueue.Dequeue();
                var result = _engine.ExportSheet(_doc, row);

                if (result.Success)
                {
                    row.WrittenFiles.AddRange(result.Files);
                    row.Result = "Exported";
                    row.State = SheetState.Unchanged;
                    row.Why = "exported in this run";
                    row.Selected = false;
                    _ledger.RecordSuccess(row, result.Files);
                }
                else
                {
                    _anyFailure = true;
                    row.WrittenFiles.AddRange(result.Files);
                    row.Result = result.Message;

                    // Drop any history for this sheet so the next run retries it. A failure
                    // that leaves a ledger entry behind becomes a permanently skipped sheet.
                    _ledger.RecordFailure(row);
                }

                _done++;
            }

            Report(_exportQueue.Count > 0 ? "Exporting" : "Exported");

            if (_exportQueue.Count > 0 && !_cancelRequested)
            {
                _event.Raise();
                return;
            }

            FinishExport();
        }

        private void FinishExport()
        {
            var cancelled = _cancelRequested && _exportQueue.Count > 0;

            foreach (var row in _exportQueue) row.Result = "Not reached, the run was cancelled";

            _ledger.SetDocumentVersion(_context.CurrentVersionGuid, _context.CurrentNumberOfSaves);
            _ledger.Save();

            string manifest = null;
            try
            {
                manifest = ManifestWriter.Write(_settings.OutputFolder, _context.Rows, _settings,
                                                _runStartedLocal, _doc.Title, cancelled);
            }
            catch (Exception ex)
            {
                // The drawings are already on disk. A missing checklist is worth a log line,
                // not a failed run.
                Log.Instance.Error("Manifest could not be written.", ex);
            }

            // Session evidence is only safe to discard when every sheet that needed
            // exporting actually got exported. Anything left behind keeps its evidence,
            // or it silently reads Unchanged next time.
            var everythingHandled = !cancelled && !_anyFailure &&
                                    _context.Rows.All(r => !r.IsExportable || r.Result == "Exported");

            if (everythingHandled) App.Tracker.Clear(_doc);

            Mode = HandlerMode.Idle;
            _exportQueue = null;

            var finished = ExportFinished;
            if (finished != null) finished(cancelled, manifest);
        }

        private void Report(string verb)
        {
            var progress = Progress;
            if (progress != null) progress(_done, _total, verb);
        }

        /// <summary>
        /// Identifies the model so a ledger written by another project in the same folder is
        /// not mistaken for ours. ProjectInformation's UniqueId survives Save As and moves.
        /// </summary>
        private static string ModelId(Document doc)
        {
            try
            {
                var info = doc.ProjectInformation;
                if (info != null && !string.IsNullOrEmpty(info.UniqueId)) return info.UniqueId;
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Model identity unavailable, falling back to the file path: " + ex.Message);
            }

            return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        }
    }
}
