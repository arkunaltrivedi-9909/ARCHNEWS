using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Everything one Analyse pass needs to carry between batches. Built on the API thread,
    /// consumed on the API thread.
    /// </summary>
    internal sealed class AnalysisContext
    {
        internal Document Doc;
        internal Ledger Ledger;
        internal ExportSettings Settings;

        /// <summary>View ids that own a changed element. A hit here is free evidence.</summary>
        internal readonly HashSet<long> ChangedOwnerViews = new HashSet<long>();

        /// <summary>Changed elements that are not owned by a view, so they need a visibility test.</summary>
        internal readonly List<ElementId> ChangedModelElements = new List<ElementId>();

        /// <summary>Visible-element sets, one per view, computed at most once per run.</summary>
        internal readonly Dictionary<long, HashSet<long>> VisibleCache = new Dictionary<long, HashSet<long>>();

        internal bool DeletionsDetected;
        internal bool EvidenceIncomplete;
        internal string EvidenceIncompleteReason;

        internal string CurrentVersionGuid;
        internal int CurrentNumberOfSaves = -1;

        public List<SheetRow> Rows { get; } = new List<SheetRow>();
        public Queue<SheetRow> Pending { get; } = new Queue<SheetRow>();
        public int Total { get { return Rows.Count; } }
    }

    /// <summary>
    /// Decides, for every sheet, whether it must be exported.
    ///
    /// Three independent evidence sources combined with a logical OR: content fingerprint,
    /// this session's DocumentChanged events, and Revit's document history since the last
    /// run. Never turn this into a confidence score, a heuristic or a vote. Any one source
    /// saying "changed" is the answer, and any source that cannot answer means Unknown,
    /// which exports.
    /// </summary>
    internal sealed class ChangeAnalyzer
    {
        private readonly DirtyTracker _tracker;

        public ChangeAnalyzer(DirtyTracker tracker)
        {
            _tracker = tracker;
        }

        /// <summary>Must be called on the Revit API thread.</summary>
        public AnalysisContext Begin(Document doc, Ledger ledger, ExportSettings settings)
        {
            var ctx = new AnalysisContext { Doc = doc, Ledger = ledger, Settings = settings };

            CollectSessionEvidence(ctx);
            CollectHistoryEvidence(ctx);

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var sheet in sheets)
            {
                var row = new SheetRow
                {
                    SheetId = sheet.Id,
                    SheetUniqueId = sheet.UniqueId,
                    SheetNumber = sheet.SheetNumber,
                    SheetName = sheet.Name
                };
                ctx.Rows.Add(row);
                ctx.Pending.Enqueue(row);
            }

            Log.Instance.Info("Analyse begun: " + ctx.Rows.Count + " sheet(s), deep scan " +
                              (settings.DeepScan ? "on" : "off") + ", " +
                              ctx.ChangedOwnerViews.Count + " changed owner view(s), " +
                              ctx.ChangedModelElements.Count + " changed model element(s).");

            return ctx;
        }

        private void CollectSessionEvidence(AnalysisContext ctx)
        {
            var touched = _tracker.TouchedIds(ctx.Doc);
            if (_tracker.SawDeletions(ctx.Doc)) ctx.DeletionsDetected = true;
            IndexChangedElements(ctx, touched);
        }

        private void CollectHistoryEvidence(AnalysisContext ctx)
        {
            var history = DocumentHistory.Compare(ctx.Doc, ctx.Ledger.LastDocumentVersionGuid);

            ctx.CurrentVersionGuid = history.CurrentVersionGuid;
            ctx.CurrentNumberOfSaves = history.CurrentNumberOfSaves;

            if (history.Failed)
            {
                // We asked what changed since the last run and got no answer. Every sheet
                // with a ledger entry becomes Unknown, and Unknown exports.
                ctx.EvidenceIncomplete = true;
                ctx.EvidenceIncompleteReason = history.FailureReason;
                return;
            }

            if (history.SawDeletions) ctx.DeletionsDetected = true;
            IndexChangedElements(ctx, history.TouchedIds);
        }

        /// <summary>
        /// Splits changed elements into the ones that name their own view and the ones that
        /// need a visibility scan. Tags, dimensions and detail components carry an
        /// OwnerViewId, which maps them to a view for free.
        /// </summary>
        private static void IndexChangedElements(AnalysisContext ctx, IEnumerable<long> ids)
        {
            foreach (var raw in ids)
            {
                var id = new ElementId(raw);

                Element element;
                try { element = ctx.Doc.GetElement(id); }
                catch { element = null; }

                if (element == null)
                {
                    // Present in the changed set but no longer resolvable: a deletion we
                    // cannot attribute to any view.
                    ctx.DeletionsDetected = true;
                    continue;
                }

                try
                {
                    var owner = element.OwnerViewId;
                    if (owner != null && owner != ElementId.InvalidElementId)
                    {
                        ctx.ChangedOwnerViews.Add(owner.Value);
                        continue;
                    }
                }
                catch
                {
                    // Could not read the owner, so treat it as a model element and let the
                    // more expensive visibility test decide. Never drop it.
                }

                // Sheets themselves and views appear here; a changed sheet is caught by its
                // fingerprint, so only genuine model content needs the scan.
                if (element is View) { ctx.ChangedOwnerViews.Add(element.Id.Value); continue; }

                ctx.ChangedModelElements.Add(id);
            }
        }

        /// <summary>
        /// Decides one sheet. Must be called on the Revit API thread. This and
        /// ExportEngine.ExportSheet are the two methods worth a breakpoint.
        /// </summary>
        public void Analyze(AnalysisContext ctx, SheetRow row)
        {
            var sheet = ctx.Doc.GetElement(row.SheetId) as ViewSheet;
            if (sheet == null)
            {
                row.State = SheetState.Unknown;
                row.Why = "the sheet could not be read from the model";
                row.Selected = true;
                return;
            }

            if (sheet.IsPlaceholder)
            {
                row.State = SheetState.Placeholder;
                row.Why = "placeholder sheet, it has no content to export";
                row.Selected = false;
                return;
            }

            row.Fingerprint = Fingerprint.Compute(ctx.Doc, sheet, ctx.Settings.DeepScan);

            var entry = ctx.Ledger.Find(row.SheetUniqueId);
            if (entry == null)
            {
                row.State = SheetState.New;
                row.Why = ctx.Ledger.WasCorrupt ? "the folder's ledger was unreadable, so nothing here is trusted"
                        : ctx.Ledger.ModelMismatch ? "this folder holds another model's history"
                        : "never sent to this folder";
                row.Selected = true;
                return;
            }

            var missing = MissingFiles(ctx, entry);
            if (missing.Count > 0)
            {
                row.State = SheetState.MissingOnDisk;
                row.Why = "the ledger says exported but " + string.Join(", ", missing) + " is not in the folder";
                row.Selected = true;
                return;
            }

            var reasons = new List<string>();

            if (!string.Equals(entry.Fingerprint, row.Fingerprint, StringComparison.Ordinal))
                reasons.Add("sheet content differs from the last export");

            if (ctx.DeletionsDetected)
                reasons.Add("elements were deleted, and a deleted element cannot be traced to the views it was on");

            if (SheetUsesAnyView(ctx, sheet, ctx.ChangedOwnerViews))
                reasons.Add("an annotation changed in a view placed on this sheet");

            if (ctx.ChangedModelElements.Count > 0 && SheetShowsChangedModel(ctx, sheet))
                reasons.Add("model elements visible on this sheet changed");

            if (reasons.Count > 0)
            {
                row.State = SheetState.Changed;
                row.Why = string.Join("; ", reasons);
                row.Selected = true;
                return;
            }

            if (ctx.EvidenceIncomplete)
            {
                row.State = SheetState.Unknown;
                row.Why = ctx.EvidenceIncompleteReason ?? "the change evidence was incomplete";
                row.Selected = true;
                return;
            }

            row.State = SheetState.Unchanged;
            row.Why = "provably identical to the last export";
            row.Selected = false;
        }

        private static List<string> MissingFiles(AnalysisContext ctx, LedgerEntry entry)
        {
            var missing = new List<string>();

            if (entry.Files == null || entry.Files.Count == 0)
            {
                missing.Add("no file was recorded");
                return missing;
            }

            foreach (var file in entry.Files)
            {
                try
                {
                    if (!File.Exists(Path.Combine(ctx.Settings.OutputFolder, file))) missing.Add(file);
                }
                catch (Exception ex)
                {
                    // Cannot see the folder, so cannot prove the file is there.
                    missing.Add(file);
                    Log.Instance.Warn("Could not check '" + file + "': " + ex.Message);
                }
            }

            return missing;
        }

        /// <summary>
        /// True when any view placed on the sheet is in <paramref name="viewIds"/>. Returns
        /// true if the viewports cannot be enumerated: an unanswerable question resolves
        /// toward exporting. Must be called on the Revit API thread.
        /// </summary>
        private static bool SheetUsesAnyView(AnalysisContext ctx, ViewSheet sheet, HashSet<long> viewIds)
        {
            if (viewIds.Count == 0) return false;

            try
            {
                if (viewIds.Contains(sheet.Id.Value)) return true;

                foreach (var viewId in PlacedViewIds(sheet, ctx.Doc))
                    if (viewIds.Contains(viewId.Value)) return true;

                return false;
            }
            catch (Exception ex)
            {
                Log.Instance.Error("Viewports unreadable on sheet " + sheet.SheetNumber + "; forcing export.", ex);
                return true;
            }
        }

        /// <summary>
        /// True when a changed model element is visible in a view placed on the sheet.
        /// With deep scan on this is exact, using the view's visible element set, cached.
        /// With it off it falls back to a per-view bounding box test, which is cheaper and
        /// documented as weaker. Returns true on any failure. Must be called on the Revit
        /// API thread.
        /// </summary>
        private static bool SheetShowsChangedModel(AnalysisContext ctx, ViewSheet sheet)
        {
            try
            {
                foreach (var viewId in PlacedViewIds(sheet, ctx.Doc))
                {
                    var view = ctx.Doc.GetElement(viewId) as View;
                    if (view == null) return true;

                    if (ctx.Settings.DeepScan)
                    {
                        var visible = VisibleSet(ctx, view);
                        if (visible == null) return true;

                        foreach (var id in ctx.ChangedModelElements)
                            if (visible.Contains(id.Value)) return true;
                    }
                    else
                    {
                        foreach (var id in ctx.ChangedModelElements)
                        {
                            var element = ctx.Doc.GetElement(id);
                            if (element == null) continue;
                            if (element.get_BoundingBox(view) != null) return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Instance.Error("Visibility scan failed on sheet " + sheet.SheetNumber + "; forcing export.", ex);
                return true;
            }
        }

        private static HashSet<long> VisibleSet(AnalysisContext ctx, View view)
        {
            HashSet<long> cached;
            if (ctx.VisibleCache.TryGetValue(view.Id.Value, out cached)) return cached;

            try
            {
                var set = new HashSet<long>();
                foreach (var id in new FilteredElementCollector(ctx.Doc, view.Id).WhereElementIsNotElementType().ToElementIds())
                    set.Add(id.Value);

                ctx.VisibleCache[view.Id.Value] = set;
                return set;
            }
            catch (Exception ex)
            {
                // Null tells the caller it could not be answered, and the caller exports.
                // Not cached, so a transient failure does not poison the rest of the run.
                Log.Instance.Error("Could not enumerate view '" + view.Name + "'.", ex);
                return null;
            }
        }

        private static IEnumerable<ElementId> PlacedViewIds(ViewSheet sheet, Document doc)
        {
            var viewportIds = sheet.GetAllViewports();
            if (viewportIds == null) yield break;

            foreach (var viewportId in viewportIds)
            {
                var viewport = doc.GetElement(viewportId) as Viewport;
                if (viewport != null) yield return viewport.ViewId;
            }
        }
    }
}
