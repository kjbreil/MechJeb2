# MechJeb MCP Server

An in-process HTTP MCP (Model Context Protocol) server hosted inside the MechJeb2 KSP mod. Lets an LLM client (Claude Code, Claude Desktop, Cursor, anything that speaks MCP 2025-06-18 over Streamable HTTP) drive MechJeb's autopilots, inspect the active vessel, search logs, and manage save games.

**Disabled by default.** Opt-in flip required before anything binds.

## Threat model in one paragraph

The server binds to `127.0.0.1` only, with a Host-header check (DNS-rebinding defense), Origin allowlist (loopback only by default), strict `Content-Type: application/json`, and a 1MB body cap. There is no authentication token by default — any local process running as your user can call any verb. If you run untrusted code on the same machine (npm postinstall scripts, browser extensions, sketchy VSCode extensions), turn the `auth_token` knob on in settings. Every call is recorded to `<KspDir>/Logs/mechjeb-mcp-audit.log`. Do not share the audit log when bug-reporting (it captures every argument).

## Enable it

After installing MechJeb2 normally, edit (or let the addon create) this file:

```
<KspDir>/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp_settings.cfg
```

Set `enabled = True`:

```
McpServerSettings
{
	enabled = True
	port = 17653
	port_scan_range = 10
	allow_null_origin = False
	auth_token =
}
```

Restart KSP. In `KSP.log` you'll see a line like:

```
[MechJeb-MCP] Listening on http://127.0.0.1:17653/mcp/ (instance <guid>, commands=N, properties=M). Awake total Xms.
```

The exact URL is also written to `<KspDir>/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp-endpoint.json` for client discovery.

## Connect from Claude Code

```
claude mcp add mechjeb http://127.0.0.1:17653/mcp/
```

(Substitute the port from your `mcp-endpoint.json` if the scan picked a different one.)

Then in any conversation:

> Use `mj_status` to check the current KSP state.

Claude will call the tool, see the scene, active vessel, and the registered command/property counts.

## The tool surface

Eight top-level MCP tools. The capability tree behind them is grown via attributes in C# — adding a new verb is `[McpCommand("path/to/verb")]` on a static method.

| Tool | Use |
|---|---|
| `mj_status` | Call this first every session. KSP scene + active vessel + UT + time-warp + save folder + ClearToSave + registry/op counts. |
| `mj_discover` | Walk the capability tree. Empty path = root children. Leaf path = full input/output schema. |
| `mj_invoke` | Run a capability. Args must satisfy `mj_discover`'s `input_schema` for that path. Returns the result, or `{handle, status}` for long-running ops. |
| `mj_read` | Read a property path. Cheap; safe to poll. |
| `mj_logs` | Filtered search over MechJeb's in-memory log ring buffer and/or `KSP.log`. Defaults to `mechjeb` stream to avoid exposing other mods' output. |
| `mj_cancel` | Cancel a running op by handle. |
| `mj_ops_list` | List all live + recently-terminated handles. |
| `mj_ops_status` | Poll a single handle for status / result / error. |

## What you can do today (v1.0.0-phase6)

- **Inspect the vessel** — `mj_invoke vessel/summary` returns mass, TWR, orbit, control state, per-resource totals, per-stage ΔV and burn-time (via MechJeb's StageStats). Pass `include_parts:true` for the full part list.
- **System orientation** — `mj_invoke system/bodies` returns the solar system. `mj_invoke vessels/loaded` lists loadable target vessels.
- **Launch to orbit** — set `autopilot/ascent/desired_orbit_altitude_m` and `desired_inclination_deg`, then `mj_invoke autopilot/ascent/engage`. Poll the returned handle. Refuses WARP_TOO_HIGH if you forgot to drop warp.
- **Plan maneuvers** — `maneuver/circularize_at_apoapsis`, `maneuver/change_apoapsis`, `maneuver/change_periapsis`, `maneuver/change_inclination`, `maneuver/clear_nodes`. Each places a single node.
- **Execute nodes** — `autopilot/node_executor/execute_next`, `execute_all`, `abort`.
- **Hold attitude** — `attitude/smartass/engage` with a Mode + Target enum.
- **Live polling during burns** — `mj_read vessel/attitude_error_deg`, `vessel/throttle`, `autopilot/ascent/t_minus_s`.
- **Targets** — `target/set_vessel(persistent_id)`, `target/set_body(body_name)`, `target/clear`, `target/current`.
- **Emergencies** — `emergency/panic`, `emergency/land_somewhere`, `emergency/land_at_ksc`.
- **Saves** — `saves/list`, `saves/metadata`, `saves/quicksave`, `saves/save`, `saves/create`, `saves/copy`, `saves/rename`, `saves/delete` (rename-to-trash), `saves/load` (force-flag re-checks ClearToSave after disengaging MCP), `saves/quickload`.
- **Logs** — `mj_logs {stream:"mechjeb"}` to grep MechJeb's own log lines. `stream:"both"` adds `KSP.log` from disk.
- **Audit log** — `cat <KspDir>/Logs/mechjeb-mcp-audit.log` for the full audit trail.

## Operational notes

- **Op handles do not survive `saves/load`.** The save-load path synchronously aborts all running handles before LoadGame runs. Don't cache handle strings across loads.
- **Errors flow in two channels.** Protocol errors (parse, invalid request, method-not-found) are JSON-RPC `error` envelopes. Tool execution errors come back as `result.isError:true` with `structuredContent.error_code`. The LLM should read the `error_code` for retry decisions; the `message` is phrased as a recovery hint.
- **MechJeb settings are shared across saves.** `saves/copy` of a save that contains a vessel whose name matches another save will produce drift in MechJeb-side settings. Pre-existing MechJeb design quirk; documented here so you don't get surprised.
- **HttpListener on Mono macOS has known shutdown quirks.** The addon uses `GetContextAsync` + `Close()`-before-`Stop()` to release ports cleanly. If KSP crashes mid-request, the OS reclaims the port at process exit.

## Smoke test it

```
cd <repo>/scripts/mcp && ./smoke.sh
```

Auto-discovers the bound URL from `mcp-endpoint.json` and runs through `initialize` / `tools/list` / `tools/call dev_ping` plus negative cases (bad JSON, wrong Host, missing protocol version, wrong content type).

## Related docs

- `docs/mcp/PROTOCOL.md` — wire spec: initialize handshake, error code enum, audit-log schema, op-handle lifecycle.
- `docs/mcp/SURFACE.md` — auto-generated full discover-tree dump. Regenerate with `dev/dump_surface` MCP call.
- `docs/plans/2026-05-10-001-feat-mechjeb-mcp-server-plan.md` — the implementation plan with deep-review refinements.
- `docs/brainstorms/2026-05-10-mechjeb-mcp-server-brainstorm.md` — the design decisions.
