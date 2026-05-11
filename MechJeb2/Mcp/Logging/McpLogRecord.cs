using System;
using System.Globalization;
using UnityEngine;

namespace MuMech.Mcp
{
    // Captured log record. Struct (not class) so the fixed-size ring buffer
    // has zero per-record allocation.
    public struct McpLogRecord
    {
        public long Seq;
        public long Ticks;        // DateTime.UtcNow.Ticks at capture
        public LogType Level;
        public string Message;
        public string StackTrace; // only set for Error/Exception/Assert levels
    }

    internal static class LogRecordJson
    {
        // Heuristic module/tag extraction at READ time. Matches "[MechJeb*]"
        // prefix in message; returns "MechJeb" or "MechJeb<rest>" if found.
        // Returns null otherwise. Cheap — single-substring scan.
        public static string ExtractTag(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            int open = message.IndexOf('[');
            if (open != 0) return null;
            int close = message.IndexOf(']', 1);
            if (close <= 1) return null;
            string content = message.Substring(1, close - 1);
            if (content.StartsWith("MechJeb", StringComparison.Ordinal)) return content;
            return null;
        }

        public static JsonObject ToJson(in McpLogRecord r, int messageLimit)
        {
            var o = new JsonObject()
                .Set("seq", r.Seq)
                .Set("ts", new JsonString(new DateTime(r.Ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)))
                .Set("level", r.Level.ToString());
            string tag = ExtractTag(r.Message);
            if (tag != null) o.Set("tag", tag);
            string msg = r.Message ?? "";
            if (messageLimit > 0 && msg.Length > messageLimit) msg = msg.Substring(0, messageLimit) + "…[truncated]";
            o.Set("message", msg);
            if (!string.IsNullOrEmpty(r.StackTrace)) o.Set("stack_trace", r.StackTrace);
            return o;
        }
    }
}
