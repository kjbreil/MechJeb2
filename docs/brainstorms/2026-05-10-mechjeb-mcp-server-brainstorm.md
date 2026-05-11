# Brainstorm: MechJeb MCP Server

**Date:** 2026-05-10
**Status:** Brainstorm — ready for `/ce:plan`
**Approach:** B — Attribute-driven registry behind a discover+invoke HTTP MCP surface

## What We're Building

An HTTP MCP server hosted inside the MechJeb2 KSP mod that lets an LLM (primarily Claude as the operator's copilot) discover, read, and invoke MechJeb capabilities, inspect the live active vessel, search MechJeb and KSP logs, and manage save games.

The wire surface is a small set of top-level MCP tools. The capability tree behind them is grown via `[McpCommand]` / `[McpProperty]` attributes annotated on MechJeb modules — adding a new capability is "annotate a method, rebuild." The LLM orients itself by calling `discover` on any subtree before invoking.

This is not a kRPC replacement. It is a MechJeb-shaped interface to MechJeb-shaped things, plus the narrow KSP primitives (saves, active-vessel summary) needed to make a copilot conversation work end-to-end.

## Why This Approach

- **Discover + invoke** keeps the LLM's tool list tiny (~3–5 entries) while still letting it learn the typed schema of any subtree on demand. Avoids both the "one giant untyped dispatcher" trap and the "30 namespaced tools eating the context window" trap.
- **Attribute-driven registry** pays for itself the second time we expose a module. Hand-rolling per-command handlers (Approach A) ships faster but produces a backlog of "Claude, can you engage the docking autopilot? — no, not exposed yet" sessions. With attributes, exposing a new module is mechanical and the schema stays consistent.
- **Trust-the-LLM safety** is acceptable because the operator is in the chat. Every mutating call is recorded in an audit log; the operator sees Claude's intent in the conversation before it lands.
- **MechJeb-only host (not a separate sidecar process)** matches the "small top-level surface" framing and keeps the install one mod. Sidecar can be added later if we ever need stdio MCP transport.

## Key Decisions

### Scope: MechJeb + selective KSP
The MCP exposes MechJeb's autopilots, modules, settings, MechJebLib computations, and a curated set of KSP primitives needed for the copilot loop: live active vessel introspection (parts, fuel/dV per stage, mass, TWR, orbit, control state), full save management (list/load/delete/create/copy/quicksave/quickload), and log access. It does **not** try to be a general KSP control surface — staging, action groups, EVA, scene transitions, etc. are out of scope unless they turn out to be required for autopilot ergonomics.

### Primary consumer: human operator + Claude as copilot
Sessions are interactive and short. No long-running autonomous mission state machines in v1. Polling is acceptable; event subscriptions are deferred. Long-running operations (e.g. "execute next maneuver node") are fire-and-forget with a status path under `discover`.

### Wire shape: JSON-RPC 2.0 over HTTP, discover + invoke
Transport is **JSON-RPC 2.0** at a single POST endpoint (`POST /mcp`) for MCP-spec compatibility with Claude Desktop, Cursor, Claude Code's HTTP MCP support, etc. Top-level tools registered via `tools/list`:
- `mj_discover(path?)` — return **JSON Schema** for a subtree (parameters, return shape, side-effect class, description) plus `surface_version` (semver). Schemas are generated automatically from C# parameter types + `[McpCommand]` description strings.
- `mj_invoke(path, args)` — call a verb. Returns `{result}` for sync ops or `{handle, status:"running"}` for long-running ops.
- `mj_read(path)` — read a property/state node (separated from invoke so the LLM can introspect cheaply).
- `mj_logs(filter)` — log search (see below).
- `mj_status(handle)` — poll a long-running op; also reachable as `mj_read("ops.<handle>")`.

Paths are dotted: `autopilot.ascent.engage`, `vessel.summary`, `saves.quickload`.

### Long-running ops: handle + poll
`mj_invoke` on an autopilot verb (e.g. `autopilot.node_executor.execute_next`) returns `{handle, status:"running"}`. Operator/Claude polls via `mj_status(handle)` or `mj_read("ops.<handle>")`. Cancel via `mj_invoke("ops.<handle>.cancel")`. At most one active op per autopilot path (mirrors MechJeb's existing singleton coordination). Handle lifetime: live while running + 5 minutes after terminal state for result retrieval, then GC'd.

### Capability registration: attribute-driven
MechJeb modules annotate their MCP-exposed verbs/state with attributes:

- `[McpCommand("autopilot/ascent/engage", description="…")]` on a method
- `[McpProperty("autopilot/ascent/desired_altitude", access=ReadWrite)]` on a property
- `[McpDescribe]` on a module class for the parent node's summary

A startup registry walks loaded assemblies, builds the discover tree, and binds invoke calls to the annotated members. Parameter binding generates schema from C# parameter types; descriptions come from attribute strings. Unannotated MechJeb internals stay invisible.

### Logs: structured MechJeb stream, plus KSP.log access
Two log surfaces, one search interface:
- **MechJeb stream (primary):** A ring buffer captured at the source — every `Debug.Log`/`print` from MechJeb code is tagged with the originating module, level, and timestamp and stored as a structured record. Returned as JSON.
- **KSP.log (secondary):** Filterable substring/regex search over the on-disk game log, for catching engine/physics/other-mod messages that aren't in the MechJeb stream.

Filters: `module`, `level`, `since/until`, `substring` or `regex`, `around_event` (return N records before/after a matching record). Default response is capped and paginated so a wide-open query doesn't dump megabytes.

### Crafts: live active vessel only
`vessel.summary` returns the active vessel's part tree (collapsed by stage), per-stage fuel and ΔV, total mass, current TWR, engaged MechJeb modules, current orbit, and control state. Other loaded vessels, `.craft` files, and persistence-only vessels are explicitly out of scope for v1.

### Saves: full management
Verbs under `saves.*`:
- `list`, `load`, `delete`, `create` (empty save / clone current), `copy`, `rename`
- `quicksave`, `quickload`
- `metadata(name)` — return save UT, money/science (if applicable), vessel count, etc.

These are powerful and destructive. Per the safety decision, they run on a trust-and-log basis.

### Hosting: in-process inside KSP
A C# `HttpListener` started by `MechJebCore` on load, bound to `127.0.0.1` only, no token. One mod to install. Threading bridge marshals incoming requests onto the Unity main thread before they touch vessel/game state. No sidecar process in v1; can be added later if stdio MCP transport becomes valuable.

### v1 P0 capability surface
The starter set of annotated modules that must ship at v1:
- `autopilot.ascent` (classic + PSG): engage, configure, status, abort
- `autopilot.maneuver_planner`: plan Hohmann / circularize / inclination change / etc.
- `autopilot.node_executor`: execute_next, status, abort
- `attitude.smartass`: hold prograde/retrograde/normal/target/custom, status
- `vessel.summary`: live active vessel introspection
- `saves.*`: full save management (list/load/delete/create/copy/rename/quicksave/quickload/metadata)
- `logs.*`: structured MechJeb log + KSP.log search

Docking, rendezvous, hoverslam, landing autopilot, RCS balancer, flight recorder, etc. are deferred to v1.1+ — the attribute-driven registry makes adding them mechanical.

### Surface versioning
Every `mj_discover` response includes a `surface_version` field at the queried node (semver). Bump **minor** on additions, **major** on removals/renames. Deprecated paths return `deprecated: true` and stay live for one major version before removal. The version is reported per-node so a partial change in one subtree is visible without invalidating everything.

### Safety: trust + log everything
No confirmation gates. The server records every call (path, args, caller, timestamp, result) to an `mcp-audit.log`. The operator can replay/grep this after a session. Future option: per-path policy file. KSP's own backup folder is the disaster recovery for `saves.delete`.

## Resolved Questions

1. ~~Hosting model.~~ **In-process `HttpListener` inside KSP.** Sidecar deferred unless stdio MCP transport becomes valuable.
2. ~~Bind address and auth.~~ **`127.0.0.1` only, no token.** Trust the OS user boundary; single-user dev machine.
3. ~~MCP wire format.~~ **JSON-RPC 2.0** at `POST /mcp` for MCP-spec compatibility.
4. ~~Initial P0 capability list.~~ Ascent (classic + PSG), maneuver planner, node executor, attitude/SmartASS, vessel summary, full saves, log search. Docking/rendezvous/etc. are v1.1.
5. ~~Async semantics.~~ **Handle + poll.** `mj_invoke` returns `{handle, status:"running"}`; `mj_status(handle)` polls; `mj_invoke("ops.<handle>.cancel")` aborts. One active op per autopilot path. Handles live 5 minutes after terminal state.
6. ~~`discover` payload shape.~~ **JSON Schema**, auto-generated from C# parameter types and attribute descriptions. Standard tooling reusable.
7. ~~Versioning.~~ **Semver in every discover response** at the queried node. Deprecated paths flagged for one major version before removal.

## Out of Scope (for v1)

- Event subscriptions / WebSocket push (polling is fine for copilot sessions)
- Autonomous mission agents (different use case, different design)
- `.craft` file reading (defer until someone actually asks for pre-flight craft analysis)
- General KSP control beyond saves and active-vessel read (this is not kRPC)
- Read-only mode toggle (operator-in-the-loop + audit log is enough for now)
- Confirmation tokens / tiered safety (revisit if the audit log shows real near-misses)
- Multi-vessel orchestration
