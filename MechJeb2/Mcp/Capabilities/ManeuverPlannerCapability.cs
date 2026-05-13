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
        internal sealed class NodeProposal
        {
            public Orbit orbit;
            public double ut;
            public Vector3d worldDv;
            public string description;
            public string suggestedManeuverPath;

            public double DeltaVMagnitude => worldDv.magnitude;
        }

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
            NodeProposal proposal = ProposeCircularizeAtApoapsis(v);
            return PlaceProposal(v, proposal);
        }

        [McpCommand("maneuver/circularize_at_periapsis",
            Description = "Plan a circularization burn at the next periapsis.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto CircularizeAtPeriapsis()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            NodeProposal proposal = ProposeCircularizeAtPeriapsis(v);
            return PlaceProposal(v, proposal);
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
            NodeProposal proposal = ProposeChangeApoapsis(v, apoapsis_m);
            return PlaceProposal(v, proposal);
        }

        [McpCommand("maneuver/change_periapsis",
            Description = "Plan a burn to raise/lower the periapsis to the target altitude (m above the " +
                          "current reference body's surface). By default the node is placed at the next " +
                          "apoapsis (optimal for elliptical orbits). For hyperbolic orbits (Ap doesn't exist), " +
                          "pass burn_in_s to place the node `now + burn_in_s` seconds — required for in-SOI " +
                          "course corrections during a capture.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto ChangePeriapsis(
            [McpParam(Description = "New periapsis altitude above sea level, in meters.")]
            double periapsis_m,
            [McpParam(Description = "If set, place the node at now + this many seconds instead of next apoapsis.")]
            double burn_in_s = 0)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            NodeProposal proposal;
            if (burn_in_s > 0)
            {
                EnsureOrbitAndBody(v);
                double ut = Planetarium.GetUniversalTime() + burn_in_s;
                double bodyRadius = v.mainBody.Radius;
                Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangePeriapsis(v.orbit, ut, bodyRadius + periapsis_m);
                proposal = new NodeProposal
                {
                    orbit = v.orbit,
                    ut = ut,
                    worldDv = dv,
                    description = "change periapsis (in " + burn_in_s + "s) to " + periapsis_m + "m",
                    suggestedManeuverPath = "maneuver/change_periapsis",
                };
            }
            else
            {
                proposal = ProposeChangePeriapsis(v, periapsis_m);
            }
            return PlaceProposal(v, proposal);
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
            NodeProposal proposal = ProposeChangeInclination(v, inclination_deg);
            return PlaceProposal(v, proposal);
        }

        [McpCommand("maneuver/hohmann_transfer_to_target",
            Description = "Plan a Hohmann transfer to the current target (or a named body if body_name is given). " +
                          "Computes the optimal burn UT and prograde ΔV. With no body_name, uses the active " +
                          "target — refuses NO_TARGET if none is set.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto HohmannTransferToTarget(
            [McpParam(Description = "Optional: target a celestial body by name instead of using the active target.")]
            string body_name = null,
            [McpParam(Description = "Optional: target a vessel by persistent_id (use for asteroid intercepts).")]
            uint target_persistent_id = 0)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");
            Orbit targetOrbit;
            string targetName;
            if (!string.IsNullOrEmpty(body_name))
            {
                CelestialBody body = ResolveBody(body_name);
                targetOrbit = body.orbit;
                targetName = body.bodyName;
            }
            else if (target_persistent_id != 0)
            {
                Vessel tv = ResolveVessel(target_persistent_id);
                targetOrbit = tv.orbit;
                if (targetOrbit == null) throw new McpException(ErrorCode.NoTarget, "Target vessel has no orbit");
                targetName = tv.GetDisplayName() ?? tv.vesselName ?? ("#" + target_persistent_id);
            }
            else
            {
                ITargetable t = v.targetObject;
                if (t == null) throw new McpException(ErrorCode.NoTarget, "No target set");
                targetOrbit = t.GetOrbit();
                if (targetOrbit == null) throw new McpException(ErrorCode.NoTarget, "Target has no orbit");
                targetName = t is CelestialBody cb ? cb.bodyName : t.GetName();
            }
            // Sanity: both orbits must share a parent body. For asteroids on heliocentric
            // orbits that haven't yet entered Kerbin's SOI, the math gives garbage — refuse
            // until the SOI transition happens (warp using vessel/warp_to_soi_change or
            // vessel/warp_to_ut to the asteroid's expected encounter time).
            if (targetOrbit.referenceBody != v.orbit.referenceBody)
                throw new McpException(ErrorCode.SchemaInvalid,
                    "Vessel orbits " + v.orbit.referenceBody?.bodyName +
                    " but target orbits " + targetOrbit.referenceBody?.bodyName +
                    " — Hohmann math requires the same parent body. Warp until SOI transition first.");
            EnsureOrbit(v);
            double now = Planetarium.GetUniversalTime();
            (Vector3d dv1, double ut1, _, _) = OrbitalManeuverCalculator
                .DeltaVAndTimeForHohmannTransfer(v.orbit, targetOrbit, now);
            return PlaceProposal(v, new NodeProposal
            {
                orbit = v.orbit,
                ut = ut1,
                worldDv = dv1,
                description = "Hohmann transfer to " + targetName,
                suggestedManeuverPath = "maneuver/hohmann_transfer_to_target",
            });
        }

        private static CelestialBody ResolveBody(string name)
        {
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b != null && string.Equals(b.bodyName, name, System.StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            throw new McpException(ErrorCode.NameNotFound, "No body named " + name);
        }

        private static Vessel ResolveVessel(uint persistent_id)
        {
            if (FlightGlobals.Vessels == null)
                throw new McpException(ErrorCode.NameNotFound, "No vessels available");
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v != null && v.persistentId == persistent_id) return v;
            }
            throw new McpException(ErrorCode.NameNotFound, "No vessel with persistent_id " + persistent_id);
        }

        [McpCommand("maneuver/course_correction_to_target_body",
            Description = "Fine-tune the trajectory for a target celestial body so the SOI-entry periapsis " +
                          "ends up at the given altitude (m ASL above the target body's surface). Use mid-coast " +
                          "after a Hohmann transfer to dial in capture geometry. Refuses NO_TARGET or if the " +
                          "target is not a celestial body.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto CourseCorrectionToTargetBody(
            [McpParam(Description = "Desired periapsis altitude (m) above the target body's surface.")]
            double final_periapsis_altitude_m,
            [McpParam(Description = "Optional: name the target body explicitly (bypasses active target).")]
            string body_name = null)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");
            CelestialBody body;
            if (!string.IsNullOrEmpty(body_name))
            {
                body = ResolveBody(body_name);
            }
            else
            {
                ITargetable t = v.targetObject;
                if (t == null) throw new McpException(ErrorCode.NoTarget, "No target set");
                body = t as CelestialBody;
                if (body == null) throw new McpException(ErrorCode.NoTarget, "Target is not a celestial body");
            }
            EnsureOrbit(v);
            double now = Planetarium.GetUniversalTime();
            double finalPeR = body.Radius + final_periapsis_altitude_m;
            Vector3d dv = OrbitalManeuverCalculator
                .DeltaVAndTimeForCheapestCourseCorrection(v.orbit, now, body.orbit, body, finalPeR, out double burnUT);
            return PlaceProposal(v, new NodeProposal
            {
                orbit = v.orbit,
                ut = burnUT,
                worldDv = dv,
                description = "course correction: " + body.bodyName + " periapsis to " + final_periapsis_altitude_m + "m",
                suggestedManeuverPath = "maneuver/course_correction_to_target_body",
            });
        }

        [McpCommand("maneuver/interplanetary_transfer",
            Description = "Plan an interplanetary ejection burn from a low orbit around the current parent " +
                          "body (e.g. LKO around Kerbin) toward a target body that orbits the same star. " +
                          "Computes the correct burn time, direction, and ΔV considering the ejection angle " +
                          "and SOI turning. Use sync_phase_angle=true to time the intercept; false to just " +
                          "drop into a heliocentric orbit with the right SMA (use for SENTINEL telescope-style " +
                          "missions where you don't actually want to reach the target).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto InterplanetaryTransfer(
            [McpParam(Description = "Target body name (e.g. \"Eve\", \"Duna\", \"Moho\"). Must orbit the same star as the current parent body.")]
            string body_name,
            [McpParam(Description = "If true, wait for the right phase angle to intercept the target. " +
                                    "If false, eject immediately and accept whatever heliocentric orbit results.")]
            bool sync_phase_angle = false)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");
            EnsureOrbitAndBody(v);
            CelestialBody body = ResolveBody(body_name);
            if (body.orbit == null) throw new McpException(ErrorCode.SchemaInvalid, body_name + " has no orbit");
            // Sanity: target must orbit the same parent as our planet
            if (body.referenceBody != v.mainBody.referenceBody)
                throw new McpException(ErrorCode.SchemaInvalid,
                    body_name + " orbits " + (body.referenceBody?.bodyName ?? "?") +
                    " but vessel's parent " + v.mainBody.bodyName +
                    " orbits " + (v.mainBody.referenceBody?.bodyName ?? "?"));
            double now = Planetarium.GetUniversalTime();
            Vector3d dv = OrbitalManeuverCalculator.DeltaVAndTimeForInterplanetaryTransferEjection(
                v.orbit, now, body.orbit, sync_phase_angle, out double burnUT);
            return PlaceProposal(v, new NodeProposal
            {
                orbit = v.orbit,
                ut = burnUT,
                worldDv = dv,
                description = "interplanetary ejection toward " + body.bodyName +
                              (sync_phase_angle ? " (synced)" : " (no phase sync)"),
                suggestedManeuverPath = "maneuver/interplanetary_transfer",
            });
        }

        [McpCommand("maneuver/match_velocities_with_target",
            Description = "Plan a burn at the next closest approach to zero relative velocity with the target. " +
                          "Used for rendezvous; refuses NO_TARGET.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static NodeDto MatchVelocitiesWithTarget(
            [McpParam(Description = "Optional: target a vessel by persistent_id (use for asteroid intercepts).")]
            uint target_persistent_id = 0)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");
            Orbit targetOrbit;
            if (target_persistent_id != 0)
            {
                Vessel tv = ResolveVessel(target_persistent_id);
                targetOrbit = tv.orbit;
                if (targetOrbit == null) throw new McpException(ErrorCode.NoTarget, "Target vessel has no orbit");
            }
            else
            {
                ITargetable t = v.targetObject;
                if (t == null) throw new McpException(ErrorCode.NoTarget, "No target set");
                targetOrbit = t.GetOrbit();
                if (targetOrbit == null) throw new McpException(ErrorCode.NoTarget, "Target has no orbit");
            }
            EnsureOrbit(v);
            if (targetOrbit.referenceBody != v.orbit.referenceBody)
                throw new McpException(ErrorCode.SchemaInvalid,
                    "Vessel orbits " + v.orbit.referenceBody?.bodyName +
                    " but target orbits " + targetOrbit.referenceBody?.bodyName +
                    " — match velocities requires the same parent body.");
            double now = Planetarium.GetUniversalTime();
            double ut = v.orbit.NextClosestApproachTime(targetOrbit, now);
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToMatchVelocities(v.orbit, ut, targetOrbit);
            return PlaceProposal(v, new NodeProposal
            {
                orbit = v.orbit,
                ut = ut,
                worldDv = dv,
                description = "match velocities at closest approach",
                suggestedManeuverPath = "maneuver/match_velocities_with_target",
            });
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
        internal static NodeProposal ProposeCircularizeAtApoapsis(Vessel v)
        {
            EnsureOrbit(v);
            double ut = v.orbit.NextApoapsisTime(Planetarium.GetUniversalTime());
            return ProposeCircularize(v.orbit, ut, "circularize at apoapsis", "maneuver/circularize_at_apoapsis");
        }

        internal static NodeProposal ProposeCircularizeAtPeriapsis(Vessel v)
        {
            EnsureOrbit(v);
            double ut = v.orbit.NextPeriapsisTime(Planetarium.GetUniversalTime());
            return ProposeCircularize(v.orbit, ut, "circularize at periapsis", "maneuver/circularize_at_periapsis");
        }

        internal static NodeProposal ProposeChangeApoapsis(Vessel v, double apoapsis_m)
        {
            EnsureOrbitAndBody(v);
            double ut = v.orbit.NextPeriapsisTime(Planetarium.GetUniversalTime());
            double bodyRadius = v.mainBody.Radius;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangeApoapsis(v.orbit, ut, bodyRadius + apoapsis_m);
            return new NodeProposal
            {
                orbit = v.orbit,
                ut = ut,
                worldDv = dv,
                description = "change apoapsis at periapsis to " + apoapsis_m + "m",
                suggestedManeuverPath = "maneuver/change_apoapsis",
            };
        }

        internal static NodeProposal ProposeChangePeriapsis(Vessel v, double periapsis_m)
        {
            EnsureOrbitAndBody(v);
            double ut = v.orbit.NextApoapsisTime(Planetarium.GetUniversalTime());
            double bodyRadius = v.mainBody.Radius;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangePeriapsis(v.orbit, ut, bodyRadius + periapsis_m);
            return new NodeProposal
            {
                orbit = v.orbit,
                ut = ut,
                worldDv = dv,
                description = "change periapsis at apoapsis to " + periapsis_m + "m",
                suggestedManeuverPath = "maneuver/change_periapsis",
            };
        }

        internal static NodeProposal ProposeChangeInclination(Vessel v, double inclination_deg)
        {
            EnsureOrbit(v);
            double now = Planetarium.GetUniversalTime();
            double tAN = v.orbit.TimeOfAscendingNodeEquatorial(now);
            double tDN = v.orbit.TimeOfDescendingNodeEquatorial(now);
            double ut = tAN < tDN ? tAN : tDN;
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangeInclination(v.orbit, ut, inclination_deg);
            return new NodeProposal
            {
                orbit = v.orbit,
                ut = ut,
                worldDv = dv,
                description = "change inclination to " + inclination_deg + "°",
                suggestedManeuverPath = "maneuver/change_inclination",
            };
        }

        internal static NodeProposal ProposeChangePeriapsis(Orbit orbit, double ut, double periapsisRadius, double periapsisAltitude)
        {
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToChangePeriapsis(orbit, ut, periapsisRadius);
            return new NodeProposal
            {
                orbit = orbit,
                ut = ut,
                worldDv = dv,
                description = "change periapsis at apoapsis to " + periapsisAltitude + "m",
                suggestedManeuverPath = "maneuver/change_periapsis",
            };
        }

        internal static Vector3d NodeComponents(NodeProposal proposal)
        {
            if (proposal?.orbit == null) return Vector3d.zero;
            return proposal.orbit.DeltaVToManeuverNodeCoordinates(proposal.ut, proposal.worldDv);
        }

        private static NodeProposal ProposeCircularize(Orbit orbit, double ut, string description, string path)
        {
            Vector3d dv = OrbitalManeuverCalculator.DeltaVToCircularize(orbit, ut);
            return new NodeProposal
            {
                orbit = orbit,
                ut = ut,
                worldDv = dv,
                description = description,
                suggestedManeuverPath = path,
            };
        }

        private static NodeDto PlaceProposal(Vessel v, NodeProposal proposal)
        {
            ManeuverNode node = v.PlaceManeuverNode(proposal.orbit, proposal.worldDv, proposal.ut);
            return DescribeNode(node, proposal);
        }

        private static NodeDto DescribeNode(ManeuverNode node, NodeProposal proposal)
        {
            return new NodeDto
            {
                ut = proposal.ut,
                dv_total_mps = proposal.worldDv.magnitude,
                dv_prograde_mps = node?.DeltaV.z ?? 0,
                dv_normal_mps = node?.DeltaV.y ?? 0,
                dv_radial_mps = node?.DeltaV.x ?? 0,
                description = proposal.description,
            };
        }

        private static void EnsureOrbit(Vessel v)
        {
            if (v?.orbit == null) throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
        }

        private static void EnsureOrbitAndBody(Vessel v)
        {
            if (v?.orbit == null || v.mainBody == null)
                throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
        }
    }
}
