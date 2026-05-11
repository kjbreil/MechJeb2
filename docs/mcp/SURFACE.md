# MechJeb MCP Capability Surface

> **Auto-generated dump of every registered command and property.** This file is regenerated per release.
>
> To regenerate against a live KSP instance:
>
> 1. Start KSP with the MCP server enabled (`mcp_settings.cfg` → `enabled = True`).
> 2. From any MCP client (e.g. `claude mcp`):
>    ```
>    mj_invoke dev/dump_surface
>    ```
> 3. Pipe the result through `scripts/mcp/gen-surface-doc.sh` (or pretty-print the JSON manually and paste
>    below). The `dev/dump_surface` JSON shape is defined in
>    `MechJeb2/Mcp/Capabilities/DevCapability.cs` (`SurfaceDumpDto`).
>
> The C# attributes in `MechJeb2/Mcp/Capabilities/*.cs` (and any other file containing
> `[McpCommand]` / `[McpProperty]`) are the authoritative source.

## Current surface (v1.0.0-phase6)

This is a hand-summarized index of paths registered at the time of release. Use `mj_discover` from a
live client to get the full, current input/output schemas — those are not duplicated here.

### Commands

| Path | Side effect | Required scene | Description |
|---|---|---|---|
| `autopilot/ascent/engage` | MutatingLongRunning | FLIGHT | Engage the ascent autopilot. Returns a handle. Refuses WARP_TOO_HIGH if warp > 1×. |
| `autopilot/ascent/abort` | Mutating | FLIGHT | Disengage MCP from the ascent autopilot's user pool. |
| `autopilot/node_executor/execute_next` | MutatingLongRunning | FLIGHT | Execute the next maneuver node. Returns a handle. |
| `autopilot/node_executor/execute_all` | MutatingLongRunning | FLIGHT | Execute all queued maneuver nodes. Returns a handle. |
| `autopilot/node_executor/abort` | Mutating | FLIGHT | Disengage MCP from the node executor. |
| `attitude/smartass/engage` | Mutating | FLIGHT | Hold attitude. Mode + Target enums. |
| `attitude/smartass/disengage` | Mutating | FLIGHT | Disengage SmartASS. |
| `maneuver/circularize_at_apoapsis` | Mutating | FLIGHT | Plan a node to circularize at next apoapsis. |
| `maneuver/circularize_at_periapsis` | Mutating | FLIGHT | Plan a node to circularize at next periapsis. |
| `maneuver/change_apoapsis` | Mutating | FLIGHT | Plan a node to raise/lower apoapsis to a target altitude. |
| `maneuver/change_periapsis` | Mutating | FLIGHT | Plan a node to raise/lower periapsis to a target altitude. |
| `maneuver/change_inclination` | Mutating | FLIGHT | Plan a node to change inclination. |
| `maneuver/clear_nodes` | Mutating | FLIGHT | Remove all maneuver nodes. |
| `emergency/panic` | Mutating | FLIGHT | Disable all SAS/throttle/autopilots. |
| `emergency/land_somewhere` | MutatingLongRunning | FLIGHT | Engage landing autopilot to land where you are. |
| `emergency/land_at_ksc` | MutatingLongRunning | FLIGHT | Engage landing autopilot targeting KSC. |
| `target/set_vessel` | Mutating | FLIGHT | Set a target by persistent_id. |
| `target/set_body` | Mutating | FLIGHT | Set a target by body name. |
| `target/clear` | Mutating | FLIGHT | Clear the target. |
| `target/current` | ReadOnly | FLIGHT | Read the current target. |
| `vessel/summary` | ReadOnly | FLIGHT | Mass, TWR, orbit, control state, resources, per-stage ΔV/burn-time. `include_parts:true` for full part list. |
| `vessels/loaded` | ReadOnly | FLIGHT | List loadable target vessels. |
| `system/bodies` | ReadOnly | (any) | Solar system bodies. |
| `system/version` | ReadOnly | (any) | KSP + MechJeb + server versions. |
| `saves/list` | ReadOnly | (any) | List saves in the current campaign folder. |
| `saves/metadata` | ReadOnly | (any) | Per-save metadata (UT, vessels, science, funds). |
| `saves/quicksave` | Mutating | FLIGHT | Quicksave. |
| `saves/save` | Mutating | (any) | Save to the named slot. |
| `saves/create` | Mutating | (any) | Create a new named save. |
| `saves/copy` | Mutating | (any) | Copy a save. |
| `saves/rename` | Mutating | (any) | Rename a save (rename-to-trash for the loaded one). |
| `saves/delete` | Mutating | (any) | Delete a save (rename-to-trash). |
| `saves/load` | Mutating | (any) | Load a save. `force:true` re-checks ClearToSave after disengaging MCP. |
| `saves/quickload` | Mutating | FLIGHT | Quickload. |
| `dev/dump_surface` | ReadOnly | (any) | Dump every registered command and property as a flat list. Used to regenerate this doc. |

### Properties

| Path | Access | Description |
|---|---|---|
| `autopilot/ascent/status` | Read | Live status string from the active ascent autopilot. |
| `autopilot/ascent/t_minus_s` | Read | Seconds until liftoff during launch countdown. |
| `autopilot/ascent/type` | Read/Write | CLASSIC or PSG. |
| `autopilot/ascent/desired_orbit_altitude_m` | Read/Write | Target orbit altitude (m ASL). |
| `autopilot/ascent/desired_inclination_deg` | Read/Write | Target inclination (deg). |
| `autopilot/ascent/desired_apoapsis_m` | Read/Write | PSG-mode target apoapsis altitude (m ASL). |
| `autopilot/ascent/turn_start_altitude_m` | Read/Write | Classic-mode gravity-turn start altitude (m). |
| `autopilot/ascent/turn_end_altitude_m` | Read/Write | Classic-mode gravity-turn end altitude (m). |
| `autopilot/ascent/turn_end_angle_deg` | Read/Write | Classic-mode pitch at gravity-turn end (deg). |
| `autopilot/ascent/autostage` | Read/Write | Auto-stage during ascent. |
| `autopilot/ascent/skip_circularization` | Read/Write | Skip the circularization burn at apoapsis. |
| `autopilot/ascent/corrective_steering` | Read/Write | Corrective steering during gravity turn. |
| `autopilot/ascent/force_roll` | Read/Write | Force a specific roll during ascent. |
| `autopilot/node_executor/state` | Read | RUNNING / FINISHED / IDLE. |
| `autopilot/node_executor/autowarp` | Read/Write | Auto-warp to the node. |
| `autopilot/node_executor/lead_time_s` | Read/Write | Lead time before node burn. |
| `autopilot/node_executor/rcs_only` | Read/Write | Use RCS only. |
| `vessel/attitude_error_deg` | Read | Attitude error (deg). |
| `vessel/throttle` | Read | Current throttle [0..1]. |
| `vessel/time_warp_rate` | Read | Current time-warp rate. |
| `vessel/time_warp_mode` | Read | LOW (physics) / HIGH (on-rails). |
| `vessel/sas_engaged` | Read | Stock SAS enabled. |

## Versioning

Every entry is decorated with `Version` (per `[McpCommand(Version="…")]` / `[McpProperty(Version="…")]`),
default `1.0.0`. `mj_discover` exposes `surface_version` at every node so a client can detect a node
upgrading without re-discovering siblings. The `server_instance_id` GUID returned by `mj_status` /
`mj_discover` changes on any addon hot-reload or scene-reload re-init — the client must re-discover.
