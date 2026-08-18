using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KTA.SmartySheets.Core
{
    public sealed class LedgerEntry
    {
        public string SheetNumber { get; set; }
        public string SheetName { get; set; }
        public string Fingerprint { get; set; }

        /// <summary>Filenames written for this sheet, relative to the output folder.</summary>
        public List<string> Files { get; set; } = new List<string>();

        public string ExportedUtc { get; set; }
    }

    public sealed class LedgerData
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Identifies the model this folder's history belongs to.</summary>
        public string ModelId { get; set; }
        public string ModelPath { get; set; }

        /// <summary>
        /// Document version recorded at the end of the last successful run. Fed back to
        /// Revit's document-history API to find what a colleague changed while we were closed.
        /// </summary>
        public string LastDocumentVersionGuid { get; set; }
        public int LastNumberOfSaves { get; set; } = -1;

        /// <summary>Keyed by sheet UniqueId, which survives renames and renumbers.</summary>
        public Dictionary<string, LedgerEntry> Entries { get; set; } = new Dictionary<string, LedgerEntry>();
    }

    /// <summary>
    /// The memory of what was last sent to one output folder. It lives in the folder,
    /// not in AppData, so it travels with the deliverable and a colleague exporting to
    /// the same network folder inherits the history.
    /// </summary>
    public sealed class Ledger
    {
        private const string FolderName = ".smartysheets";
        private const string FileName = "ledger.json";

        private readonly string _outputFolder;
        private LedgerData _data;

        public Ledger(string outputFolder)
        {
            _outputFolder = outputFolder;
            _data = new LedgerData();
        }

        /// <summary>True when this folder had no usable history, so everything reads New.</summary>
        public bool StartedFresh { get; private set; } = true;

        /// <summary>Set when a ledger existed but belonged to a different model.</summary>
        public bool ModelMismatch { get; private set; }

        /// <summary>Set when a ledger existed but could not be parsed.</summary>
        public bool WasCorrupt { get; private set; }

        public string LastDocumentVersionGuid { get { return _data.LastDocumentVersionGuid; } }
        public int LastNumberOfSaves { get { return _data.LastNumberOfSaves; } }

        public string Directory { get { return Path.Combine(_outputFolder, FolderName); } }
        public string FilePath { get { return Path.Combine(Directory, FileName); } }

        public LedgerEntry Find(string sheetUniqueId)
        {
            LedgerEntry entry;
            return _data.Entries.TryGetValue(sheetUniqueId, out entry) ? entry : null;
        }

        public void Load(string modelId, string modelPath)
        {
            _data = new LedgerData { ModelId = modelId, ModelPath = modelPath };
            StartedFresh = true;
            ModelMismatch = false;
            WasCorrupt = false;

            try
            {
                if (!File.Exists(FilePath))
                {
                    Log.Instance.Info("No ledger in " + _outputFolder + "; first run against this folder.");
                    return;
                }

                var parsed = JsonSerializer.Deserialize<LedgerData>(File.ReadAllText(FilePath));

                if (parsed == null || parsed.Entries == null)
                {
                    WasCorrupt = true;
                    Log.Instance.Warn("Ledger parsed to nothing; treating the folder as empty.");
                    return;
                }

                // A ledger from another model would map its sheet ids onto ours and could
                // mark one of our sheets Unchanged that was never exported here. Start fresh.
                if (!string.IsNullOrEmpty(parsed.ModelId) &&
                    !string.Equals(parsed.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    ModelMismatch = true;
                    Log.Instance.Warn("Ledger in " + _outputFolder + " belongs to model '" + parsed.ModelPath +
                                      "'. Starting fresh for '" + modelPath + "'.");
                    return;
                }

                parsed.ModelId = modelId;
                parsed.ModelPath = modelPath;
                _data = parsed;
                StartedFresh = false;
                Log.Instance.Info("Ledger loaded: " + _data.Entries.Count + " sheet(s) of history.");
            }
            catch (Exception ex)
            {
                // A half-written or hand-edited ledger must never crash or, worse, be
                // partially trusted. Everything reads New and re-exports.
                WasCorrupt = true;
                _data = new LedgerData { ModelId = modelId, ModelPath = modelPath };
                Log.Instance.Error("Ledger unreadable; treating the folder as empty.", ex);
            }
        }

        public void RecordSuccess(SheetRow row, IEnumerable<string> files)
        {
            _data.Entries[row.SheetUniqueId] = new LedgerEntry
            {
                SheetNumber = row.SheetNumber,
                SheetName = row.SheetName,
                Fingerprint = row.Fingerprint,
                Files = new List<string>(files),
                ExportedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        /// <summary>
        /// Drops a sheet's history so the next run retries it. A failed export must never
        /// leave behind a record that says the sheet is current.
        /// </summary>
        public void RecordFailure(SheetRow row)
        {
            _data.Entries.Remove(row.SheetUniqueId);
        }

        public void SetDocumentVersion(string versionGuid, int numberOfSaves)
        {
            _data.LastDocumentVersionGuid = versionGuid;
            _data.LastNumberOfSaves = numberOfSaves;
        }

        public void Save()
        {
            try
            {
                var dir = new DirectoryInfo(Directory);
                if (!dir.Exists) dir.Create();

                // Write beside the target and move into place, so a crash mid-write leaves
                // the previous ledger intact rather than a truncated one.
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp,
                    JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));

                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temp, FilePath);

                try { new DirectoryInfo(Directory).Attributes |= FileAttributes.Hidden; }
                catch { /* Hiding the folder is cosmetic; a network share may refuse it. */ }
            }
            catch (Exception ex)
            {
                // Losing the ledger costs one full re-export, which is safe. Do not fail the run.
                Log.Instance.Error("Ledger could not be saved. The next run will re-export everything.", ex);
            }
        }
    }
}
