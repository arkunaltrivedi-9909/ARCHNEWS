using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Turns sheet-derived text into something Windows will actually accept as a
    /// filename, and keeps two sheets that resolve to the same name from
    /// overwriting each other.
    /// </summary>
    internal static class PathSafety
    {
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Sheet";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c < 32) sb.Append('_');
                else sb.Append(c);
            }

            // Windows silently strips trailing dots and spaces, which would let two
            // distinct sheets collide without the collision check ever seeing it.
            var name = sb.ToString().Trim().TrimEnd('.', ' ');
            if (name.Length == 0) name = "Sheet";

            if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;

            // 255 is the per-component limit; leave headroom for an extension and a _NN suffix.
            if (name.Length > 180) name = name.Substring(0, 180).TrimEnd('.', ' ');

            return name;
        }

        /// <summary>
        /// Returns a name not yet present in <paramref name="taken"/>, appending _02, _03
        /// and so on. The winner is added to the set.
        /// </summary>
        public static string Deduplicate(string baseName, HashSet<string> taken)
        {
            if (taken.Add(baseName)) return baseName;

            for (var i = 2; i < 1000; i++)
            {
                var candidate = baseName + "_" + i.ToString("00");
                if (taken.Add(candidate)) return candidate;
            }

            var fallback = baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            taken.Add(fallback);
            return fallback;
        }

        /// <summary>
        /// Proves the folder is writable before a run starts, rather than failing on
        /// sheet 1 of 150.
        /// </summary>
        public static bool TryProveWritable(string folder, out string reason)
        {
            reason = null;
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) { reason = "No output folder selected."; return false; }

                System.IO.Directory.CreateDirectory(folder);

                var probe = Path.Combine(folder, ".smartysheets-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                reason = "The output folder is read-only for this account:" + Environment.NewLine + folder;
                return false;
            }
            catch (Exception ex)
            {
                reason = "The output folder cannot be written to:" + Environment.NewLine + folder +
                         Environment.NewLine + Environment.NewLine + ex.Message;
                return false;
            }
        }

        /// <summary>True when the file exists and something else holds it open.</summary>
        public static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
    }
}
