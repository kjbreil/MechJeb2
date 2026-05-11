# MechJeb MCP Wire Protocol

This is the precise wire contract the MechJeb MCP server speaks. For operator-facing setup, see `README.md` in this directory.

## Transport

- **HTTP only.** Bound to `127.0.0.1` exclusively. HTTPS rejected on Mono (Unity 2019.4 / Mono 5.x has known TLS bugs in `HttpListener`).
- **Single endpoint:** `POST /mcp/` (trailing slash required).
- **Wire format:** JSON-RPC 2.0 with MCP framing per spec revision `2025-06-18`.
- **Required request headers:**
  - `Content-Type: application/json` (anything else → HTTP 415)
  - `Accept: application/json, text/event-stream`
  - `Host:` must be `127.0.0.1`, `localhost`, or `[::1]` (with optional port). Other values → HTTP 400.
  - `Origin:` if present, must be one of `http://localhost[:*]`, `http://127.0.0.1[:*]`, `http://[::1][:*]`, or absent. `null` Origin is gated by `allow_null_origin` setting (default false).
  - `MCP-Protocol-Version: 2025-06-18` required on every call **except** `initialize` and `notifications/initialized`. Missing → HTTP 400 + `PROTOCOL_VERSION_MISSING`. Wrong value → HTTP 400 + `PROTOCOL_VERSION_UNSUPPORTED`.
- **Body size cap:** 1 MB. Larger → HTTP 413. Chunked-transfer requests with no `Content-Length` are also size-capped at the read layer.
- **Optional `Mcp-Session-Id`:** server emits a UUID in the `initialize` response header; clients may echo on subsequent requests.

## Handshake

Client → server:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "protocolVersion":"2025-06-18",
  "capabilities":{},
  "clientInfo":{"name":"<client>","version":"<x.y>"}}}
```

Server → client:

```json
{"jsonrpc":"2.0","id":1,"result":{
  "protocolVersion":"2025-06-18",
  "capabilities":{"tools":{"listChanged":true}},
  "serverInfo":{"name":"mechjeb-mcp","version":"1.0.0-phase6"},
  "instructions":"MechJeb capability server. Call mj_status first..."}}
