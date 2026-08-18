using System;
using System.Reflection;
using Autodesk.Revit.UI;
using KTA.SmartySheets.Core;
using KTA.SmartySheets.UI;

namespace KTA.SmartySheets
{
    /// <summary>
    /// Entry point. Builds the ribbon and, more importantly, starts listening to
    /// DocumentChanged the moment Revit loads, long before anyone opens the window.
    /// Edits made before the tool is ever launched still have to be caught.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        internal const string TabName = "KTA";
        internal const string PanelName = "Sheets";

        internal static DirtyTracker Tracker { get; private set; }
        internal static ExportHandler Handler { get; private set; }
        internal static ExternalEvent Event { get; private set; }
        internal static UIControlledApplication UiControlled { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                UiControlled = application;

                Tracker = new DirtyTracker();
                application.ControlledApplication.DocumentChanged += Tracker.OnDocumentChanged;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;

                Handler = new ExportHandler();
                Event = ExternalEvent.Create(Handler);
                Handler.Bind(Event);

                BuildRibbon(application);

                Log.Instance.Info("KTA Smarty Sheets loaded, version " +
                                  Assembly.GetExecutingAssembly().GetName().Version);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // A failure here means no ribbon tab. Say so in the log, because the journal
                // will only report that the external application returned Failed.
                Log.Instance.Error("Startup failed. The KTA tab will not appear.", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentChanged -= Tracker.OnDocumentChanged;
                application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
                MainWindow.CloseIfOpen();
            }
            catch (Exception ex)
            {
                Log.Instance.Error("Shutdown was not clean.", ex);
            }
            return Result.Succeeded;
        }

        private static void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                // Session evidence for a closed document is dead weight, and its key would
                // collide if the same path is reopened after being edited elsewhere.
                Tracker.Forget(DirtyTracker.KeyFor(e.Document));
                MainWindow.CloseIfOpen();
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Document close handling failed: " + ex.Message);
            }
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Exception)
            {
                // Another KTA add-in already created the tab. Revit throws rather than
                // returning the existing one, and that is fine.
            }

            var panel = application.CreateRibbonPanel(TabName, PanelName);

            var button = new PushButtonData(
                "KTA_SmartySheets",
                "Smarty\nSheets",
                Assembly.GetExecutingAssembly().Location,
                typeof(Commands.ShowSmartySheetsCommand).FullName)
            {
                ToolTip = "Re-export only the sheets that changed.",
                LongDescription =
                    "Compares every sheet against a ledger of what was last sent to the chosen output folder, " +
                    "exports what moved, and writes a CSV checklist proving what landed."
            };

            var pushButton = panel.AddItem(button) as PushButton;
            if (pushButton != null)
            {
                pushButton.LargeImage = IconFactory.Create(32);
                pushButton.Image = IconFactory.Create(16);
            }
        }
    }
}
