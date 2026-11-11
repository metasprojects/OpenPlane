using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenPlane.Core
{
    /// <summary>Turns plane and craft names into file names that are safe on Windows, Linux and macOS.</summary>
    public static class FileNames
    {
        private static readonly HashSet<char> Invalid = new HashSet<char>(
            System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }));

        /// <summary>Windows device names: "CON", "nul.txt" and the like can't be ordinary files there.</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        };

        /// <summary>
        /// A file name (without extension) for <paramref name="name"/>: forbidden and control characters become '_', runs of
        /// spaces shrink, it is at most 80 characters, never ends in a dot or space, and never is a Windows device name
        /// (those get a trailing '_'). Empty names become <paramref name="fallback"/>.
        /// </summary>
        public static string Sanitize(string? name, string fallback = "Plane")
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "") sb.Append(Invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var s = sb.ToString();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Trim().TrimStart('.');
            if (s.Length > 80) s = s.Substring(0, 80);
            s = s.TrimEnd(' ', '.');
            if (s.Length == 0) return fallback;
            // "CON", "con.backup" and "COM1 " are all the device on Windows: what counts is the part before the first dot.
            int dot = s.IndexOf('.');
            var stem = (dot >= 0 ? s.Substring(0, dot) : s).TrimEnd(' ');
            if (Reserved.Contains(stem)) s = dot >= 0 ? stem + "_" + s.Substring(dot) : s + "_";
            return s;
        }
    }
}
