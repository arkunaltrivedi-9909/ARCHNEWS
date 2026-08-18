using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KTA.SmartySheets.Core;

namespace KTA.SmartySheets.UI
{
    public partial class MainWindow : Window
    {
        private static MainWindow _instance;

        private readonly Document _doc;
        private readonly ExportSettings _settings;
        private readonly ChangeAnalyzer _analyzer;
        private readonly ObservableCollection<SheetRow> _rows = new ObservableCollection<SheetRow>();

        private AnalysisContext _context;

        private MainWindow(UIApplication uiApp, Document doc)
        {
            InitializeComponent();

            _doc = doc;
            _settings = ExportSettings.Load();
            _analyzer = new ChangeAnalyzer(App.Tracker);

            SheetGrid.ItemsSource = _rows;

            try
            {
                // Owning the window to Revit keeps it above the main frame and lets it
                // minimise with Revit instead of drifting behind it.
                new WindowInteropHelper(this) { Owner = uiApp.MainWindowHandle };
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Window could not be owned by Revit: " + ex.Message);
            }

            PopulatePaperFormats();
            LoadSettingsIntoUi();
            WireHandler();
        }

        /// <summary>
        /// One window per session. A second click brings the existing one forward rather
        /// than opening a rival copy with its own idea of what has been exported.
        /// </summary>
        public static void ShowOrActivate(UIApplication uiApp, Document doc)
        {
            if (_instance != null)
            {
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new MainWindow(uiApp, doc);
            _instance.Show();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null) return;
            try { _instance.Close(); }
            catch (Exception ex) { Log.Instance.Warn("Window would not close: " + ex.Message); }
        }

        private void WireHandler()
        {
            App.Handler.Progress = OnProgress;
            App.Handler.AnalyseFinished = OnAnalyseFinished;
            App.Handler.ExportFinished = OnExportFinished;
            App.Handler.Failed = OnHandlerFailed;
        }

        private void PopulatePaperFormats()
        {
            try
            {
                // Read from the running Revit rather than a hard-coded list: the members of
                // ExportPaperFormat differ between releases.
                foreach (var name in Enum.GetNames(typeof(ExportPaperFormat))) PaperFormatBox.Items.Add(name);
            }
            catch (Exception ex)
            {
                PaperFormatBox.Items.Add("Default");
                Log.Instance.Warn("Paper formats could not be listed: " + ex.Message);
            }
        }

        private void LoadSettingsIntoUi()
        {
            FolderBox.Text = _settings.OutputFolder;
            TemplateBox.Text = _settings.NameTemplate;
            PdfBox.IsChecked = _settings.ExportPdf;
            DwgBox.IsChecked = _settings.ExportDwg;
            DeepScanBox.IsChecked = _settings.DeepScan;
            BatchBox.Text = _settings.BatchSize.ToString(CultureInfo.InvariantCulture);

            PaperFormatBox.SelectedItem = PaperFormatBox.Items.Contains(_settings.PaperFormat)
                ? _settings.PaperFormat
                : (PaperFormatBox.Items.Count > 0 ? PaperFormatBox.Items[0] : null);
        }

        private void PullSettingsFromUi()
        {
            _settings.OutputFolder = FolderBox.Text.Trim();
            _settings.NameTemplate = TemplateBox.Text;
            _settings.ExportPdf = PdfBox.IsChecked == true;
            _settings.ExportDwg = DwgBox.IsChecked == true;
            _settings.DeepScan = DeepScanBox.IsChecked == true;
            _settings.PaperFormat = PaperFormatBox.SelectedItem as string ?? "Default";

            int batch;
            if (int.TryParse(BatchBox.Text, out batch) && batch >= 1 && batch <= 50) _settings.BatchSize = batch;
            else BatchBox.Text = _settings.BatchSize.ToString(CultureInfo.InvariantCulture);
        }

        private void OnAnalyse(object sender, RoutedEventArgs e)
        {
            PullSettingsFromUi();

            string reason;
            if (!PathSafety.TryProveWritable(_settings.OutputFolder, out reason))
            {
                Say(reason);
                return;
            }

            if (!_settings.ExportPdf && !_settings.ExportDwg)
            {
                Say("Tick at least one format, PDF or DWG.");
                return;
            }

            _settings.Save();
            _rows.Clear();
            _context = null;

            SetBusy(true, "Analysing…");
            App.Handler.QueueAnalyse(_doc, _settings, _analyzer);
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (_context == null) { Say("Analyse first."); return; }

            var selected = _rows.Count(r => r.Selected && r.IsExportable);
            if (selected == 0) { Say("Nothing is ticked, so there is nothing to export."); return; }

            PullSettingsFromUi();
            _settings.Save();

            SetBusy(true, "Exporting…");
            App.Handler.QueueExport(_settings);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            App.Handler.RequestCancel();
            StatusText.Text = "Stopping after the current batch…";
        }

        private void OnProgress(int done, int total, string verb)
        {
            Progress.Maximum = Math.Max(1, total);
            Progress.Value = done;
            StatusText.Text = verb + " " + done + " of " + total;
        }

