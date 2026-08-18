using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    public sealed class ExportResult
    {
        public bool Success;
        /// <summary>Filenames written, relative to the output folder.</summary>
        public readonly List<string> Files = new List<string>();
        /// <summary>Shown in the grid and written to the manifest. Plain English.</summary>
        public string Message = string.Empty;
    }

    /// <summary>
    /// Writes one sheet at a time.
    ///
    /// One sheet per call is not a naive loop, it is the only way to control the filename.
    /// With <c>PDFExportOptions.Combine = false</c> Revit names files by its own rules and
    /// ignores <c>FileName</c> entirely, so the ledger could not match a sheet to a file.
    /// </summary>
    public sealed class ExportEngine
    {
        private readonly ExportSettings _settings;

        public ExportEngine(ExportSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Exports one sheet in every requested format. Must be called on the Revit API
        /// thread. Deliberately not wrapped in a Transaction: Document.Export is a read,
        /// and a transaction around it adds failure modes and nothing else.
        /// </summary>
        public ExportResult ExportSheet(Document doc, SheetRow row)
        {
            var result = new ExportResult { Success = true };
            var notes = new List<string>();

            foreach (var format in _settings.Formats())
            {
                try
                {
                    var extension = format == "PDF" ? ".pdf" : ".dwg";
                    var stem = row.TargetName;
                    var target = Path.Combine(_settings.OutputFolder, stem + extension);

                    if (PathSafety.IsLocked(target))
                    {
                        // Someone has the previous export open. Overwriting is impossible, and
                        // silently skipping would leave a stale file that the ledger then calls
                        // current. Write alongside it and say so.
                        stem = stem + "_NEW";
                        target = Path.Combine(_settings.OutputFolder, stem + extension);
                        notes.Add(Path.GetFileName(row.TargetName + extension) +
                                  " is open in another program, so " + Path.GetFileName(target) + " was written instead");
                    }

                    var written = format == "PDF"
                        ? ExportPdf(doc, row, stem)
                        : ExportDwg(doc, row, stem);

                    if (!written || !File.Exists(target))
                    {
                        result.Success = false;
                        notes.Add(format + " export produced no file. Check the paper format setting; " +
                                  "Revit is documented to do nothing silently when Paper Format is Default " +
                                  "and other layout options are set.");
                        continue;
                    }

                    result.Files.Add(Path.GetFileName(target));
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    notes.Add(format + " export failed: " + ex.Message);
                    Log.Instance.Error("Export failed for sheet " + row.SheetNumber + " (" + format + ").", ex);
                }
            }

            if (result.Files.Count == 0 && result.Success)
            {
                result.Success = false;
                notes.Add("no format was selected");
            }

            result.Message = notes.Count == 0 ? "Exported" : string.Join(" ", notes);
            return result;
        }

        private bool ExportPdf(Document doc, SheetRow row, string stem)
        {
            var options = new PDFExportOptions
            {
                // True with a single sheet id is what makes FileName authoritative.
                Combine = true,
                FileName = stem,
                StopOnError = true,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true
            };

            ApplyPdfOptions(options);

            return doc.Export(_settings.OutputFolder, new List<ElementId> { row.SheetId }, options);
        }

        /// <summary>
        /// Layout options are only applied once a real paper format has been chosen.
        /// With PaperFormat left at Default, setting placement, zoom or margin is documented
        /// to make the export do nothing at all and throw no exception, which would look to
        /// the caller exactly like a successful export of an empty file.
        /// </summary>
        private void ApplyPdfOptions(PDFExportOptions options)
        {
            if (string.IsNullOrWhiteSpace(_settings.PaperFormat) ||
                string.Equals(_settings.PaperFormat, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ExportPaperFormat format;
            if (!Enum.TryParse(_settings.PaperFormat, true, out format))
            {
                Log.Instance.Warn("Paper format '" + _settings.PaperFormat +
                                  "' is not a value this Revit build knows. Leaving it at Default.");
                return;
            }

            options.PaperFormat = format;
            options.PaperPlacement = PaperPlacementType.Center;
            options.ZoomType = ZoomType.Zoom;
            options.ZoomPercentage = 100;
        }

        private bool ExportDwg(Document doc, SheetRow row, string stem)
        {
            var options = new DWGExportOptions();

            if (!string.IsNullOrWhiteSpace(_settings.DwgSetupName))
            {
                try
                {
                    options = DWGExportOptions.GetPredefinedOptions(doc, _settings.DwgSetupName) ?? new DWGExportOptions();
                }
                catch (Exception ex)
                {
                    // A missing named setup is a configuration mistake, not a reason to stop.
                    // Revit's defaults still produce a valid DWG.
                    Log.Instance.Warn("DWG setup '" + _settings.DwgSetupName + "' not found; using defaults. " + ex.Message);
                }
            }

            options.MergedViews = true;

            return doc.Export(_settings.OutputFolder, stem, new List<ElementId> { row.SheetId }, options);
        }
    }
}
