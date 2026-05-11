using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MuMech.Mcp
{
    // On-demand substring/regex search over <KspDir>/KSP.log. Tails up to
    // a configurable number of bytes by default so a wide-open query doesn't
    // dump megabytes.
    internal static class KspLogFileSearch
    {
        public const int DefaultTailBytes = 8 * 1024 * 1024;  // 8MB

        public sealed class Line
        {
            public long byte_offset;
            public string text;
        }

        public static List<Line> Tail(string substring, Regex regex, int tailBytes, int limit)
        {
            string path = KspLogPath();
            var result = new List<Line>();
            if (!File.Exists(path)) return result;

            long fileLen = new FileInfo(path).Length;
            long start = Math.Max(0, fileLen - tailBytes);

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
                {
                    long off = start;
                    string line;
                    // If we didn't start at the beginning, skip the (probably
                    // partial) first line.
                    if (start > 0) { sr.ReadLine(); }
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) { off += 1; continue; }
                        bool match = true;
                        if (substring != null && line.IndexOf(substring, StringComparison.Ordinal) < 0) match = false;
                        if (match && regex != null && !regex.IsMatch(line)) match = false;
                        if (match)
                        {
                            result.Add(new Line { byte_offset = off, text = line });
                            if (result.Count >= limit) return result;
                        }
                        off += Encoding.UTF8.GetByteCount(line) + 1;  // approximate
                    }
                }
            }
            return result;
        }

        private static string KspLogPath()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            try { root = Path.GetFullPath(root); } catch { }
            return Path.Combine(root, "KSP.log");
        }
    }
}