        private void OnAnalyseFinished(AnalysisContext context)
        {
            _context = context;

            _rows.Clear();
            foreach (var row in context.Rows) _rows.Add(row);

            var changed = _rows.Count(r => r.Selected && r.IsExportable);
            var unchanged = _rows.Count(r => r.State == SheetState.Unchanged);
            var placeholders = _rows.Count(r => r.State == SheetState.Placeholder);

            SetBusy(false, changed + " to export, " + unchanged + " unchanged" +
                          (placeholders > 0 ? ", " + placeholders + " placeholder" : "") +
                          ", " + _rows.Count + " sheets total.");

            ExportButton.IsEnabled = changed > 0;
        }

        private void OnExportFinished(bool cancelled, string manifestPath)
        {
            var exported = _rows.Count(r => r.Result == "Exported");
            var failed = _rows.Count(r => !string.IsNullOrEmpty(r.Result) && r.Result != "Exported");

            var summary = exported + " exported" +
                          (failed > 0 ? ", " + failed + " failed" : "") +
                          (cancelled ? ", run cancelled" : "");

            if (!string.IsNullOrEmpty(manifestPath)) summary += ". Checklist: " + Path.GetFileName(manifestPath);

            SetBusy(false, summary);
            ExportButton.IsEnabled = _rows.Any(r => r.Selected && r.IsExportable);

            if (failed > 0)
            {
                Say("Some sheets did not export. Their rows show why, and their history was cleared " +
                    "so the next run retries them." + Environment.NewLine + Environment.NewLine +
                    "Full detail: " + Log.Directory);
            }
        }

        private void OnHandlerFailed(string message)
        {
            SetBusy(false, "Stopped.");
            Say(message);
        }

        private void SetBusy(bool busy, string status)
        {
            AnalyseButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy && _context != null;
            CancelButton.IsEnabled = busy;
            FolderBox.IsEnabled = !busy;
            TemplateBox.IsEnabled = !busy;
            StatusText.Text = status;
            if (!busy) Progress.Value = Progress.Maximum;
        }

        private void OnSelectChanged(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.Selected = row.IsExportable && row.State != SheetState.Unchanged;
            ExportButton.IsEnabled = _context != null && _rows.Any(r => r.Selected);
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.Selected = false;
            ExportButton.IsEnabled = false;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the output folder",
                Multiselect = false
            };

            if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;

            if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
        }

        private void OnFolderChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // The ledger belongs to a folder, so pointing somewhere else invalidates the
            // analysis on screen. Clearing it is safer than leaving stale states visible.
            if (_context == null) return;

            _context = null;
            _rows.Clear();
            ExportButton.IsEnabled = false;
            StatusText.Text = "Folder changed. Analyse again.";
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            OpenInExplorer(FolderBox.Text.Trim());
        }

        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            OpenInExplorer(Log.Directory);
        }

        private static void OpenInExplorer(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Could not open '" + path + "': " + ex.Message);
            }
        }

        private void OnShowTokens(object sender, RoutedEventArgs e)
        {
            var dialog = new TaskDialog("Name template tokens")
            {
                MainInstruction = "Tokens you can use",
                MainContent = string.Join(Environment.NewLine, NameTemplate.KnownTokens) + Environment.NewLine +
                              Environment.NewLine +
                              "Empty tokens collapse with the punctuation around them, so an unrevised " +
                              "sheet gives A101 - Ground Floor Plan rather than A101 - Ground Floor Plan - ."
            };
            dialog.Show();
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (App.Handler.IsBusy)
            {
                // Closing mid-run would orphan the callbacks the handler still holds.
                App.Handler.RequestCancel();
                e.Cancel = true;
                StatusText.Text = "Stopping after the current batch. Close again once it has finished.";
                return;
            }

            PullSettingsFromUi();
            _settings.Save();

            App.Handler.Progress = null;
            App.Handler.AnalyseFinished = null;
            App.Handler.ExportFinished = null;
            App.Handler.Failed = null;

            _instance = null;
            base.OnClosing(e);
        }

        private void Say(string message)
        {
            var dialog = new TaskDialog("KTA Smarty Sheets") { MainContent = message };
            dialog.Show();
        }
    }

    /// <summary>Colours the State column so a grid of 150 rows can be read at a glance.</summary>
    public sealed class StateBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush New = new SolidColorBrush(Color.FromRgb(0x1B, 0x6A, 0xC6));
        private static readonly SolidColorBrush Changed = new SolidColorBrush(Color.FromRgb(0xB4, 0x5F, 0x06));
        private static readonly SolidColorBrush Missing = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
        private static readonly SolidColorBrush Unknown = new SolidColorBrush(Color.FromRgb(0x7B, 0x3F, 0xA8));
        private static readonly SolidColorBrush Unchanged = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x5C));
        private static readonly SolidColorBrush Placeholder = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0x9E));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is SheetState)) return Unchanged;

            switch ((SheetState)value)
            {
                case SheetState.New: return New;
                case SheetState.Changed: return Changed;
                case SheetState.MissingOnDisk: return Missing;
                case SheetState.Unknown: return Unknown;
                case SheetState.Placeholder: return Placeholder;
                default: return Unchanged;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
