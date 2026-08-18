using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KTA.SmartySheets.Core;
using KTA.SmartySheets.UI;

namespace KTA.SmartySheets.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ShowSmartySheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiApp = commandData.Application;
                var uiDoc = uiApp.ActiveUIDocument;

                if (uiDoc == null || uiDoc.Document == null)
                {
                    TaskDialog.Show("Smarty Sheets", "Open a project first, then try again.");
                    return Result.Cancelled;
                }

                var doc = uiDoc.Document;

                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("Smarty Sheets", "This works on a project, not a family. Open a project and try again.");
                    return Result.Cancelled;
                }

                MainWindow.ShowOrActivate(uiApp, doc);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Instance.Error("Command failed.", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
