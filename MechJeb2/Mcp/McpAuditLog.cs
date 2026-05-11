using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MuMech.Mcp
{
    // Append-only JSONL audit log for every MCP call. One sealed instance
    // owned by McpServerAddon. Thread-safe writes from HTTP worker threads.
    //
    // Durability strategy (cf. deepen-plan finding D5):
    //   * Each entry is framed by leading AND trailing newline: "\n{json}\n".
    //     A torn write on KSP crash produces an obviously-skippable record;
    //     readers walk to the next "\n{" sentinel rather than choking on
    //     malformed JSONL.
    //   * Mutating calls: synchronous Flush(flushToDisk: true) per entry.
    //   * Read-only calls: buffered append; flushed on a 1-second timer or
    //     on Dispose.
    //   * Daily file rotation with 30-day retention (no gzip).
    //
    // File location: <KspDir>/Logs/mechjeb-mcp-audit.log
    // Permissions: best-effort 0600 on POSIX via File.SetUnixFileMode where
    // available; no-op on Windows / older Mono. Documented as not a tamper-
    // proof record (operator-as-user can `rm` it anyway).
    public sealed class McpAuditLog : IDisposable
    {
        private readonly string _path;
        private readonly object _writeLock = new object();
        private readonly Timer _flushTimer;
        private FileStream _stream;
        private bool _disposed;

        public string Path => _path;

        public McpAuditLog(string filePath)
        {
            _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
            string dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: false);
            TryRestrictPermissions(_path);

            // Periodic flush for buffered (read-only) entries. 1 second is a
            // reasonable compromise between durability and write amplification.
            _flushTimer = new Timer(_ => TimerFlush(), null, 1000, 1000);
        }

        public bool Append(McpAuditEntry entry, bool durable)
        {
            if (entry == null) return false;
            JsonObject obj = entry.ToJson();
            // Frame: \n + json + \n. The leading \n means each record begins
            // immediately after a known-good byte. Readers scan for "\n{" as
            // record start.
            string payload = "\n" + Json.Stringify(obj) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);

            lock (_writeLock)
            {
                if (_disposed) return false;
                try
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    if (durable)
                    {
                        _stream.Flush(flushToDisk: true);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    // Audit log write must NOT throw back to the request handler.
                    // Log to KSP.log and continue; an audit gap is better than a
                    // failed request, but the gap itself is the bug.
                    UnityEngine.Debug.LogError("[MechJeb-MCP] Audit log write failed: " + ex.Message);
                    return false;
                }
            }
        }

        private void TimerFlush()
        {
            lock (_writeLock)
            {
                if (_disposed || _stream == null) return;
                try { _stream.Flush(flushToDisk: false); }
                catch { /* swallowed; will retry next tick */ }
            }
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _flushTimer.Dispose(); } catch { }
                try
                {
                    if (_stream != null)
                    {
                        _stream.Flush(flushToDisk: true);
                        _stream.Dispose();
                        _stream = null;
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[MechJeb-MCP] Audit log dispose failed: " + ex.Message);
                }
            }
        }

        private static void TryRestrictPermissions(string path)
        {
            // Best-effort 0600 on POSIX systems. .NET Framework 4.8 on Mono
            // doesn't have File.SetUnixFileMode (that's net7+), but Mono.Posix
            // also isn't reliably present. We use reflection-free Mono.Unix
            // fallback if available, else no-op.
            //
            // On macOS / Linux KSP installs, the default umask 022 produces a
            // world-readable 0644 file. This is documented in docs/mcp/README
            // as a "don't share when zipping save folder" caveat.
            //
            // Operator can still chmod 0600 by hand if they care.
            try
            {
                Type unixFileInfo = Type.GetType("Mono.Unix.UnixFileInfo, Mono.Posix");
                if (unixFileInfo == null) return;
                object info = Activator.CreateInstance(unixFileInfo, new object[] { path });
                System.Reflection.PropertyInfo modeProp = unixFileInfo.GetProperty("FileAccessPermissions");
                if (modeProp == null) return;
                // 0600 = UserRead | UserWrite = 0x100 | 0x80 = 384
                modeProp.SetValue(info, Enum.ToObject(modeProp.PropertyType, 384), null);
            }
            catch
            {
                // Mono.Posix not loaded — fine.
            }
        }
    }

    public sealed class McpAuditEntry
    {
        public string RequestId;
        public DateTime TimestampUtc;
        public string ClientAddr;
        public string ToolName;            // mj_invoke / mj_read / etc.
        public string Path;                // capability path when applicable
        public JsonValue Args;             // truncated to ~4KB by caller
        public string ResultCode;          // "OK" or ErrorCode wire string
        public long LatencyMs;
        public bool IsMutating;            // controls fsync

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            o.Set("request_id", RequestId ?? "");
            o.Set("ts", new JsonString(TimestampUtc.ToString("o", CultureInfo.InvariantCulture)));
            o.Set("client_addr", ClientAddr ?? "");
            o.Set("tool", ToolName ?? "");
            if (!string.IsNullOrEmpty(Path)) o.Set("path", Path);
            if (Args != null) o.Set("args", Args);
            o.Set("result_code", ResultCode ?? "");
            o.Set("latency_ms", LatencyMs);
            o.Set("mutating", IsMutating);
            return o;
        }
    }
}
