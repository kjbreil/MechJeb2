using System;
using System.Collections.Generic;

namespace MuMech.Mcp
{
    [McpDescription("Read-only maneuver feasibility estimates using the same orbital math as the maneuver planner.", Version = "1.0.0")]
    public static class ManeuverEstimateCapability
    {
        private const double DeltaVReserveMultiplier = 1.10;
        private const double LowTwrThreshold = 0.20;

        [McpCommand("estimate/maneuver/circularize_at_apoapsis",
            Description = "Estimate whether the active vessel can circularize at the next apoapsis without placing a maneuver node.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto CircularizeAtApoapsis()
        {
            return EstimateSingle(
                "circularize_at_apoapsis",
                "maneuver/circularize_at_apoapsis",
                v => ManeuverPlannerCapability.ProposeCircularizeAtApoapsis(v));
        }

        [McpCommand("estimate/maneuver/circularize_at_periapsis",
            Description = "Estimate whether the active vessel can circularize at the next periapsis without placing a maneuver node.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto CircularizeAtPeriapsis()
        {
            return EstimateSingle(
                "circularize_at_periapsis",
                "maneuver/circularize_at_periapsis",
                v => ManeuverPlannerCapability.ProposeCircularizeAtPeriapsis(v));
        }

        [McpCommand("estimate/maneuver/change_apoapsis",
            Description = "Estimate whether the active vessel can raise/lower apoapsis to the target altitude (m ASL) without placing a node.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto ChangeApoapsis(
            [McpParam(Description = "New apoapsis altitude above sea level, in meters.")]
            double apoapsis_m)
        {
            Vessel v = RequireVesselWithOrbitAndBody();
            ManeuverEstimateDto invalid = ValidateTargetAltitude(v, apoapsis_m, "change_apoapsis", "maneuver/change_apoapsis");
            if (invalid != null) return invalid;

            return EstimateSingle(
                "change_apoapsis",
                "maneuver/change_apoapsis",
                active => ManeuverPlannerCapability.ProposeChangeApoapsis(active, apoapsis_m),
                BuildTargetWarnings(v, apoapsis_m, "target_apoapsis"));
        }

        [McpCommand("estimate/maneuver/change_periapsis",
            Description = "Estimate whether the active vessel can raise/lower periapsis to the target altitude (m ASL) without placing a node.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto ChangePeriapsis(
            [McpParam(Description = "New periapsis altitude above sea level, in meters.")]
            double periapsis_m)
        {
            Vessel v = RequireVesselWithOrbitAndBody();
            ManeuverEstimateDto invalid = ValidateTargetAltitude(v, periapsis_m, "change_periapsis", "maneuver/change_periapsis");
            if (invalid != null) return invalid;

            return EstimateSingle(
                "change_periapsis",
                "maneuver/change_periapsis",
                active => ManeuverPlannerCapability.ProposeChangePeriapsis(active, periapsis_m),
                BuildTargetWarnings(v, periapsis_m, "target_periapsis"));
        }

        [McpCommand("estimate/maneuver/change_inclination",
            Description = "Estimate whether the active vessel can change inclination at the next AN/DN without placing a node.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto ChangeInclination(
            [McpParam(Description = "Target inclination in degrees.")]
            double inclination_deg)
        {
            RequireVesselWithOrbit();
            if (inclination_deg < 0 || inclination_deg > 180 || !IsFinite(inclination_deg))
            {
                return BlockedEstimate(
                    "change_inclination",
                    "maneuver/change_inclination",
                    "invalid_target_inclination",
                    "input_near_invalid_bounds");
            }

            var warnings = new List<string>();
            if (inclination_deg < 0.1 || inclination_deg > 179.9)
                warnings.Add("input_near_invalid_bounds");

            return EstimateSingle(
                "change_inclination",
                "maneuver/change_inclination",
                v => ManeuverPlannerCapability.ProposeChangeInclination(v, inclination_deg),
                warnings);
        }

        [McpCommand("estimate/maneuver/change_orbit",
            Description = "Estimate a two-step orbit change: burn at periapsis to set apoapsis, then burn at apoapsis to set periapsis.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ManeuverEstimateDto ChangeOrbit(
            [McpParam(Description = "New apoapsis altitude above sea level, in meters.")]
            double apoapsis_m,
            [McpParam(Description = "New periapsis altitude above sea level, in meters.")]
            double periapsis_m)
        {
            Vessel v = RequireVesselWithOrbitAndBody();
            ManeuverEstimateDto invalidAp = ValidateTargetAltitude(v, apoapsis_m, "change_orbit", "maneuver/change_apoapsis");
            if (invalidAp != null) return invalidAp;
            ManeuverEstimateDto invalidPe = ValidateTargetAltitude(v, periapsis_m, "change_orbit", "maneuver/change_apoapsis");
            if (invalidPe != null) return invalidPe;

            var warnings = BuildTargetWarnings(v, apoapsis_m, "target_apoapsis");
            AddRange(warnings, BuildTargetWarnings(v, periapsis_m, "target_periapsis"));
            AddWarning(warnings, "multi_step_estimate");

            try
            {
                ManeuverPlannerCapability.NodeProposal step1 = ManeuverPlannerCapability.ProposeChangeApoapsis(v, apoapsis_m);
                Orbit transferOrbit = v.orbit.PerturbedOrbit(step1.ut, step1.worldDv);
                double bodyRadius = v.mainBody.Radius;
                double ut2 = transferOrbit.NextApoapsisTime(step1.ut);
                ManeuverPlannerCapability.NodeProposal step2 =
                    ManeuverPlannerCapability.ProposeChangePeriapsis(transferOrbit, ut2, bodyRadius + periapsis_m, periapsis_m);

                double estimatedDv = step1.DeltaVMagnitude + step2.DeltaVMagnitude;
                var steps = new List<ManeuverStepEstimateDto>
                {
                    BuildStep("change_apoapsis", step1, new List<string>()),
                    BuildStep("change_periapsis", step2, new List<string>())
                };

                return BuildEstimate(v, "change_orbit", "maneuver/change_apoapsis", estimatedDv, step1.ut, warnings, steps, true);
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FailedEstimate("change_orbit", "maneuver/change_apoapsis", ex);
            }
        }

        private static ManeuverEstimateDto EstimateSingle(
            string operation,
            string suggestedPath,
            Func<Vessel, ManeuverPlannerCapability.NodeProposal> proposer,
            List<string> extraWarnings = null)
        {
            Vessel v = RequireVesselWithOrbit();
            var warnings = extraWarnings ?? new List<string>();

            try
            {
                ManeuverPlannerCapability.NodeProposal proposal = proposer(v);
                if (!IsFinite(proposal.DeltaVMagnitude) || !IsFinite(proposal.ut))
                    return BlockedEstimate(operation, suggestedPath, "maneuver_estimate_invalid", "planner_returned_non_finite_values");

                return BuildEstimate(v, operation, suggestedPath, proposal.DeltaVMagnitude, proposal.ut, warnings, new List<ManeuverStepEstimateDto>(), false);
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FailedEstimate(operation, suggestedPath, ex);
            }
        }

        private static ManeuverEstimateDto BuildEstimate(
            Vessel v,
            string operation,
            string suggestedPath,
            double estimatedDv,
            double burnUt,
            List<string> warnings,
            List<ManeuverStepEstimateDto> steps,
            bool multiStep)
        {
            warnings = warnings ?? new List<string>();
            AddContextWarnings(v, warnings, multiStep);

            DeltaVAvailability available = GetAvailableDeltaV(v);
            var blocking = new List<string>();
            if (!available.valid)
            {
                blocking.Add("delta_v_unavailable");
                AddWarning(warnings, "stage_stats_unavailable");
            }
            else if (available.deltaV < estimatedDv * DeltaVReserveMultiplier)
            {
                blocking.Add("insufficient_delta_v");
            }

            if (available.lowTwr)
                AddWarning(warnings, "low_twr");

            double now = Planetarium.GetUniversalTime();
            double availableDv = available.valid ? available.deltaV : 0.0;
            return new ManeuverEstimateDto
            {
                possible = blocking.Count == 0,
                confidence = Confidence(warnings, available.valid),
                estimate_quality = "planner_dry_run",
                estimated_delta_v_mps = estimatedDv,
                available_delta_v_mps = availableDv,
                margin_mps = availableDv - estimatedDv,
                margin_ratio = estimatedDv > 0 ? availableDv / estimatedDv : 0.0,
                burn_ut = burnUt,
                burn_in_s = Math.Max(0.0, burnUt - now),
                operation = operation,
                suggested_maneuver_path = suggestedPath,
                warnings = warnings,
                blocking_reasons = blocking,
                steps = steps ?? new List<ManeuverStepEstimateDto>(),
            };
        }

        private static ManeuverStepEstimateDto BuildStep(
            string operation,
            ManeuverPlannerCapability.NodeProposal proposal,
            List<string> warnings)
        {
            Vector3d nodeDv = ManeuverPlannerCapability.NodeComponents(proposal);
            double now = Planetarium.GetUniversalTime();
            return new ManeuverStepEstimateDto
            {
                operation = operation,
                estimated_delta_v_mps = proposal.DeltaVMagnitude,
                burn_ut = proposal.ut,
                burn_in_s = Math.Max(0.0, proposal.ut - now),
                dv_prograde_mps = nodeDv.z,
                dv_normal_mps = nodeDv.y,
                dv_radial_mps = nodeDv.x,
                suggested_maneuver_path = proposal.suggestedManeuverPath,
                warnings = warnings ?? new List<string>(),
            };
        }

        private static ManeuverEstimateDto FailedEstimate(string operation, string suggestedPath, Exception ex)
        {
            var warnings = new List<string> { "maneuver_estimate_failed", ex.GetType().Name };
            return new ManeuverEstimateDto
            {
                possible = false,
                confidence = "low",
                estimate_quality = "planner_dry_run",
                estimated_delta_v_mps = 0.0,
                available_delta_v_mps = 0.0,
                margin_mps = 0.0,
                margin_ratio = 0.0,
                burn_ut = 0.0,
                burn_in_s = 0.0,
                operation = operation,
                suggested_maneuver_path = suggestedPath,
                warnings = warnings,
                blocking_reasons = new List<string> { "maneuver_estimate_failed" },
                steps = new List<ManeuverStepEstimateDto>(),
            };
        }

        private static ManeuverEstimateDto BlockedEstimate(string operation, string suggestedPath, string reason, string warning)
        {
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(warning)) warnings.Add(warning);
            return new ManeuverEstimateDto
            {
                possible = false,
                confidence = "low",
                estimate_quality = "planner_dry_run",
                estimated_delta_v_mps = 0.0,
                available_delta_v_mps = 0.0,
                margin_mps = 0.0,
                margin_ratio = 0.0,
                burn_ut = 0.0,
                burn_in_s = 0.0,
                operation = operation,
                suggested_maneuver_path = suggestedPath,
                warnings = warnings,
                blocking_reasons = new List<string> { reason },
                steps = new List<ManeuverStepEstimateDto>(),
            };
        }

        private static ManeuverEstimateDto ValidateTargetAltitude(Vessel v, double altitude, string operation, string suggestedPath)
        {
            if (!IsFinite(altitude) || v?.mainBody == null || v.mainBody.Radius + altitude <= 0)
                return BlockedEstimate(operation, suggestedPath, "invalid_target_altitude", "input_near_invalid_bounds");
            return null;
        }

        private static List<string> BuildTargetWarnings(Vessel v, double altitude, string prefix)
        {
            var warnings = new List<string>();
            if (altitude < 0)
            {
                warnings.Add(prefix + "_below_sea_level");
                warnings.Add("input_near_invalid_bounds");
            }
            if (v?.mainBody != null && v.mainBody.atmosphere && altitude < v.mainBody.atmosphereDepth)
                warnings.Add(prefix + "_inside_atmosphere");
            return warnings;
        }

        private static void AddContextWarnings(Vessel v, List<string> warnings, bool multiStep)
        {
            if (multiStep) AddWarning(warnings, "multi_step_estimate");
            if (v?.orbit != null && v.orbit.eccentricity >= 1.0)
                AddWarning(warnings, "hyperbolic_orbit");
            if (InAtmosphere(v))
                AddWarning(warnings, "in_atmosphere");
        }

        private static bool InAtmosphere(Vessel v)
        {
            if (v?.mainBody == null || !v.mainBody.atmosphere) return false;
            return v.atmDensity > 0.000001 || v.altitude < v.mainBody.atmosphereDepth;
        }

        private static string Confidence(List<string> warnings, bool deltaVAvailable)
        {
            if (!deltaVAvailable || warnings.Contains("input_near_invalid_bounds") || warnings.Contains("stage_stats_unavailable"))
                return "low";

            if (warnings.Contains("in_atmosphere")
                || warnings.Contains("hyperbolic_orbit")
                || warnings.Contains("low_twr")
                || warnings.Contains("multi_step_estimate"))
                return "medium";

            return "high";
        }

        private static DeltaVAvailability GetAvailableDeltaV(Vessel v)
        {
            var result = new DeltaVAvailability();
            MechJebCore core = v?.GetMasterMechJeb();
            if (core == null) return result;

            try
            {
                MechJebModuleStageStats stats = core.StageStats ?? core.GetComputerModule<MechJebModuleStageStats>();
                if (stats == null) return result;

                stats.RequestUpdate();
                var vac = stats.VacStats;
                if (vac == null || vac.Count == 0) return result;

                double total = 0.0;
                double maxTwr = 0.0;
                bool sawFiniteStage = false;
                double geeASL = v?.mainBody != null ? v.mainBody.GeeASL : 1.0;
                for (int i = 0; i < vac.Count; i++)
                {
                    double dv = vac[i].DeltaV;
                    if (!IsFinite(dv)) continue;
                    total += Math.Max(0.0, dv);
                    maxTwr = Math.Max(maxTwr, vac[i].StartTWR(geeASL));
                    sawFiniteStage = true;
                }

                result.valid = sawFiniteStage;
                result.deltaV = total;
                result.lowTwr = maxTwr > 0.0 && maxTwr < LowTwrThreshold;
            }
            catch
            {
                result.valid = false;
            }

            return result;
        }

        private static Vessel RequireVesselWithOrbit()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null) throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            return v;
        }

        private static Vessel RequireVesselWithOrbitAndBody()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v?.orbit == null || v.mainBody == null)
                throw new McpException(ErrorCode.NoVessel, "No active vessel orbit");
            return v;
        }

        private static void AddRange(List<string> target, List<string> source)
        {
            if (target == null || source == null) return;
            for (int i = 0; i < source.Count; i++)
                AddWarning(target, source[i]);
        }

        private static void AddWarning(List<string> warnings, string warning)
        {
            if (warnings == null || string.IsNullOrEmpty(warning) || warnings.Contains(warning)) return;
            warnings.Add(warning);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private sealed class DeltaVAvailability
        {
            public bool valid;
            public double deltaV;
            public bool lowTwr;
        }
    }
}
