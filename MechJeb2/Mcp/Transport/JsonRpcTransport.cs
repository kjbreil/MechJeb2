using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech.Mcp
{
    // JSON-RPC 2.0 envelope handling on top of HttpListener. Implements the
    // MCP transport subset we need at Phase 1: `initialize`, `tools/list`,
    // `tools/call`, plus the `notifications/initialized` no-op notification.
    //
    // The registry of MCP tool handlers is injected at construction time.
    // Phase 1 ships with a single `dev_ping` tool that proves the harness
    // end-to-end; the full tool surface (mj_status / mj_discover / mj_invoke
    // / mj_read / mj_logs / mj_cancel / mj_ops_*) lands in later phases.
    //
    // Per MCP 2025-06-18 spec:
    //   * tool execution failures → result.isError:true (NOT a JSON-RPC error).
    //   * protocol-layer failures → JSON-RPC error envelope.
    //   * tool result content is both `structuredContent` (typed) AND a text
    //     mirror, for backward compat.
    internal sealed class JsonRpcTransport
    {
        public const string ServerName = "mechjeb-mcp";
        public const string ServerVersion = "1.0.0-phase1";

        public delegate ToolResult ToolHandler(JsonValue arguments, RequestContext ctx);

        public sealed class ToolDefinition
        {
            public string Name;
            public string Description;
            public JsonObject InputSchema;
            public JsonObject OutputSchema;        // optional
            public JsonObject Annotations;         // readOnlyHint, destructiveHint, ...
            public ToolHandler Handler;
            public bool IsMutating;                // controls audit-log fsync
        }

        public sealed class ToolResult
        {
            public JsonValue StructuredContent;
            public bool IsError;
            public string ErrorCode;               // wire string when IsError
            public string Message;                 // human-readable / recovery hint
        }

        public sealed class RequestContext
        {
            public string RequestId;
            public string ClientAddr;
            public string SessionId;
        }

        private readonly Dictionary<string, ToolDefinition> _tools;
        private readonly McpAuditLog _audit;
        private readonly McpServerSettings _settings;
        private readonly Func<string> _serverInstanceId;
        private readonly Func<JsonObject> _toolsListPrebuilt;

        public JsonRpcTransport(
            IEnumerable<ToolDefinition> tools,
            McpAuditLog audit,
            McpServerSettings settings,
            Func<string> serverInstanceId)
        {
            _tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
            foreach (ToolDefinition t in tools) _tools[t.Name] = t;
            _audit = audit;
            _settings = settings;
            _serverInstanceId = serverInstanceId;

            // Pre-serialize the tools/list payload at construction time. The
            // tool set doesn't change at runtime in Phase 1, so this is cheap.
            JsonObject prebuilt = BuildToolsListResult();
            _toolsListPrebuilt = () => prebuilt;
        }

        public void Handle(HttpListenerContext context)
        {
            var sw = Stopwatch.StartNew();
            string clientAddr = context.Request.RemoteEndPoint?.ToString() ?? "?";
            JsonValue requestEnvelope = null;
            string method = null;
            string requestId = "";
            var ctx = new RequestContext { ClientAddr = clientAddr };

            try
            {
                // Pre-flight: cap body size even when the client uses
                // Transfer-Encoding: chunked (ContentLength64 = -1).
                // HttpAccessControl.Evaluate's ContentLength64 check only fires
                // when the length is known; chunked bypasses it.
                if (!TryReadBody(context.Request, HttpAccessControl.MaxBodyBytes, out string body))
                {
                    context.Response.StatusCode = 413;
                    WriteJsonBody(context.Response, new JsonObject()
                        .Set("error", new JsonObject()
                            .Set("code", "PAYLOAD_TOO_LARGE")
                            .Set("message", "Request body exceeds " + HttpAccessControl.MaxBodyBytes + " bytes")));
                    return;
                }
                if (body.Length == 0)
                {
                    WriteJsonRpcError(context, null, JsonRpcErrorCode.ParseError, "Empty request body");
                    return;
                }

                try { requestEnvelope = Json.Parse(body); }
                catch (JsonException ex)
                {
                    WriteJsonRpcError(context, null, JsonRpcErrorCode.ParseError, "Invalid JSON: " + ex.Message);
                    return;
                }

                JsonObject envObj = requestEnvelope as JsonObject;
                if (envObj == null)
                {
                    WriteJsonRpcError(context, null, JsonRpcErrorCode.InvalidRequest, "Envelope must be a JSON object");
                    return;
                }

                if (envObj["jsonrpc"].AsString != "2.0")
                {
                    WriteJsonRpcError(context, null, JsonRpcErrorCode.InvalidRequest, "jsonrpc must be \"2.0\"");
                    return;
                }

                method = envObj.TryGet("method", out JsonValue mv) && mv.Type == JsonType.String ? mv.AsString : null;
                if (method == null)
                {
                    WriteJsonRpcError(context, envObj["id"], JsonRpcErrorCode.InvalidRequest, "Missing method");
                    return;
                }

                bool isInitialize = method == "initialize";

                // Access control with method-aware initialize bypass.
                HttpAccessControl.Decision decision = HttpAccessControl.Evaluate(
                    context.Request, isInitialize, _settings.AllowNullOrigin);
                if (!decision.Allowed)
                {
                    Debug.LogWarning(string.Format(
                        "[MechJeb-MCP] Rejected request from {0}: {1}", clientAddr, decision.RejectReason));
                    context.Response.StatusCode = decision.RejectStatus;
                    WriteJsonBody(context.Response, new JsonObject()
                        .Set("error", new JsonObject()
                            .Set("code", ErrorCodes.ToWire(decision.ErrorCode))
                            .Set("message", decision.RejectReason)));
                    return;
                }

                JsonValue idValue = envObj.TryGet("id", out JsonValue iv) ? iv : null;
                if (idValue != null && idValue.Type == JsonType.String) requestId = idValue.AsString;
                else if (idValue != null && idValue.Type == JsonType.Number) requestId = idValue.AsInt.ToString();
                ctx.RequestId = requestId;
                ctx.SessionId = context.Request.Headers["Mcp-Session-Id"];

                JsonValue paramsValue = envObj.TryGet("params", out JsonValue pv) ? pv : null;
                JsonObject paramsObj = paramsValue as JsonObject;

                // Dispatch method.
                JsonValue resultValue;
                switch (method)
                {
                    case "initialize":
                        resultValue = HandleInitialize(paramsObj, context.Response);
                        break;
                    case "notifications/initialized":
                        // No id, no response (it's a notification). 202 Accepted per spec.
                        // Set ContentLength64 = 0 so HTTP/1.1 keep-alive clients don't
                        // stall waiting for a body that will never arrive.
                        context.Response.StatusCode = 202;
                        context.Response.ContentLength64 = 0;
                        return;
                    case "tools/list":
                        resultValue = _toolsListPrebuilt();
                        break;
                    case "tools/call":
                        resultValue = HandleToolsCall(paramsObj, ctx, sw);
                        break;
                    default:
                        WriteJsonRpcError(context, idValue, JsonRpcErrorCode.MethodNotFound, "Unknown method: " + method);
                        return;
                }

                WriteJsonRpcResult(context, idValue, resultValue);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Unhandled transport error: " + ex);
                try
                {
                    WriteJsonRpcError(context,
                        requestEnvelope is JsonObject o ? o["id"] : null,
                        JsonRpcErrorCode.InternalError, "Internal server error");
                }
                catch { /* response stream may be unusable; nothing to do. */ }
            }
        }

        private JsonObject HandleInitialize(JsonObject paramsObj, HttpListenerResponse response)
        {
            // MCP spec 2025-06-18: mirror the client's protocolVersion (or send
            // ours if theirs is missing). We claim only the `tools` capability
            // at this phase.
            string clientProtoVer = paramsObj != null
                ? (paramsObj.TryGet("protocolVersion", out JsonValue pv) && pv.Type == JsonType.String ? pv.AsString : null)
                : null;
            string negotiated = clientProtoVer ?? HttpAccessControl.McpProtocolVersion;

            // Session: emit Mcp-Session-Id header (Type-4 random GUID).
            string sessionId = Guid.NewGuid().ToString("N");
            response.Headers["Mcp-Session-Id"] = sessionId;

            var serverInfo = new JsonObject()
                .Set("name", ServerName)
                .Set("version", ServerVersion);

            var capabilities = new JsonObject()
                .Set("tools", new JsonObject().Set("listChanged", true));

            return new JsonObject()
                .Set("protocolVersion", negotiated)
                .Set("capabilities", capabilities)
                .Set("serverInfo", serverInfo)
                .Set("instructions",
                    "MechJeb capability server. Call mj_status first to orient; mj_discover to walk the capability tree. " +
                    "Phase 1 ships dev_ping only — full surface lands in later phases.");
        }

        private JsonObject HandleToolsCall(JsonObject paramsObj, RequestContext ctx, Stopwatch sw)
        {
            if (paramsObj == null)
                return BuildToolResultEnvelope(ToolError("SCHEMA_INVALID", "Missing params"));

            string toolName = paramsObj.TryGet("name", out JsonValue nv) && nv.Type == JsonType.String ? nv.AsString : null;
            if (toolName == null)
                return BuildToolResultEnvelope(ToolError("SCHEMA_INVALID", "Missing tools/call name"));

            JsonValue argsValue = paramsObj.TryGet("arguments", out JsonValue av) ? av : new JsonObject();

            if (!_tools.TryGetValue(toolName, out ToolDefinition def))
                return BuildToolResultEnvelope(ToolError("PATH_NOT_FOUND", "Unknown tool: " + toolName));

            ToolResult result;
            try { result = def.Handler(argsValue, ctx); }
            catch (McpException ex)
            {
                result = new ToolResult { IsError = true, ErrorCode = ErrorCodes.ToWire(ex.Code), Message = ex.Message };
            }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Tool '" + toolName + "' threw: " + ex);
                result = new ToolResult { IsError = true, ErrorCode = "INTERNAL", Message = "Tool raised an exception" };
            }

            JsonObject envelope = BuildToolResultEnvelope(result);

            // Audit (after building the envelope so we know IsError).
            try
            {
                var entry = new McpAuditEntry
                {
                    RequestId = ctx.RequestId,
                    TimestampUtc = DateTime.UtcNow,
                    ClientAddr = ctx.ClientAddr,
                    ToolName = toolName,
                    Args = TruncateForAudit(argsValue),
                    ResultCode = result.IsError ? (result.ErrorCode ?? "INTERNAL") : "OK",
                    LatencyMs = sw.ElapsedMilliseconds,
                    IsMutating = def.IsMutating,
                };
                _audit?.Append(entry, durable: def.IsMutating);
            }
            catch { /* never let audit failure mask the response */ }

            return envelope;
        }

        private static JsonObject BuildToolResultEnvelope(ToolResult result)
        {
            // Per MCP 2025-06-18: return BOTH structuredContent AND a text
            // mirror containing the JSON, for backward compat with older
            // clients that only read content[].
            JsonValue payload = result.IsError
                ? (JsonValue)new JsonObject()
                    .Set("error_code", result.ErrorCode ?? "INTERNAL")
                    .Set("message", result.Message ?? "")
                : (result.StructuredContent ?? new JsonObject());

            string textMirror = result.IsError
                ? (result.Message ?? result.ErrorCode ?? "INTERNAL")
                : Json.Stringify(payload);

            var contentArr = new JsonArray();
            contentArr.Add(new JsonObject()
                .Set("type", "text")
                .Set("text", textMirror));

            var env = new JsonObject()
                .Set("content", contentArr)
                .Set("structuredContent", payload)
                .Set("isError", result.IsError);
            return env;
        }

        public static ToolResult Ok(JsonValue structured)
        {
            return new ToolResult { StructuredContent = structured ?? new JsonObject() };
        }

        public static ToolResult ToolError(string code, string message)
        {
            return new ToolResult { IsError = true, ErrorCode = code, Message = message };
        }

        public static ToolResult ToolError(ErrorCode code, string message)
        {
            return new ToolResult { IsError = true, ErrorCode = ErrorCodes.ToWire(code), Message = message };
        }

        private JsonObject BuildToolsListResult()
        {
            var list = new JsonArray();
            foreach (KeyValuePair<string, ToolDefinition> kv in _tools)
            {
                ToolDefinition t = kv.Value;
                var entry = new JsonObject()
                    .Set("name", t.Name)
                    .Set("description", t.Description ?? "")
                    .Set("inputSchema", t.InputSchema ?? EmptyObjectSchema());
                if (t.OutputSchema != null) entry.Set("outputSchema", t.OutputSchema);
                if (t.Annotations != null) entry.Set("annotations", t.Annotations);
                list.Add(entry);
            }
            return new JsonObject().Set("tools", list);
        }

        public static JsonObject EmptyObjectSchema()
        {
            return new JsonObject()
                .Set("type", "object")
                .Set("properties", new JsonObject())
                .Set("additionalProperties", false);
        }

        // Reads the request body with a hard byte cap, regardless of whether
        // the client supplied Content-Length or used chunked transfer encoding.
        // Returns false (without consuming the full body) if the cap is hit.
        private static bool TryReadBody(HttpListenerRequest req, int maxBytes, out string body)
        {
            body = null;
            // Fast reject if Content-Length is declared and exceeds cap.
            long declared = req.ContentLength64;
            if (declared > maxBytes) return false;

            Encoding encoding = req.ContentEncoding ?? Encoding.UTF8;
            int bufSize = Math.Min(8192, maxBytes);
            var buf = new byte[bufSize];
            using (var ms = new MemoryStream(declared > 0 ? (int)declared : bufSize))
            {
                int n;
                while ((n = req.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > maxBytes) return false;
                    ms.Write(buf, 0, n);
                }
                body = encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                return true;
            }
        }

        private static void WriteJsonRpcResult(HttpListenerContext context, JsonValue id, JsonValue result)
        {
            var env = new JsonObject().Set("jsonrpc", "2.0");
            if (id != null) env.Set("id", id);
            env.Set("result", result ?? JsonNull.Instance);
            context.Response.StatusCode = 200;
            WriteJsonBody(context.Response, env);
        }

        private static void WriteJsonRpcError(HttpListenerContext context, JsonValue id, int code, string message)
        {
            var err = new JsonObject().Set("code", code).Set("message", message ?? "");
            var env = new JsonObject().Set("jsonrpc", "2.0");
            env.Set("id", id ?? JsonNull.Instance);
            env.Set("error", err);
            // JSON-RPC errors still travel over HTTP 200 (envelope is valid).
            context.Response.StatusCode = 200;
            WriteJsonBody(context.Response, env);
        }

        public static void WriteJsonBody(HttpListenerResponse response, JsonValue body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(Json.Stringify(body));
            response.ContentType = "application/json";
            response.ContentLength64 = payload.Length;
            using (Stream s = response.OutputStream)
            {
                s.Write(payload, 0, payload.Length);
            }
        }

        // Truncate args destined for the audit log so a 1MB request body
        // doesn't bloat the audit file. 4KB is the convention from finding D6.
        private static JsonValue TruncateForAudit(JsonValue args)
        {
            if (args == null) return JsonNull.Instance;
            string s = Json.Stringify(args);
            if (s.Length <= 4096) return args;
            return new JsonString(s.Substring(0, 4096) + "…[truncated]");
        }
    }
}
