using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech.Mcp
{
    // Implementation of the six MCP top-level tools defined by the plan,
    // wired together by McpServerAddon at boot. Each builder returns a
    // ToolDefinition ready to register with JsonRpcTransport.
    //
    // Naming:
    //   mj_status         — server + KSP state probe (read-only)
    //   mj_discover       — walk the capability tree, return schemas
    //   mj_invoke         — call a capability path
    //   mj_read           — read a [McpProperty] value
    //   mj_cancel         — abort a running handle
    //   mj_ops_status     — poll a handle for status / result / error
    //   mj_ops_list       — list all live and recently-terminated handles
    //
    // mj_logs is implemented separately in LogsTool (Phase 3).
    internal static class Tools
    {
        // --- mj_status -----------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Status(McpRegistry registry, OpRegistry ops, Func<string> serverInstanceId)
        {
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_status",
                Description = "KSP + server state probe. Call this first every session to orient. " +
                              "Returns current scene, active vessel summary, paused state, time-warp, UT, " +
                              "save folder, server_instance_id, surface_version, op counts.",
                InputSchema = JsonRpcTransport.EmptyObjectSchema(),
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    var gate = MainThreadGate.Run(() => BuildStatusObject(registry, ops, serverInstanceId()));
                    if (gate.TimedOut) return JsonRpcTransport.ToolError(ErrorCode.MainThreadHung, "Main thread did not service mj_status in time. Try again.");
                    if (gate.Error != null) return JsonRpcTransport.ToolError(ErrorCode.Internal, gate.Error.Message);
                    return JsonRpcTransport.Ok(gate.Value);
                },
            };
        }

        private static JsonObject BuildStatusObject(McpRegistry registry, OpRegistry ops, string instanceId)
        {
            var s = new JsonObject();
            s.Set("server_instance_id", instanceId);
            s.Set("server_version", JsonRpcTransport.ServerVersion);
            s.Set("scene", HighLogic.LoadedScene.ToString());
            s.Set("paused", FlightDriver.Pause);
            s.Set("ut", Planetarium.GetUniversalTime());

            try
            {
                s.Set("time_warp_rate", TimeWarp.CurrentRate);
                s.Set("time_warp_mode", TimeWarp.WarpMode.ToString());
            }
            catch
            {
                s.Set("time_warp_rate", 1.0);
                s.Set("time_warp_mode", "LOW");
            }

            s.Set("save_folder", HighLogic.SaveFolder ?? "");
            try
            {
                ClearToSaveStatus cts = FlightGlobals.ClearToSave();
                s.Set("clear_to_save", cts.ToString());
            }
            catch { s.Set("clear_to_save", "UNKNOWN"); }

            // Active vessel summary (minimal — fuller summary is vessel/summary).
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) s.Set("active_vessel", JsonNull.Instance);
            else
            {
                var vs = new JsonObject()
                    .Set("persistent_id", v.persistentId)
                    .Set("name", v.GetDisplayName() ?? v.vesselName ?? "?")
                    .Set("situation", v.situation.ToString())
                    .Set("type", v.vesselType.ToString());
                if (v.mainBody != null) vs.Set("body", v.mainBody.bodyName ?? "?");
                s.Set("active_vessel", vs);
            }

            // Registry overview.
            s.Set("commands_registered", registry?.CommandCount ?? 0);
            s.Set("properties_registered", registry?.PropertyCount ?? 0);

            // Op counts.
            if (ops != null)
            {
                List<OpHandle> all = ops.SnapshotAll();
                int active = 0, terminal = 0;
                foreach (OpHandle h in all)
                {
                    if (h.Status == OpStatus.Running) active++; else terminal++;
                }
                s.Set("ops_active", active);
                s.Set("ops_terminal_retained", terminal);
            }
            return s;
        }

        // --- mj_discover ---------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Discover(McpRegistry registry, Func<string> serverInstanceId)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("properties", new JsonObject()
                    .Set("path", new JsonObject().Set("type", "string").Set("description", "Capability path (empty = root)")))
                .Set("additionalProperties", false);
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_discover",
                Description = "Walk the capability tree. Empty path returns root children. A namespace path " +
                              "returns its children. A leaf path returns full input/output schemas.",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    string path = "";
                    if (args is JsonObject ao && ao.TryGet("path", out JsonValue pv) && pv.Type == JsonType.String)
                        path = pv.AsString;
                    McpRegistry.Node node = registry.ResolveNode(path);
                    if (node == null)
                        return JsonRpcTransport.ToolError(ErrorCode.PathNotFound, "No such path: " + path);

                    var result = new JsonObject()
                        .Set("path", string.IsNullOrEmpty(path) ? "/" : path)
                        .Set("server_instance_id", serverInstanceId());

                    if (!string.IsNullOrEmpty(node.Description)) result.Set("description", node.Description);
                    if (!string.IsNullOrEmpty(node.Version)) result.Set("surface_version", node.Version);

                    if (node.Command != null)
                    {
                        result.Set("kind", "command");
                        result.Set("side_effect", node.Command.SideEffect.ToString());
                        result.Set("input_schema", node.Command.InputSchema);
                        if (node.Command.OutputSchema != null) result.Set("output_schema", node.Command.OutputSchema);
                        result.Set("returns_handle", node.Command.ReturnsHandle);
                        if (node.Command.Deprecated) result.Set("deprecated", true);
                        if (!string.IsNullOrEmpty(node.Command.ReplacedBy)) result.Set("replaced_by", node.Command.ReplacedBy);
                    }
                    else if (node.Property != null)
                    {
                        result.Set("kind", "property");
                        result.Set("access", node.Property.Access.ToString().ToLowerInvariant());
                        result.Set("schema", node.Property.Schema);
                        if (node.Property.Deprecated) result.Set("deprecated", true);
                    }
                    else
                    {
                        result.Set("kind", "namespace");
                        var children = new JsonArray();
                        foreach (KeyValuePair<string, McpRegistry.Node> kv in node.Children)
                        {
                            McpRegistry.Node c = kv.Value;
                            var child = new JsonObject()
                                .Set("name", c.Name)
                                .Set("path", c.FullPath);
                            if (c.Command != null) child.Set("kind", "command");
                            else if (c.Property != null) child.Set("kind", "property");
                            else child.Set("kind", "namespace");
                            if (!string.IsNullOrEmpty(c.Description)) child.Set("description", c.Description);
                            children.Add(child);
                        }
                        result.Set("children", children);
                    }
                    return JsonRpcTransport.Ok(result);
                },
            };
        }

        // --- mj_invoke -----------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Invoke(McpRegistry registry, OpRegistry ops)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("required", new JsonArray().Add("path"))
                .Set("properties", new JsonObject()
                    .Set("path", new JsonObject().Set("type", "string").Set("description", "Capability path to call"))
                    .Set("args", new JsonObject().Set("type", "object").Set("description", "Arguments matching the path's input_schema")))
                .Set("additionalProperties", false);

            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_invoke",
                Description = "Invoke a capability path. Args must satisfy the path's input_schema (see mj_discover). " +
                              "Returns the path's output, or {handle, status:'running'} for long-running ops " +
                              "(poll via mj_ops_status, cancel via mj_cancel).",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", false)
                    .Set("destructiveHint", true)
                    .Set("idempotentHint", false)
                    .Set("openWorldHint", false),
                IsMutating = true,
                Handler = (args, ctx) =>
                {
                    if (!(args is JsonObject argObj))
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "args must be an object");
                    if (!argObj.TryGet("path", out JsonValue pv) || pv.Type != JsonType.String)
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "missing 'path'");
                    string path = pv.AsString;

                    McpRegistry.CommandBinding binding = registry.ResolveCommand(path);
                    if (binding == null)
                        return JsonRpcTransport.ToolError(ErrorCode.PathNotFound,
                            "No command at path '" + path + "'. Call mj_discover to find available commands.");

                    JsonValue callArgs = argObj.TryGet("args", out JsonValue av) ? av : new JsonObject();

                    // Scene gate (cheap, no main-thread hop required).
                    string sceneErr = CheckScene(binding.RequiredScenes);
                    if (sceneErr != null)
                        return JsonRpcTransport.ToolError(ErrorCode.WrongScene, sceneErr);

                    // Time-warp gate.
                    if (binding.MaxTimeWarpRate > 0)
                    {
                        double currentRate = 1.0;
                        try { currentRate = TimeWarp.CurrentRate; } catch { }
                        if (currentRate > binding.MaxTimeWarpRate)
                            return JsonRpcTransport.ToolError(ErrorCode.WarpTooHigh,
                                "WARP_TOO_HIGH: current rate " + currentRate + "× exceeds " +
                                binding.MaxTimeWarpRate + "× max for '" + path + "'. " +
                                "Call mj_invoke with path='warp/set' (when available) or lower warp in-game and retry.");
                    }

                    // Argument binding.
                    if (!ArgumentBinder.TryBind(binding.Parameters, callArgs, out object[] bound, out string bindErr))
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, bindErr + " (path=" + path + ")");

                    var gate = MainThreadGate.Run<object>(() =>
                    {
                        try { return binding.Method.Invoke(null, bound); }
                        catch (System.Reflection.TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                    }, binding.MainThreadTimeoutMs);

                    if (gate.TimedOut)
                        return JsonRpcTransport.ToolError(ErrorCode.MainThreadHung,
                            "Capability '" + path + "' did not complete within " + binding.MainThreadTimeoutMs + "ms");
                    if (gate.Error is McpException mex)
                        return new JsonRpcTransport.ToolResult
                        {
                            IsError = true,
                            ErrorCode = ErrorCodes.ToWire(mex.Code),
                            Message = mex.Message,
                            StructuredContent = mex.Details,
                        };
                    if (gate.Error != null)
                        return JsonRpcTransport.ToolError(ErrorCode.Internal,
                            "Capability '" + path + "' threw: " + gate.Error.Message);

                    object raw = gate.Value;
                    if (binding.ReturnsHandle && raw is IRunningOp opRet)
                    {
                        OpHandle conflict;
                        OpHandle handle = ops.TryStart(path, opRet, ctx.RequestId, out conflict);
                        if (handle == null)
                        {
                            string conflictInfo = conflict != null ? conflict.Handle : "?";
                            return JsonRpcTransport.ToolError(ErrorCode.Busy,
                                "Path '" + path + "' already has a running op (handle " + conflictInfo + "). " +
                                "Poll mj_ops_status or call mj_cancel.");
                        }
                        return JsonRpcTransport.Ok(new JsonObject()
                            .Set("handle", handle.Handle)
                            .Set("status", "running")
                            .Set("path", path));
                    }
                    return JsonRpcTransport.Ok(ToJson(raw, binding.ReturnType));
                },
            };
        }

        // --- mj_read -------------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Read(McpRegistry registry)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("required", new JsonArray().Add("path"))
                .Set("properties", new JsonObject()
                    .Set("path", new JsonObject().Set("type", "string").Set("description", "Property path to read")))
                .Set("additionalProperties", false);
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_read",
                Description = "Read a [McpProperty] value. Use mj_discover to find readable paths. " +
                              "Read-only and idempotent — safe to poll.",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    if (!(args is JsonObject argObj))
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "args must be an object");
                    if (!argObj.TryGet("path", out JsonValue pv) || pv.Type != JsonType.String)
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "missing 'path'");
                    string path = pv.AsString;

                    McpRegistry.PropertyBinding binding = registry.ResolveProperty(path);
                    if (binding == null)
                        return JsonRpcTransport.ToolError(ErrorCode.PathNotFound, "No property at path '" + path + "'");
                    if (!binding.CanRead)
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "Property '" + path + "' is write-only");

                    string sceneErr = CheckScene(binding.RequiredScenes);
                    if (sceneErr != null)
                        return JsonRpcTransport.ToolError(ErrorCode.WrongScene, sceneErr);

                    var gate = MainThreadGate.Run<object>(() => binding.GetValue());
                    if (gate.TimedOut)
                        return JsonRpcTransport.ToolError(ErrorCode.MainThreadHung, "Main thread did not service mj_read in time");
                    if (gate.Error != null)
                        return JsonRpcTransport.ToolError(ErrorCode.Internal, "Read threw: " + gate.Error.Message);

                    return JsonRpcTransport.Ok(new JsonObject().Set("value", ToJson(gate.Value, binding.ValueType)));
                },
            };
        }

        // --- mj_cancel -----------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Cancel(OpRegistry ops)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("required", new JsonArray().Add("handle"))
                .Set("properties", new JsonObject()
                    .Set("handle", new JsonObject().Set("type", "string"))
                    .Set("reason", new JsonObject().Set("type", "string")))
                .Set("additionalProperties", false);
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_cancel",
                Description = "Cancel a running op by handle. Returns success even if the handle is already " +
                              "terminal (includes current status).",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", false)
                    .Set("destructiveHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = true,
                Handler = (args, ctx) =>
                {
                    if (!(args is JsonObject argObj))
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "args must be an object");
                    if (!argObj.TryGet("handle", out JsonValue hv) || hv.Type != JsonType.String)
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "missing 'handle'");
                    string reason = "client_cancel";
                    if (argObj.TryGet("reason", out JsonValue rv) && rv.Type == JsonType.String) reason = rv.AsString;

                    OpHandle existing = ops.Get(hv.AsString);
                    if (existing == null)
                        return JsonRpcTransport.ToolError(ErrorCode.HandleNotFound, "Unknown handle " + hv.AsString);

                    bool cancelled = false;
                    if (existing.Status == OpStatus.Running)
                    {
                        var gate = MainThreadGate.Run(() => ops.Cancel(hv.AsString, reason));
                        cancelled = !gate.TimedOut && gate.Error == null && true.Equals(gate.Value);
                    }
                    return JsonRpcTransport.Ok(new JsonObject()
                        .Set("handle", existing.Handle)
                        .Set("status", existing.Status.ToString().ToUpperInvariant())
                        .Set("cancelled_now", cancelled));
                },
            };
        }

        // --- mj_logs -------------------------------------------------------
        public static JsonRpcTransport.ToolDefinition Logs(McpLogStream stream)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("properties", new JsonObject()
                    .Set("stream", new JsonObject().Set("type", "string").Set("enum", new JsonArray().Add("mechjeb").Add("ksp_log").Add("both"))
                        .Set("default", "mechjeb")
                        .Set("description", "Which log source. Default 'mechjeb' restricts to MechJeb-tagged records. 'ksp_log' tails KSP.log on disk. 'both' merges."))
                    .Set("level", new JsonObject().Set("type", "string").Set("enum", new JsonArray().Add("Log").Add("Warning").Add("Error").Add("Exception").Add("Assert")))
                    .Set("substring", new JsonObject().Set("type", "string"))
                    .Set("regex", new JsonObject().Set("type", "string"))
                    .Set("since_seq", new JsonObject().Set("type", "integer"))
                    .Set("until_seq", new JsonObject().Set("type", "integer"))
                    .Set("limit", new JsonObject().Set("type", "integer").Set("default", 100).Set("minimum", 1).Set("maximum", 500))
                    .Set("message_limit", new JsonObject().Set("type", "integer").Set("default", 1024)
                        .Set("description", "Per-record message char cap. 0 = no truncation."))
                    .Set("ksp_log_tail_bytes", new JsonObject().Set("type", "integer").Set("default", 8 * 1024 * 1024)))
                .Set("additionalProperties", false);

            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_logs",
                Description = "Filtered search over the in-memory MechJeb log ring buffer and/or KSP.log on disk. " +
                              "Defaults to MechJeb-tagged records only (lower risk of exposing other mods' logs).",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    JsonObject a = args as JsonObject ?? new JsonObject();
                    string streamSel = GetString(a, "stream", "mechjeb");
                    string levelSel = GetString(a, "level", null);
                    string substring = GetString(a, "substring", null);
                    string regexStr = GetString(a, "regex", null);
                    long sinceSeq = GetLong(a, "since_seq", 0);
                    long untilSeq = GetLong(a, "until_seq", 0);
                    int limit = (int)GetLong(a, "limit", 100);
                    int msgLimit = (int)GetLong(a, "message_limit", 1024);
                    int tailBytes = (int)GetLong(a, "ksp_log_tail_bytes", KspLogFileSearch.DefaultTailBytes);

                    Regex regex = null;
                    if (!string.IsNullOrEmpty(regexStr))
                    {
                        try { regex = new Regex(regexStr, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)); }
                        catch (ArgumentException) { return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "Invalid regex: " + regexStr); }
                    }

                    var records = new JsonArray();
                    if (streamSel == "mechjeb" || streamSel == "both")
                    {
                        var snapshot = stream.Snapshot(sinceSeq, untilSeq, limit);
                        foreach (McpLogRecord r in snapshot)
                        {
                            if (levelSel != null && !r.Level.ToString().Equals(levelSel, StringComparison.OrdinalIgnoreCase)) continue;
                            // For 'mechjeb' stream, require a MechJeb tag prefix.
                            if (streamSel == "mechjeb" && LogRecordJson.ExtractTag(r.Message) == null) continue;
                            if (substring != null && (r.Message == null || r.Message.IndexOf(substring, StringComparison.Ordinal) < 0)) continue;
                            if (regex != null && !regex.IsMatch(r.Message ?? "")) continue;
                            records.Add(LogRecordJson.ToJson(r, msgLimit));
                            if (records.Count >= limit) break;
                        }
                    }
                    if ((streamSel == "ksp_log" || streamSel == "both") && records.Count < limit)
                    {
                        int kspLimit = limit - records.Count;
                        var lines = KspLogFileSearch.Tail(substring, regex, tailBytes, kspLimit);
                        foreach (KspLogFileSearch.Line line in lines)
                        {
                            var o = new JsonObject()
                                .Set("source", "ksp_log")
                                .Set("byte_offset", line.byte_offset)
                                .Set("text", line.text);
                            records.Add(o);
                        }
                    }
                    return JsonRpcTransport.Ok(new JsonObject()
                        .Set("stream", streamSel)
                        .Set("count", records.Count)
                        .Set("records", records));
                },
            };
        }

        private static string GetString(JsonObject o, string key, string defaultValue)
        {
            if (o.TryGet(key, out JsonValue v) && v.Type == JsonType.String) return v.AsString;
            return defaultValue;
        }
        private static long GetLong(JsonObject o, string key, long defaultValue)
        {
            if (o.TryGet(key, out JsonValue v) && v.Type == JsonType.Number) return v.AsInt;
            return defaultValue;
        }

        // --- mj_ops_list / mj_ops_status -----------------------------------
        public static JsonRpcTransport.ToolDefinition OpsList(OpRegistry ops)
        {
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_ops_list",
                Description = "List all live and recently-terminated operation handles. Useful after a context " +
                              "reset when the LLM has forgotten which handles it minted.",
                InputSchema = JsonRpcTransport.EmptyObjectSchema(),
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    var arr = new JsonArray();
                    foreach (OpHandle h in ops.SnapshotAll()) arr.Add(h.ToJson());
                    return JsonRpcTransport.Ok(new JsonObject().Set("ops", arr));
                },
            };
        }

        public static JsonRpcTransport.ToolDefinition OpsStatus(OpRegistry ops)
        {
            var input = new JsonObject()
                .Set("type", "object")
                .Set("required", new JsonArray().Add("handle"))
                .Set("properties", new JsonObject().Set("handle", new JsonObject().Set("type", "string")))
                .Set("additionalProperties", false);
            return new JsonRpcTransport.ToolDefinition
            {
                Name = "mj_ops_status",
                Description = "Poll a handle for status / result / error. Handles live 5 minutes past terminal state.",
                InputSchema = input,
                Annotations = new JsonObject()
                    .Set("readOnlyHint", true)
                    .Set("idempotentHint", true)
                    .Set("openWorldHint", false),
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    if (!(args is JsonObject argObj))
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "args must be an object");
                    if (!argObj.TryGet("handle", out JsonValue hv) || hv.Type != JsonType.String)
                        return JsonRpcTransport.ToolError(ErrorCode.SchemaInvalid, "missing 'handle'");
                    OpHandle h = ops.Get(hv.AsString);
                    if (h == null)
                        return JsonRpcTransport.ToolError(ErrorCode.HandleNotFound, "Unknown handle " + hv.AsString);
                    return JsonRpcTransport.Ok(h.ToJson());
                },
            };
        }

        // --- helpers -------------------------------------------------------
        private static string CheckScene(GameScenes[] required)
        {
            if (required == null || required.Length == 0) return null;
            GameScenes current = HighLogic.LoadedScene;
            foreach (GameScenes s in required)
                if (s == current) return null;
            var allowed = new List<string>();
            foreach (GameScenes s in required) allowed.Add(s.ToString());
            return "Current scene " + current + " not in allowed: [" + string.Join(",", allowed.ToArray()) + "]";
        }

        // Converts a C# value of an [McpProperty] or [McpCommand] return type
        // into a JsonValue. Works for primitives, enums, strings, JsonValues,
        // arrays/lists of primitives, and "plain DTOs" (public fields/props).
        // For Unity/KSP runtime types it falls back to ToString — but those
        // are rejected at registry build, so this only fires for forgotten
        // edge cases.
        public static JsonValue ToJson(object value, Type declaredType)
        {
            if (value == null) return JsonNull.Instance;
            if (value is JsonValue jv) return jv;
            Type t = value.GetType();
            if (value is bool b) return new JsonBool(b);
            if (value is string s) return new JsonString(s);
            if (value is Enum) return new JsonString(value.ToString());
            if (value is IConvertible)
            {
                TypeCode tc = Type.GetTypeCode(t);
                switch (tc)
                {
                    case TypeCode.SByte: case TypeCode.Byte:
                    case TypeCode.Int16: case TypeCode.UInt16:
                    case TypeCode.Int32: case TypeCode.UInt32:
                    case TypeCode.Int64:
                        return new JsonNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    case TypeCode.UInt64:
                        return new JsonNumber((long)(ulong)value);
                    case TypeCode.Single: case TypeCode.Double:
                    case TypeCode.Decimal:
                        return new JsonNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
            }
            if (value is System.Collections.IDictionary dict)
            {
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry e in dict)
                    obj.Set(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "",
                            ToJson(e.Value, e.Value?.GetType()));
                return obj;
            }
            if (value is System.Collections.IEnumerable en)
            {
                var arr = new JsonArray();
                foreach (object item in en) arr.Add(ToJson(item, item?.GetType()));
                return arr;
            }
            // Plain DTO: reflect public fields + properties.
            var dto = new JsonObject();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                dto.Set(f.Name, ToJson(f.GetValue(value), f.FieldType));
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetGetMethod(false) == null) continue;
                try { dto.Set(p.Name, ToJson(p.GetValue(value, null), p.PropertyType)); }
                catch { /* skip getter that throws */ }
            }
            return dto;
        }
    }
}
