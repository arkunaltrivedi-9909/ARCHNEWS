using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KTA.SmartySheets.Core
{
    public sealed class ExportSettings
    {
        public const string DefaultTemplate = "{SheetNumber} - {SheetName} - {Revision}";

        public string OutputFolder { get; set; } = string.Empty;
        public string NameTemplate { get; set; } = DefaultTemplate;
        public bool ExportPdf { get; set; } = true;
        public bool ExportDwg { get; set; } = false;

        /// <summary>
        /// Enumerates the visible elements of every placed view to map a changed model
        /// element to the sheets that show it. Accurate, and the slow part on large models.
        /// Off falls back to a bounding-box test, which is documented as weaker.
        /// </summary>
        public bool DeepScan { get; set; } = true;

        /// <summary>Sheets per API-thread batch. Small keeps Revit responsive and Cancel honest.</summary>
        public int BatchSize { get; set; } = 5;

        /// <summary>Named DWG export setup from the model. Empty means Revit's defaults.</summary>
        public string DwgSetupName { get; set; } = string.Empty;

        /// <summary>
        /// Name of an <c>ExportPaperFormat</c> member, or "Default" to let Revit decide.
        /// Held as a string because the enum's members differ between releases, and an
        /// unknown name should degrade to Default rather than fail to compile.
        /// </summary>
        public string PaperFormat { get; set; } = "Default";

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "KTA", "SmartySheets", "settings.json");
            }
        }

        public static ExportSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<ExportSettings>(File.ReadAllText(SettingsPath));
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.NameTemplate)) loaded.NameTemplate = DefaultTemplate;
                        if (loaded.BatchSize < 1 || loaded.BatchSize > 50) loaded.BatchSize = 5;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt settings are not worth a dialog. Fall back to defaults.
                Log.Instance.Warn("Settings unreadable, using defaults: " + ex.Message);
            }
            return new ExportSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Settings could not be saved: " + ex.Message);
            }
        }

        public IEnumerable<string> Formats()
        {
            if (ExportPdf) yield return "PDF";
            if (ExportDwg) yield return "DWG";
        }
    }
}
