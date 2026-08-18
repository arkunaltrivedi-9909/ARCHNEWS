using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Turns a template such as <c>{SheetNumber} - {SheetName} - {Revision}</c> into a
    /// filename. Empty tokens collapse together with the punctuation that was holding
    /// them, so an unrevised sheet gives <c>A101 - Ground Floor Plan</c> rather than
    /// <c>A101 - Ground Floor Plan - </c>.
    /// </summary>
    internal static class NameTemplate
    {
        private static readonly Regex TokenPattern = new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);
        private static readonly char[] Glue = { ' ', '-', '_', '–', '—', ',' };

        public static readonly string[] KnownTokens =
        {
            "{SheetNumber}", "{SheetName}", "{Revision}", "{RevisionDate}", "{RevisionDescription}",
            "{ProjectNumber}", "{ProjectName}", "{ClientName}", "{Discipline}", "{ModelName}",
            "{User}", "{Date:yyyy-MM-dd}", "{Param:Any Parameter Name}"
        };

        /// <summary>
        /// Values that do not vary per sheet, resolved once per run. Must be built on the
        /// Revit API thread.
        /// </summary>
        internal sealed class ProjectTokens
        {
            public string ProjectNumber = string.Empty;
            public string ProjectName = string.Empty;
            public string ClientName = string.Empty;
            public string ModelName = string.Empty;
            public string User = string.Empty;

            public static ProjectTokens From(Document doc)
            {
                var tokens = new ProjectTokens();
                try
                {
                    var info = doc.ProjectInformation;
                    if (info != null)
                    {
                        tokens.ProjectNumber = info.Number ?? string.Empty;
                        tokens.ProjectName = info.Name ?? string.Empty;
                        tokens.ClientName = info.ClientName ?? string.Empty;
                    }

                    tokens.ModelName = string.IsNullOrEmpty(doc.PathName)
                        ? doc.Title
                        : Path.GetFileNameWithoutExtension(doc.PathName);

                    tokens.User = doc.Application.Username ?? string.Empty;
                }
                catch (Exception ex)
                {
                    // A blank token is a cosmetic problem in a filename, not a correctness
                    // one, so this does not force anything dirty.
                    Log.Instance.Warn("Project tokens partly unresolved: " + ex.Message);
                }
                return tokens;
            }
        }

        /// <summary>Must be called on the Revit API thread.</summary>
        public static string Resolve(string template, Document doc, ViewSheet sheet, ProjectTokens project)
        {
            if (string.IsNullOrWhiteSpace(template)) template = ExportSettings.DefaultTemplate;

            var built = new StringBuilder(template.Length + 32);
            var lastIndex = 0;
            var previousTokenWasEmpty = false;

            foreach (Match match in TokenPattern.Matches(template))
            {
                var literal = template.Substring(lastIndex, match.Index - lastIndex);
                if (previousTokenWasEmpty) literal = literal.TrimStart(Glue);
                built.Append(literal);
                previousTokenWasEmpty = false;

                var value = ResolveToken(match.Groups[1].Value, doc, sheet, project);

                if (string.IsNullOrWhiteSpace(value))
                {
                    TrimEnd(built);
                    previousTokenWasEmpty = true;
                }
                else
                {
                    built.Append(value.Trim());
                }

                lastIndex = match.Index + match.Length;
            }

            var tail = template.Substring(lastIndex);
            if (previousTokenWasEmpty) tail = tail.TrimStart(Glue);
            built.Append(tail);

            TrimEnd(built);
            var result = built.ToString().TrimStart(Glue);

            return PathSafety.SanitizeFileName(result);
        }

        private static void TrimEnd(StringBuilder sb)
        {
            while (sb.Length > 0 && Array.IndexOf(Glue, sb[sb.Length - 1]) >= 0) sb.Length--;
        }

        private static string ResolveToken(string token, Document doc, ViewSheet sheet, ProjectTokens project)
        {
            try
            {
                if (token.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                    return DateTime.Now.ToString(token.Substring(5));

                if (token.StartsWith("Param:", StringComparison.OrdinalIgnoreCase))
                    return LookupParameter(doc, sheet, token.Substring(6).Trim());

                switch (token.ToLowerInvariant())
                {
                    case "sheetnumber": return sheet.SheetNumber;
                    case "sheetname": return sheet.Name;
                    case "projectnumber": return project.ProjectNumber;
                    case "projectname": return project.ProjectName;
                    case "clientname": return project.ClientName;
                    case "modelname": return project.ModelName;
                    case "user": return project.User;
                    case "discipline":
                    {
                        // LookupParameter returns an empty string rather than null, so a
                        // plain ?? would never reach the second name.
                        var discipline = LookupParameter(doc, sheet, "Sheet Discipline");
                        return string.IsNullOrEmpty(discipline) ? LookupParameter(doc, sheet, "Discipline") : discipline;
                    }
                    case "revision": return CurrentRevision(doc, sheet, r => r.RevisionNumber);
                    case "revisiondate": return CurrentRevision(doc, sheet, r => r.RevisionDate);
                    case "revisiondescription": return CurrentRevision(doc, sheet, r => r.Description);
                    default:
                        Log.Instance.Warn("Unknown token '{" + token + "}' left blank.");
                        return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.Instance.Warn("Token '{" + token + "}' could not be resolved: " + ex.Message);
                return string.Empty;
            }
        }

        private static string CurrentRevision(Document doc, ViewSheet sheet, Func<Revision, string> pick)
        {
            var id = sheet.GetCurrentRevision();
            if (id == null || id == ElementId.InvalidElementId) return string.Empty;

            var revision = doc.GetElement(id) as Revision;
            return revision == null ? string.Empty : (pick(revision) ?? string.Empty);
        }

        private static string LookupParameter(Document doc, ViewSheet sheet, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var value = Read(sheet.LookupParameter(name));
            if (!string.IsNullOrEmpty(value)) return value;

            var info = doc.ProjectInformation;
            return info == null ? string.Empty : Read(info.LookupParameter(name));
        }

        private static string Read(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return string.Empty;

            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString() ?? string.Empty;
                case StorageType.Integer: return parameter.AsInteger().ToString();
                case StorageType.Double: return parameter.AsValueString() ?? parameter.AsDouble().ToString("0.###");
                case StorageType.ElementId: return parameter.AsValueString() ?? string.Empty;
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Assigns every row a filename stem and resolves collisions with _02, _03 and so on,
        /// so two sheets that produce the same name both survive. Must be called on the
        /// Revit API thread.
        /// </summary>
        public static void AssignNames(Document doc, IEnumerable<SheetRow> rows, string template)
        {
            var project = ProjectTokens.From(doc);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var sheet = doc.GetElement(row.SheetId) as ViewSheet;
                var stem = sheet == null
                    ? PathSafety.SanitizeFileName(row.SheetNumber + " - " + row.SheetName)
                    : Resolve(template, doc, sheet, project);

                row.TargetName = PathSafety.Deduplicate(stem, taken);
            }
        }
    }
}
