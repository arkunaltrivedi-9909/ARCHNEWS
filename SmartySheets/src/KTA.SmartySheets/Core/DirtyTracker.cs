using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// What changed in this Revit session, gathered from <c>DocumentChanged</c> from the
    /// moment Revit starts.
    ///
    /// This class exists because <c>Element.VersionGuid</c> cannot answer the question.
    /// Autodesk state it only moves between saves, synchronise-to-central and
    /// reload-latest, so between saves it cannot tell you whether anything changed.
    /// </summary>
    internal sealed class DirtyTracker
    {
        private sealed class DocState
        {
            public readonly HashSet<long> Touched = new HashSet<long>();
            public bool SawDeletions;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, DocState> _byDocument =
            new Dictionary<string, DocState>(StringComparer.OrdinalIgnoreCase);

        public static string KeyFor(Document doc)
        {
            if (doc == null) return string.Empty;
            var path = doc.PathName;
            return string.IsNullOrEmpty(path) ? "unsaved::" + doc.Title : path;
        }

        /// <summary>
        /// Runs on the Revit API thread inside Revit's commit pipeline, on every single
        /// transaction in every open document. It does one cheap thing and it never throws:
        /// an exception escaping here surfaces to the user as a Revit-level failure on an
        /// unrelated command.
        /// </summary>
        public void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsFamilyDocument) return;

                var key = KeyFor(doc);

                lock (_gate)
                {
                    DocState state;
                    if (!_byDocument.TryGetValue(key, out state))
                    {
                        state = new DocState();
                        _byDocument[key] = state;
                    }

                    foreach (var id in e.GetAddedElementIds()) state.Touched.Add(id.Value);
                    foreach (var id in e.GetModifiedElementIds()) state.Touched.Add(id.Value);

                    // A deleted element cannot be asked which views it appeared in, so the
                    // ids are worthless to us. All we can safely record is that it happened.
                    var deleted = e.GetDeletedElementIds();
                    if (deleted != null && deleted.Count > 0) state.SawDeletions = true;
                }
            }
            catch
            {
                // Deliberately total. See the summary above: nothing may escape this method.
                // The cost of losing one event is a false negative risk covered by the
                // fingerprint and document-history evidence sources.
            }
        }

        /// <summary>Ids touched in this session, copied so the caller can iterate freely.</summary>
        public HashSet<long> TouchedIds(Document doc)
        {
            lock (_gate)
            {
                DocState state;
                if (_byDocument.TryGetValue(KeyFor(doc), out state)) return new HashSet<long>(state.Touched);
                return new HashSet<long>();
            }
        }

        public bool SawDeletions(Document doc)
        {
            lock (_gate)
            {
                DocState state;
                return _byDocument.TryGetValue(KeyFor(doc), out state) && state.SawDeletions;
            }
        }

        /// <summary>
        /// Called only after a run in which every exportable sheet was written successfully.
        /// If anything failed, was cancelled or was unticked by the user, the session
        /// evidence must survive, or that sheet silently reads Unchanged next time.
        /// </summary>
        public void Clear(Document doc)
        {
            lock (_gate)
            {
                _byDocument.Remove(KeyFor(doc));
            }
        }

        public void Forget(string key)
        {
            lock (_gate)
            {
                _byDocument.Remove(key);
            }
        }
    }
}
