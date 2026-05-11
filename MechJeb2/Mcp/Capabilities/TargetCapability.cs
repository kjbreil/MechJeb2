using System.Collections.Generic;

namespace MuMech.Mcp
{
    [McpDescription("Target selection — pick the vessel/body the LLM is rendezvousing with or pointing at.", Version = "1.0.0")]
    public static class TargetCapability
    {
        public sealed class TargetDto
        {
            public string type;        // VESSEL / BODY / NONE
            public string name;
            public uint? persistent_id;
            public string body;
            public bool present;
        }

        [McpCommand("target/current",
            Description = "Returns the currently-selected target, if any.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static TargetDto Current()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");
            ITargetable t = v.targetObject;
            if (t == null) return new TargetDto { type = "NONE", present = false };
            if (t is Vessel tv)
            {
                return new TargetDto
                {
                    type = "VESSEL",
                    name = tv.GetDisplayName() ?? tv.vesselName ?? "?",
                    persistent_id = tv.persistentId,
                    body = tv.mainBody?.bodyName ?? "?",
                    present = true,
                };
            }
            if (t is CelestialBody tb)
            {
                return new TargetDto { type = "BODY", name = tb.bodyName ?? "?", present = true };
            }
            return new TargetDto { type = "OTHER", name = t.GetName() ?? "?", present = true };
        }

        [McpCommand("target/clear",
            Description = "Clear the current target.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject Clear()
        {
            FlightGlobals.fetch.SetVesselTarget((Vessel)null);
            return new JsonObject().Set("cleared", true);
        }

        [McpCommand("target/set_vessel",
            Description = "Select a vessel as the target by persistent_id (see vessels/loaded).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static TargetDto SetVessel(uint persistent_id)
        {
            Vessel match = null;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v != null && v.persistentId == persistent_id) { match = v; break; }
            }
            if (match == null) throw new McpException(ErrorCode.NameNotFound, "No vessel with persistent_id " + persistent_id);
            FlightGlobals.fetch.SetVesselTarget(match);
            return Current();
        }

        [McpCommand("target/set_body",
            Description = "Select a celestial body as the target by name.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static TargetDto SetBody(string body_name)
        {
            CelestialBody match = null;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b != null && string.Equals(b.bodyName, body_name, System.StringComparison.OrdinalIgnoreCase))
                { match = b; break; }
            }
            if (match == null) throw new McpException(ErrorCode.NameNotFound, "No body named " + body_name);
            FlightGlobals.fetch.SetVesselTarget(match);
            return Current();
        }

        public sealed class LoadedVesselDto
        {
            public uint persistent_id;
            public string name;
            public string situation;
            public string vessel_type;
            public string body;
            public bool is_active;
        }

        [McpCommand("vessels/loaded",
            Description = "List all loaded vessels in the current scene. The active vessel is flagged. " +
                          "Use persistent_id with target/set_vessel.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static List<LoadedVesselDto> ListLoadedVessels()
        {
            var result = new List<LoadedVesselDto>();
            if (FlightGlobals.Vessels == null) return result;
            uint activeId = FlightGlobals.ActiveVessel?.persistentId ?? 0;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null) continue;
                result.Add(new LoadedVesselDto
                {
                    persistent_id = v.persistentId,
                    name = v.GetDisplayName() ?? v.vesselName ?? "?",
                    situation = v.situation.ToString(),
                    vessel_type = v.vesselType.ToString(),
                    body = v.mainBody?.bodyName ?? "?",
                    is_active = v.persistentId == activeId,
                });
            }
            return result;
        }
    }
}
