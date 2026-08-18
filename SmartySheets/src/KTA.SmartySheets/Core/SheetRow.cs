using System;
using System.Collections.Generic;
using System.ComponentModel;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    public enum SheetState
    {
        /// <summary>Never sent to this folder.</summary>
        New,
        /// <summary>Evidence says something moved. <see cref="SheetRow.Why"/> says what.</summary>
        Changed,
        /// <summary>Ledger says exported, the file is not on disk.</summary>
        MissingOnDisk,
        /// <summary>Could not be verified either way, so it exports.</summary>
        Unknown,
        /// <summary>Provably identical to the last export. Skipped.</summary>
        Unchanged,
        /// <summary>Placeholder sheet. Listed, never exported, has no content.</summary>
        Placeholder
    }

    /// <summary>
    /// One row of the grid: a sheet, what we decided about it, and why.
    /// Built on the Revit API thread and displayed on the same thread, so the
    /// notifications below need no marshalling.
    /// </summary>
    public sealed class SheetRow : INotifyPropertyChanged
    {
        private bool _selected;
        private SheetState _state;
        private string _why = string.Empty;
        private string _result = string.Empty;

        public ElementId SheetId { get; set; }
        public string SheetUniqueId { get; set; }
        public string SheetNumber { get; set; }
        public string SheetName { get; set; }

        /// <summary>Filename stem, without extension, after tokens and collision handling.</summary>
        public string TargetName { get; set; }

        /// <summary>Fingerprint computed during this Analyse. Written to the ledger on success.</summary>
        public string Fingerprint { get; set; }

        /// <summary>Files actually written for this sheet during the current run.</summary>
        public List<string> WrittenFiles { get; } = new List<string>();

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Raise(nameof(Selected)); } }
        }

        public SheetState State
        {
            get { return _state; }
            set { if (_state != value) { _state = value; Raise(nameof(State)); } }
        }

        public string Why
        {
            get { return _why; }
            set { if (_why != value) { _why = value ?? string.Empty; Raise(nameof(Why)); } }
        }

        /// <summary>Outcome of the export pass: blank, "Exported", "Skipped", or an error.</summary>
        public string Result
        {
            get { return _result; }
            set { if (_result != value) { _result = value ?? string.Empty; Raise(nameof(Result)); } }
        }

        public bool IsExportable
        {
            get { return State != SheetState.Unchanged && State != SheetState.Placeholder; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
