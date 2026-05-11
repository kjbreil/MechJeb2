using System;
using System.Net;

namespace MuMech.Mcp
{
    // Validates incoming HTTP requests against the security posture defined in
    // the plan: loopback-only, strict Origin/Host checks (DNS-rebinding defense),
    // POST /mcp/ only, JSON content type, bounded body size.
    //
    // Bind address is enforced by HttpListener prefix (`http://127.0.0.1:<port>/mcp/`);
    // this class additionally validates per-request headers and the path.
    internal static class HttpAccessControl
    {
        public const string EndpointPath = "/mcp/";
        public const int MaxBodyBytes = 1 * 1024 * 1024; // 1MB
        public const string McpProtocolVersion = "2025-06-18";

        public sealed class Decision
        {
            public bool Allowed;
            public int RejectStatus;
            public string RejectReason;
            public ErrorCode ErrorCode;
        }

        // The Origin allowlist for browser-originating requests. Non-browser
        // tools (curl, claude-code CLI) typically omit Origin entirely; that's
        // accepted by default but logged so the operator can spot anomalies.
        // Adding any non-loopback origin to this list opens the door to CSRF.
        private static readonly string[] AllowedOriginPrefixes = new[]
        {
            "http://localhost",
            "http://127.0.0.1",
            "http://[::1]",
        };

        // The Host allowlist (canonical defense against DNS rebinding). An
        // attacker who tricks DNS into pointing evil.com → 127.0.0.1 will still
        // send `Host: evil.com`, which fails this check.
        private static readonly string[] AllowedHostPrefixes = new[]
        {
            "127.0.0.1",
            "localhost",
            "[::1]",
        };

        public static Decision Evaluate(HttpListenerRequest request, bool isInitializeMessage, bool allowNullOrigin)
        {
            var d = new Decision();

            // Path: only POST /mcp/, exact.
            if (!request.HttpMethod.Equals("POST", StringComparison.Ordinal))
            {
                d.RejectStatus = 405;
                d.RejectReason = "Only POST allowed on /mcp/";
                return d;
            }
            string absPath = request.Url?.AbsolutePath ?? string.Empty;
            if (!absPath.Equals(EndpointPath, StringComparison.Ordinal))
            {
                d.RejectStatus = 404;
                d.RejectReason = "Unknown path";
                return d;
            }

            // Host header: must be loopback. Canonical DNS-rebinding defense.
            string host = request.Headers["Host"] ?? string.Empty;
            if (!IsAllowedHost(host))
            {
                d.RejectStatus = 403;
                d.RejectReason = "Host header not allowed: " + host;
                return d;
            }

            // Origin header: allow if absent (CLI tools), reject if present and
            // not loopback. `null` Origin (file:// pages, sandboxed iframes) is
            // gated by the operator-controlled allowNullOrigin flag.
            string origin = request.Headers["Origin"];
            if (origin != null && origin.Length > 0)
            {
                if (origin.Equals("null", StringComparison.Ordinal))
                {
                    if (!allowNullOrigin)
                    {
                        d.RejectStatus = 403;
                        d.RejectReason = "Origin: null not allowed";
                        return d;
                    }
                }
                else if (!IsAllowedOrigin(origin))
                {
                    d.RejectStatus = 403;
                    d.RejectReason = "Origin not allowed: " + origin;
                    return d;
                }
            }

            // Content-Type: must be application/json. Rejecting text/plain et al
            // forces a CORS preflight from any browser-originated POST, which
            // we never grant — eliminates simple-request CSRF.
            string contentType = request.ContentType ?? string.Empty;
            // Trim any "; charset=utf-8" suffix before comparison
            int semi = contentType.IndexOf(';');
            string mediaType = (semi >= 0 ? contentType.Substring(0, semi) : contentType).Trim();
            if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                d.RejectStatus = 415;
                d.RejectReason = "Content-Type must be application/json";
                return d;
            }

            // Body size cap.
            long len = request.ContentLength64;
            if (len > MaxBodyBytes)
            {
                d.RejectStatus = 413;
                d.RejectReason = "Body exceeds " + MaxBodyBytes + " bytes";
                return d;
            }

            // MCP-Protocol-Version header: required on all post-initialize calls.
            // The spec allows missing on initialize itself (clients haven't
            // negotiated yet) and treats missing as a strict failure afterward.
            if (!isInitializeMessage)
            {
                string protoVer = request.Headers["MCP-Protocol-Version"];
                if (string.IsNullOrEmpty(protoVer))
                {
                    d.RejectStatus = 400;
                    d.RejectReason = "Missing MCP-Protocol-Version header";
                    d.ErrorCode = ErrorCode.ProtocolVersionMissing;
                    return d;
                }
                if (!protoVer.Equals(McpProtocolVersion, StringComparison.Ordinal))
                {
                    d.RejectStatus = 400;
                    d.RejectReason = "Unsupported MCP-Protocol-Version: " + protoVer;
                    d.ErrorCode = ErrorCode.ProtocolVersionUnsupported;
                    return d;
                }
            }

            d.Allowed = true;
            return d;
        }

        private static bool IsAllowedHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            for (int i = 0; i < AllowedHostPrefixes.Length; i++)
            {
                string p = AllowedHostPrefixes[i];
                if (host.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
                if (host.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAllowedOrigin(string origin)
        {
            for (int i = 0; i < AllowedOriginPrefixes.Length; i++)
            {
                string p = AllowedOriginPrefixes[i];
                if (origin.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
                if (origin.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
