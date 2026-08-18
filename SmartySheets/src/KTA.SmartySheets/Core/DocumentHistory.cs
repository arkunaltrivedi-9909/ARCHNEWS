using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Result of asking Revit what moved between the document version recorded at the end
    /// of the last run and the document as it stands now. This is the evidence source that
    /// covers "you closed Revit, a colleague edited the central model, you reopened".
    /// </summary>
    internal sealed class HistoryResult
    {
        public bool Available;
        public bool Failed;
        public string FailureReason;
        public bool SawDeletions;
        public readonly HashSet<long> TouchedIds = new HashSet<long>();
        public string CurrentVersionGuid;
        public int CurrentNumberOfSaves = -1;
    }

    /// <summary>
    /// The single place that touches Revit's document-version API.
    ///
    /// This is the most release-fragile surface in the add-in and it is isolated here on
    /// purpose: if the member names below have drifted, this is the only file to correct.
    /// See VERIFY.md. The return type is bound as <see cref="ICollection{ElementId}"/> so
    /// it compiles whether Revit hands back a list or a set.
    /// </summary>
    internal static class DocumentHistory
    {
        /// <summary>Must be called on the Revit API thread.</summary>
        public static HistoryResult Compare(Document doc, string baselineVersionGuid)
        {
            var result = new HistoryResult();

            try
            {
                var current = Document.GetDocumentVersion(doc);
                result.CurrentVersionGuid = current.VersionGUID.ToString();
                result.CurrentNumberOfSaves = current.NumberOfSaves;
            }
            catch (Exception ex)
            {
                // Without a current version we cannot store a baseline for next time either,
                // but that only costs a full export later. It is not grounds to fail now.
                Log.Instance.Warn("Document version unavailable: " + ex.Message);
            }

            if (string.IsNullOrEmpty(baselineVersionGuid))
            {
                // No baseline recorded, so history has nothing to say. Not a failure:
                // the ledger's absence already makes these sheets New.
                return result;
            }

            Guid baseline;
            if (!Guid.TryParse(baselineVersionGuid, out baseline))
            {
                result.Failed = true;
                result.FailureReason = "the recorded document version could not be read";
                return result;
            }

            try
            {
                ICollection<ElementId> changed = doc.GetChangedElements(baseline);
                result.Available = true;

                if (changed == null) return result;

                foreach (var id in changed)
                {
                    // GetChangedElements returns created, modified and deleted ids alike.
                    // A deleted id no longer resolves, and a deleted element cannot be asked
                    // which views it appeared in, so it is recorded only as a fact.
                    if (doc.GetElement(id) == null) result.SawDeletions = true;
                    else result.TouchedIds.Add(id.Value);
                }
            }
            catch (Exception ex)
            {
                // We asked a question we could not get an answer to. Saying so makes every
                // sheet Unknown, which exports. That is the correct direction to fail in.
                result.Failed = true;
                result.FailureReason = "Revit could not compare against the last recorded document version";
                Log.Instance.Error("GetChangedElements failed against baseline " + baselineVersionGuid, ex);
            }

            return result;
        }
    }
}