```

Client must then send the notification (no response, HTTP 202):

```json
{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
```

## Tool invocation result shape

Per MCP 2025-06-18, `tools/call` returns BOTH `structuredContent` (typed clients) AND a text-content mirror (older clients). Both encode the same data.

Success:

```json
{"jsonrpc":"2.0","id":2,"result":{
  "content":[{"type":"text","text":"<json-serialized structuredContent>"}],
  "structuredContent":{"...":"..."},
  "isError":false}}
```

Tool-execution failure (NOT a JSON-RPC error — the LLM sees and recovers):

```json
{"jsonrpc":"2.0","id":2,"result":{
  "content":[{"type":"text","text":"WARP_TOO_HIGH: time-warp is 4×. Lower warp and retry."}],
  "structuredContent":{"error_code":"WARP_TOO_HIGH","message":"..."},
  "isError":true}}
```

Protocol-level failure (JSON-RPC `error`, used only for parse/transport problems):

```json
{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Invalid JSON: ..."}}
```

## Error code enum (wire strings)

All English identifiers. Never localized.

| Code | When to use |
|---|---|
| `WRONG_SCENE` | Capability requires a specific KSP scene; tells LLM to switch. |
| `NO_VESSEL` | No master MechJeb on the active vessel (or no active vessel). |
| `NO_TARGET` | Target-relative SmartASS mode without a target set. |
| `BUSY` | A long-running op is already active at this path. Returned with `current_handle`. |
| `WARP_TOO_HIGH` | Time-warp exceeds the command's `MaxTimeWarpRate`. |
| `NOT_CLEAR_TO_SAVE` | KSP refused the save/load. Returned with `clear_to_save` status. |
| `NAME_EXISTS` / `NAME_NOT_FOUND` | Save file name collision / absence. |
| `RESERVED_NAME` | Attempt to use a reserved name (`persistent`, `quicksave`, `Backups`, Windows reserved). |
| `SAVE_IN_USE` | Rename/delete of the currently-loaded save. Pass `force:true`. |
| `PATH_NOT_FOUND` | Unknown capability path. |
| `SCHEMA_INVALID` | Arguments don't match `input_schema`. |
| `DISPATCHER_BACKLOG` | Main-thread queue full; transient — retry. |
| `MAIN_THREAD_HUNG` | Main-thread didn't service the call within the command's timeout; permanent — don't retry. |
| `OP_ABORTED` | Handle was aborted by a scene/vessel change / forced load. Reason populated. |
| `HANDLE_NOT_FOUND` | Polled handle has been GC'd (retained 5 min after terminal). |
| `MODULE_HIDDEN` / `MODULE_NOT_UNLOCKED` | MechJeb module exists but is hidden/locked (career-mode R&D, etc.). |
| `PROTOCOL_VERSION_MISSING` / `PROTOCOL_VERSION_UNSUPPORTED` | Header issues. |
| `INTERNAL` | Unhandled exception. Bug; full stack in `KSP.log`. |
| `DEPRECATED_PATH` | Warning (not error). The result also carries `replaced_by`. |

## Op handle lifecycle

Long-running operations (autopilot engages, node execution) return `{handle, status:"running"}` from `mj_invoke` and live in the in-process `OpRegistry`.

```
PENDING → RUNNING → SUCCEEDED   (op.IsDone true, no error)
                  → FAILED      (op.IsDone true, ErrorCode set)
                  → CANCELLED   (mj_cancel)
                  → ABORTED     (GameEvent: scene/vessel/level change)
```

- One RUNNING handle per capability path. Second `mj_invoke` on same path returns `BUSY` with `current_handle`.
- Terminal handles retained 5 minutes after termination, cap 100. `HANDLE_NOT_FOUND` returned after that.
- `GameEvents.onVesselChange` / `onVesselSwitching` / `onGameSceneSwitchRequested` / `onGameStateLoad` / `onLevelWasLoaded` all synchronously abort all RUNNING handles with `reason` populated.

## Audit log

Every `tools/call` is appended to `<KspDir>/Logs/mechjeb-mcp-audit.log` as line-delimited JSON with **sentinel-newline framing** (`\n{json}\n`) so a torn write on KSP crash produces an obviously-skippable record rather than corrupting the JSONL.

Mutating calls (commands marked `Mutating` or `MutatingLongRunning`, `mj_invoke`, `mj_cancel`) get `FileStream.Flush(true)` per entry. Read-only calls (`mj_status`, `mj_discover`, `mj_read`, `mj_logs`, `mj_ops_list`, `mj_ops_status`) use buffered writes flushed on a 1-second timer.

Entry shape:

```json
{"request_id":"...","ts":"2026-05-10T16:42:01.123Z","client_addr":"127.0.0.1:54321",
 "tool":"mj_invoke","path":"autopilot/ascent/engage","args":{...},
 "result_code":"OK","latency_ms":12,"mutating":true}
```

Args are truncated at 4 KB; oversized args appear as `"...[truncated]"`. Do not share this log when filing bug reports — it captures every argument.

## Surface versioning

- `mj_discover` responses include `surface_version` at every node (read from `[McpCommand(Version=...)]` / `[McpProperty(Version=...)]`, defaults to `1.0.0`).
- `server_instance_id` is a GUID generated once per addon `Awake`. Any change (hot-reload, scene-reload re-init) means the client must re-discover before invoking.

## Source of truth

The full `dev/dump_surface` output is regenerated into `docs/mcp/SURFACE.md` per release. The C# source in `MechJeb2/Mcp/*.cs` is authoritative; this protocol doc is hand-written.
