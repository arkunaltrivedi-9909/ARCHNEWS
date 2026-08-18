using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// A content hash for one sheet: everything that can change what the exported page
    /// looks like, flattened into a deterministic string and reduced to SHA-256.
    ///
    /// Read the catch blocks before changing anything here. Each one appends a fresh Guid,
    /// so a section that fails to read produces a *different* hash every time and the sheet
    /// re-exports for ever until the failure is fixed. That is deliberate. A catch that
    /// appends nothing would make an unreadable sheet look identical to itself and the
    /// sheet would be skipped, which is the one outcome this tool may not produce.
    /// </summary>
    internal static class Fingerprint
    {
        /// <summary>
        /// Parameters Revit rewrites on save or on worksharing operations. Including them
        /// would mark every sheet dirty after a plain save and reduce the tool to a slow
        /// way of exporting everything.
        /// </summary>
        private static readonly HashSet<BuiltInParameter> VolatileParameters = new HashSet<BuiltInParameter>
        {
            BuiltInParameter.EDITED_BY,
            BuiltInParameter.ELEM_PARTITION_PARAM
        };

        private static readonly HashSet<string> VolatileParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Edited by", "Workset", "Design Option"
        };

        /// <summary>Must be called on the Revit API thread.</summary>
        public static string Compute(Document doc, ViewSheet sheet, bool deepScan)
        {
            var sb = new StringBuilder(4096);

            Section(sb, "sheet", () => AppendSheetParameters(doc, sheet, sb));
            Section(sb, "titleblock", () => AppendTitleBlocks(doc, sheet, sb));
            Section(sb, "revisions", () => AppendRevisions(doc, sheet, sb));
            Section(sb, "viewports", () => AppendViewports(doc, sheet, sb, deepScan));
            Section(sb, "schedules", () => AppendScheduleInstances(doc, sheet, sb));
            Section(sb, "clouds", () => AppendRevisionClouds(doc, sheet.Id, sb));

            return Sha256(sb.ToString());
        }

        private static void Section(StringBuilder sb, string name, Action body)
        {
            sb.Append("\n[").Append(name).Append("]\n");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                // Unreadable section, so we cannot claim the sheet is unchanged. The Guid
                // guarantees a different hash on every run until this stops throwing.
                sb.Append("UNREADABLE:").Append(Guid.NewGuid().ToString("N")).Append('\n');
                Log.Instance.Error("Fingerprint section '" + name + "' failed; sheet forced dirty.", ex);
            }
        }

        internal static void AppendSheetParameters(Document doc, ViewSheet sheet, StringBuilder sb)
        {
            sb.Append("number=").Append(sheet.SheetNumber).Append('\n');
            sb.Append("name=").Append(sheet.Name).Append('\n');
            sb.Append("placeholder=").Append(sheet.IsPlaceholder ? '1' : '0').Append('\n');
            AppendParameters(sheet, sb);
        }

        private static void AppendTitleBlocks(Document doc, ViewSheet sheet, StringBuilder sb)
        {
            var blocks = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .ToElements()
                .OrderBy(e => e.Id.Value);

            foreach (var block in blocks)
            {
                sb.Append("tb=").Append(block.Id.Value)
                  .Append(" type=").Append(block.GetTypeId().Value).Append('\n');
                AppendLocation(block, sb);
                AppendParameters(block, sb);
            }
        }

        private static void AppendRevisions(Document doc, ViewSheet sheet, StringBuilder sb)
        {
            var current = sheet.GetCurrentRevision();
            var hasCurrent = current != null && current != ElementId.InvalidElementId;
            sb.Append("currentRev=").Append(hasCurrent ? current.Value.ToString() : "none").Append('\n');

            var ids = sheet.GetAllRevisionIds();
            if (ids == null) return;

            foreach (var id in ids.OrderBy(i => i.Value))
            {
                var revision = doc.GetElement(id) as Revision;
                if (revision == null) { sb.Append("rev=").Append(id.Value).Append('\n'); continue; }

                sb.Append("rev=").Append(id.Value)
                  .Append(" seq=").Append(revision.SequenceNumber)
                  .Append(" num=").Append(revision.RevisionNumber)
                  .Append(" date=").Append(revision.RevisionDate)
                  .Append(" desc=").Append(revision.Description)
                  .Append(" issued=").Append(revision.Issued ? '1' : '0')
                  .Append(" by=").Append(revision.IssuedBy)
                  .Append(" to=").Append(revision.IssuedTo)
                  .Append('\n');
            }
        }

        private static void AppendViewports(Document doc, ViewSheet sheet, StringBuilder sb, bool deepScan)
        {
            var viewportIds = sheet.GetAllViewports();
            if (viewportIds == null) return;

            foreach (var viewportId in viewportIds.OrderBy(i => i.Value))
            {
                var viewport = doc.GetElement(viewportId) as Viewport;
                if (viewport == null) continue;

                sb.Append("vp=").Append(viewport.Id.Value)
                  .Append(" view=").Append(viewport.ViewId.Value)
                  .Append(" rot=").Append(viewport.Rotation)
                  .Append(" type=").Append(viewport.GetTypeId().Value)
                  .Append('\n');

                AppendXyz("vpCentre", viewport.GetBoxCenter(), sb);

                var outline = viewport.GetBoxOutline();
                if (outline != null)
                {
                    AppendXyz("vpMin", outline.MinimumPoint, sb);
                    AppendXyz("vpMax", outline.MaximumPoint, sb);
                }

                var view = doc.GetElement(viewport.ViewId) as View;
                if (view != null) AppendView(doc, view, sb, deepScan);
            }
        }

        private static void AppendView(Document doc, View view, StringBuilder sb, bool deepScan)
        {
            sb.Append("view=").Append(view.Id.Value)
              .Append(" name=").Append(view.Name)
              .Append(" scale=").Append(view.Scale)
              .Append(" detail=").Append(view.DetailLevel)
              .Append(" display=").Append(view.DisplayStyle)
              .Append(" template=").Append(view.ViewTemplateId.Value)
              .Append(" discipline=").Append(view.Discipline)
              .Append(" cropActive=").Append(view.CropBoxActive ? '1' : '0')
              .Append(" cropVisible=").Append(view.CropBoxVisible ? '1' : '0')
              .Append('\n');

            if (view.CropBoxActive)
            {
                var crop = view.CropBox;
                if (crop != null)
                {
                    AppendXyz("cropMin", crop.Min, sb);
                    AppendXyz("cropMax", crop.Max, sb);
                }
            }

            AppendAnnotationDigest(doc, view.Id, sb);
            AppendRevisionClouds(doc, view.Id, sb);

            if (deepScan) AppendVisibleElementDigest(doc, view, sb);
        }

        /// <summary>
        /// Everything owned by the view: tags, dimensions, text, detail components. These
        /// carry an OwnerViewId, so they map to their view for free and no visibility scan
        /// is needed to know they affect this sheet.
        /// </summary>
        private static void AppendAnnotationDigest(Document doc, ElementId viewId, StringBuilder sb)
        {
            var owned = new FilteredElementCollector(doc)
                .WherePasses(new ElementOwnerViewFilter(viewId))
                .WhereElementIsNotElementType()
                .ToElements()
                .OrderBy(e => e.Id.Value);

            var digest = new StringBuilder();
            var count = 0;

            foreach (var element in owned)
            {
                count++;
                digest.Append(element.Id.Value).Append(':').Append(element.GetTypeId().Value).Append(':');
                AppendLocationInline(element, digest);
                digest.Append(';');
            }

            sb.Append("annCount=").Append(count).Append(" annHash=").Append(Sha256(digest.ToString())).Append('\n');
        }

        private static void AppendRevisionClouds(Document doc, ElementId ownerViewId, StringBuilder sb)
        {
            var clouds = new FilteredElementCollector(doc, ownerViewId)
                .OfCategory(BuiltInCategory.OST_RevisionClouds)
                .WhereElementIsNotElementType()
                .ToElements()
                .OrderBy(e => e.Id.Value);

            foreach (var cloud in clouds)
            {
                sb.Append("cloud=").Append(cloud.Id.Value);

                var revisionParam = cloud.get_Parameter(BuiltInParameter.REVISION_CLOUD_REVISION);
                if (revisionParam != null) sb.Append(" rev=").Append(revisionParam.AsElementId().Value);

                var box = cloud.get_BoundingBox(null);
                if (box != null) { AppendXyzInline(box.Min, sb); AppendXyzInline(box.Max, sb); }

                sb.Append('\n');
            }
        }

        /// <summary>
        /// The expensive one. Enumerating the visible element ids of a view is what lets a
        /// moved wall three sheets away be attributed correctly. Only the id set and count
        /// are hashed; geometry comparison would cost far more than a re-export.
        /// </summary>
        private static void AppendVisibleElementDigest(Document doc, View view, StringBuilder sb)
        {
            var ids = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .ToElementIds()
                .Select(i => i.Value)
                .OrderBy(v => v);

            var digest = new StringBuilder();
            var count = 0;
            foreach (var id in ids) { count++; digest.Append(id).Append(','); }

            sb.Append("visCount=").Append(count).Append(" visHash=").Append(Sha256(digest.ToString())).Append('\n');
        }

        private static void AppendScheduleInstances(Document doc, ViewSheet sheet, StringBuilder sb)
        {
            var instances = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .OrderBy(s => s.Id.Value);

            foreach (var instance in instances)
            {
                sb.Append("sched=").Append(instance.Id.Value)
                  .Append(" schedule=").Append(instance.ScheduleId.Value)
                  .Append(" rotation=").Append(instance.Rotation);
                AppendXyzInline(instance.Point, sb);
                sb.Append('\n');

                var schedule = doc.GetElement(instance.ScheduleId) as ViewSchedule;
                if (schedule != null) sb.Append("  schedName=").Append(schedule.Name).Append('\n');
            }
        }

        private static void AppendParameters(Element element, StringBuilder sb)
        {
            var rendered = new List<string>();

            foreach (Parameter parameter in element.Parameters)
            {
                try
                {
                    var definition = parameter.Definition;
                    if (definition == null) continue;
                    if (VolatileParameterNames.Contains(definition.Name)) continue;

                    var internalDefinition = definition as InternalDefinition;
                    if (internalDefinition != null && VolatileParameters.Contains(internalDefinition.BuiltInParameter)) continue;

                    rendered.Add("  " + definition.Name + "=" + Render(parameter));
                }
                catch (Exception ex)
                {
                    // One unreadable parameter forces the sheet dirty rather than silently
                    // dropping out of the hash.
                    rendered.Add("  UNREADABLE:" + Guid.NewGuid().ToString("N"));
                    Log.Instance.Warn("Parameter unreadable on element " + element.Id.Value + ": " + ex.Message);
                }
            }

            rendered.Sort(StringComparer.Ordinal);
            foreach (var line in rendered) sb.Append(line).Append('\n');
        }

        private static string Render(Parameter parameter)
        {
            if (!parameter.HasValue) return "<null>";

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    // Rounded because Revit's internal doubles carry noise that would
                    // otherwise report a change on a sheet nobody touched.
                    return parameter.AsDouble().ToString("F6", CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    return parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                default:
                    return "<none>";
            }
        }

        private static void AppendLocation(Element element, StringBuilder sb)
        {
            sb.Append("  loc=");
            AppendLocationInline(element, sb);
            sb.Append('\n');
        }

        private static void AppendLocationInline(Element element, StringBuilder sb)
        {
            var point = element.Location as LocationPoint;
            if (point != null) { AppendXyzInline(point.Point, sb); sb.Append('@').Append(point.Rotation.ToString("F6", CultureInfo.InvariantCulture)); return; }

            var curve = element.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            {
                AppendXyzInline(curve.Curve.GetEndPoint(0), sb);
                AppendXyzInline(curve.Curve.GetEndPoint(1), sb);
                return;
            }

            var box = element.get_BoundingBox(null);
            if (box != null) { AppendXyzInline(box.Min, sb); AppendXyzInline(box.Max, sb); return; }

            sb.Append("<none>");
        }

        private static void AppendXyz(string label, XYZ xyz, StringBuilder sb)
        {
            sb.Append(label).Append('=');
            AppendXyzInline(xyz, sb);
            sb.Append('\n');
        }

        private static void AppendXyzInline(XYZ xyz, StringBuilder sb)
        {
            if (xyz == null) { sb.Append("(null)"); return; }
            sb.Append('(')
              .Append(xyz.X.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(xyz.Y.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(xyz.Z.ToString("F6", CultureInfo.InvariantCulture)).Append(')');
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
