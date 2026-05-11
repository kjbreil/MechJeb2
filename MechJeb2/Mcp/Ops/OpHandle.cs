using System;
using System.Globalization;

namespace MuMech.Mcp
{
    public enum OpStatus
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Cancelled,
        Aborted,    // scene/vessel changed out from under us
    }

    // Per-handle state record. Owned by OpRegistry.
    public sealed class OpHandle
    {
        public string Handle;          // base32 GUID
        public string Path;            // capability path that minted this
        public string RequestId;       // the MCP request that started it
        public IRunningOp Op;
        public DateTime StartedUtc;
        public DateTime? TerminatedUtc;
        public OpStatus Status;
        public string Reason;          // populated for Aborted/Cancelled
        public JsonValue Result;       // populated for Succeeded
        public string ErrorCode;       // populated for Failed
        public string ErrorMessage;    // populated for Failed

        public bool IsTerminal =>
            Status == OpStatus.Succeeded || Status == OpStatus.Failed ||
            Status == OpStatus.Cancelled || Status == OpStatus.Aborted;

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Set("handle", Handle)
                .Set("path", Path)
                .Set("status", Status.ToString().ToUpperInvariant())
                .Set("started_at_utc", new JsonString(StartedUtc.ToString("o", CultureInfo.InvariantCulture)))
                .Set("age_ms", (long)((DateTime.UtcNow - StartedUtc).TotalMilliseconds));
            if (TerminatedUtc.HasValue)
                o.Set("terminated_at_utc", new JsonString(TerminatedUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(Reason)) o.Set("reason", Reason);
            if (Result != null) o.Set("result", Result);
            if (!string.IsNullOrEmpty(ErrorCode)) o.Set("error_code", ErrorCode);
            if (!string.IsNullOrEmpty(ErrorMessage)) o.Set("message", ErrorMessage);
            return o;
        }

        public static string NewHandleId()
        {
            // Base32-ish — 16 hex chars is plenty of entropy and human-readable.
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }
}
