namespace MuMech.Mcp
{
    // Common maneuver-planner verbs. v1 ships the high-leverage subset
    // (circularize, change apsis); the full 20-Operation catalog is
    // deferred until the LLM actually asks for it. Each verb places a
    // maneuver node directly via Vessel.PlaceManeuverNode using
    // OrbitalManeuverCalculator math — same path the in-game Maneuver
    // Planner takes, just programmatic.
    [McpDescription("Place maneuver nodes via MechJeb's orbital math. Pair with autopilot/node_executor/execute_next.", Version = "1.0.0")]
    public static class ManeuverPlannerCapability
    {
        public sealed class NodeDto
        {
            public double ut;
            public double dv_total_mps;
            public double dv_prograde_mps;
            public double dv_normal_mps;
            public double dv_radial_mps;
            public string description;
        }

        [McpCommand("maneuver/circularize_at_apoapsis",
            Description = "Plan a circularization burn at the next apoapsis.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto CircularizeAtApoapsis()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null) throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            double ut = v.orbit.NextApoapsisTime(Planetarium.GetUniversalTime());
            return PlaceCircularize(v, ut, "circularize at apoapsis");
        }

        [McpCommand("maneuver/circularize_at_periapsis",
            Description = "Plan a circularization burn at the next periapsis.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto CircularizeAtPeriapsis()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null) throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            double ut = v.orbit.NextPeriapsisTime(Planetarium.GetUniversalTime());
            return PlaceCircularize(v, ut, "circularize at periapsis");
        }

        [McpCommand("maneuver/change_apoapsis",
            Description = "Plan a burn at the next periapsis to raise/lower the apoapsis to the target altitude (m ASL).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto ChangeApoapsis(
            [McpParam(Description = "New apoapsis altitude above sea level, in meters.")]
            double apoapsis_m)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null || v.mainBody == null)
                throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            double ut = v.orbit.NextPeriapsisTime(Planetarium.GetUniversalTime());
            double bodyRadius = v.mainBody.Radius;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangeApoapsis(v.orbit, ut, bodyRadius + apoapsis_m);
            ManeuverNode node = v.PlaceManeuverNode(v.orbit, dv, ut);
            return DescribeNode(node, dv, ut, "change apoapsis at periapsis to " + apoapsis_m + "m");
        }

        [McpCommand("maneuver/change_periapsis",
            Description = "Plan a burn at the next apoapsis to raise/lower the periapsis to the target altitude (m ASL).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto ChangePeriapsis(
            [McpParam(Description = "New periapsis altitude above sea level, in meters.")]
            double periapsis_m)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null || v.mainBody == null)
                throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            double ut = v.orbit.NextApoapsisTime(Planetarium.GetUniversalTime());
            double bodyRadius = v.mainBody.Radius;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangePeriapsis(v.orbit, ut, bodyRadius + periapsis_m);
            ManeuverNode node = v.PlaceManeuverNode(v.orbit, dv, ut);
            return DescribeNode(node, dv, ut, "change periapsis at apoapsis to " + periapsis_m + "m");
        }

        [McpCommand("maneuver/change_inclination",
            Description = "Plan an inclination change at the next AN or DN (whichever comes first).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto ChangeInclination(
            [McpParam(Description = "Target inclination in degrees.")]
            double inclination_deg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null) throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            double now = Planetarium.GetUniversalTime();
            double tAN = v.orbit.TimeOfAscendingNodeEquatorial(now);
            double tDN = v.orbit.TimeOfDescendingNodeEquatorial(now);
            double ut = tAN < tDN ? tAN : tDN;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangeInclination(v.orbit, ut, inclination_deg);
            ManeuverNode node = v.PlaceManeuverNode(v.orbit, dv, ut);
            return DescribeNode(node, dv, ut, "change inclination to " + inclination_deg + "°");
        }

        [McpCommand("maneuver/clear_nodes",
            Description = "Remove all maneuver nodes from the current vessel's flight plan.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject ClearNodes()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.patchedConicSolver == null) return new JsonObject().Set("cleared", 0);
            int n = v.patchedConicSolver.maneuverNodes?.Count ?? 0;
            // RemoveAll is safest — node.RemoveSelf() mutates the list mid-iteration.
            while (v.patchedConicSolver.maneuverNodes != null && v.patchedConicSolver.maneuverNodes.Count > 0)
            {
                v.patchedConicSolver.maneuverNodes[0].RemoveSelf();
            }
            return new JsonObject().Set("cleared", n);
        }

        // -- helpers --------------------------------------------------------
        private static NodeDto PlaceCircularize(Vessel v, double ut, string description)
        {
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToCircularize(v.orbit, ut);
            ManeuverNode node = v.PlaceManeuverNode(v.orbit, dv, ut);
            return DescribeNode(node, dv, ut, description);
        }

        private static NodeDto DescribeNode(ManeuverNode node, Vector3d dv, double ut, string description)
        {
            return new NodeDto
            {
                ut = ut,
                dv_total_mps = dv.magnitude,
                dv_prograde_mps = node?.DeltaV.z ?? 0,
                dv_normal_mps = node?.DeltaV.y ?? 0,
                dv_radial_mps = node?.DeltaV.x ?? 0,
                description = description,
            };
        }
    }
}
