using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Writes the run's checklist next to the drawings: one row per sheet, a tick for what
    /// is current, and the reason behind every decision. This file is the proof that the
    /// skipped sheets were skipped on purpose.
    /// </summary>
    internal static class ManifestWriter
    {
        public const string FileName = "SmartySheets_Manifest.csv";

        public static string Write(string outputFolder, IEnumerable<SheetRow> rows, ExportSettings settings,
                                   DateTime startedLocal, string modelName, bool cancelled)
        {
            var path = Path.Combine(outputFolder, FileName);
            var sb = new StringBuilder();

            sb.AppendLine("Sheet Number,Sheet Name,Current,State,Why,Files,Result");

            var exported = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var row in rows)
            {
                var isCurrent = row.State == SheetState.Unchanged || row.Result == "Exported";

                if (row.Result == "Exported") exported++;
                else if (row.State == SheetState.Unchanged) skipped++;
                else if (!string.IsNullOrEmpty(row.Result)) failed++;

                sb.Append(Escape(row.SheetNumber)).Append(',')
                  .Append(Escape(row.SheetName)).Append(',')
                  .Append(isCurrent ? "✔" : "").Append(',')
                  .Append(Escape(row.State.ToString())).Append(',')
                  .Append(Escape(row.Why)).Append(',')
                  .Append(Escape(string.Join(" | ", row.WrittenFiles))).Append(',')
                  .Append(Escape(row.Result))
                  .AppendLine();
            }

            sb.AppendLine();
            sb.Append("Run,").Append(Escape(startedLocal.ToString("yyyy-MM-dd HH:mm"))).AppendLine();
            sb.Append("Model,").Append(Escape(modelName)).AppendLine();
            sb.Append("Formats,").Append(Escape(string.Join(" + ", settings.Formats()))).AppendLine();
            sb.Append("Deep scan,").Append(settings.DeepScan ? "on" : "off").AppendLine();
            sb.Append("Exported,").Append(exported).AppendLine();
            sb.Append("Skipped as unchanged,").Append(skipped).AppendLine();
            sb.Append("Failed,").Append(failed).AppendLine();
            if (cancelled) sb.AppendLine("Note,Run was cancelled before every selected sheet was written");

            // The BOM is not optional. Without it Excel opens the file as ANSI and the tick
            // arrives as mojibake, which is exactly the column people rely on.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            Log.Instance.Info("Manifest written: " + path + " (" + exported + " exported, " + skipped +
                              " skipped, " + failed + " failed).");
            return path;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
