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
            // overrideInputLock=true — default false is silently rejected when
            // the scene has any input lock active (very common).
            FlightGlobals.fetch.SetVesselTarget(match, true);
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
            // overrideInputLock=true — default false is silently rejected when
            // the scene has any input lock active (very common).
            FlightGlobals.fetch.SetVesselTarget(match, true);
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
        public sealed class AsteroidDto
        {
            public uint persistent_id;
            public string name;
            public string body;                  // current parent body (usually Sun)
            public double mass_t;                // approximate mass in tonnes
            public string size_class;            // A..E classification by mass
            public double sma_m;                 // heliocentric semi-major axis
            public double eccentricity;
            public double inclination_deg;
            public double apoapsis_m;
            public double periapsis_m;
            public double period_s;
            public double closest_kerbin_approach_ut;     // UT of next closest approach to Kerbin
            public double closest_kerbin_approach_dist_m; // distance (m) at that UT
            public bool will_enter_kerbin_soi;            // true if approach distance < Kerbin SOI
            public double t_minus_s;                      // seconds from now to closest approach
        }

        [McpCommand("tracking/list_asteroids",
            Description = "List all tracked asteroids (SpaceObject-type vessels). For each, computes the next " +
                          "closest approach to Kerbin and flags whether that pass enters Kerbin's SOI " +
                          "(the asteroid is capturable in the next pass). Use this to find a Kerbin-encounter " +
                          "asteroid for a capture/grappling mission.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static List<AsteroidDto> ListAsteroids()
        {
            var result = new List<AsteroidDto>();
            if (FlightGlobals.Vessels == null) return result;
            CelestialBody kerbin = null;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b != null && b.bodyName == "Kerbin") { kerbin = b; break; }
            }
            double now = Planetarium.GetUniversalTime();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.vesselType != VesselType.SpaceObject) continue;
                Orbit o = v.orbit;
                var dto = new AsteroidDto
                {
                    persistent_id = v.persistentId,
                    name = v.GetDisplayName() ?? v.vesselName ?? "?",
                    body = v.mainBody?.bodyName ?? "?",
                    mass_t = v.GetTotalMass(),
                };
                // Size class by mass (KSP stock):
                //  A: ~3-10t, B: ~20-50t, C: ~100-300t, D: ~600-1500t, E: ~3000-5000t
                double m = dto.mass_t;
                dto.size_class = m < 20 ? "A" : m < 100 ? "B" : m < 600 ? "C" : m < 3000 ? "D" : "E";
                if (o != null)
                {
                    dto.sma_m = o.semiMajorAxis;
                    dto.eccentricity = o.eccentricity;
                    dto.inclination_deg = o.inclination;
                    dto.apoapsis_m = o.ApR;
                    dto.periapsis_m = o.PeR;
                    dto.period_s = double.IsInfinity(o.period) ? -1 : o.period;
                    if (kerbin != null && kerbin.orbit != null)
                    {
                        // If the asteroid is already orbiting Kerbin, encounter is "now"
                        if (v.mainBody == kerbin)
                        {
                            dto.closest_kerbin_approach_ut = now;
                            dto.closest_kerbin_approach_dist_m = 0;
                            dto.will_enter_kerbin_soi = true;
                            dto.t_minus_s = 0;
                        }
                        else
                        {
                            double caUT = o.NextClosestApproachTime(kerbin.orbit, now + 1);
                            Vector3d astAt = o.getPositionAtUT(caUT);
                            Vector3d kerAt = kerbin.orbit.getPositionAtUT(caUT);
                            double dist = (astAt - kerAt).magnitude;
                            dto.closest_kerbin_approach_ut = caUT;
                            dto.closest_kerbin_approach_dist_m = dist;
                            dto.will_enter_kerbin_soi = dist < kerbin.sphereOfInfluence;
                            dto.t_minus_s = caUT - now;
                        }
                    }
                }
                result.Add(dto);
            }
            return result;
        }
    }
}
