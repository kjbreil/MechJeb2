using System.Collections.Generic;

namespace MuMech.Mcp
{
    [McpDescription("Live active vessel introspection. Stage-collapsed by default; full parts list optional.", Version = "1.0.0")]
    public static class VesselCapability
    {
        [McpCommand("vessel/summary",
            Description = "Active vessel snapshot. Orbit, mass, TWR, control state, resources, stages, " +
                          "engaged MechJeb modules. Pass include_parts:true for the full part list " +
                          "(capped at 500 parts; parts_truncated flag set if exceeded).",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static VesselSummaryDto Summary(
            [McpParam(Description = "Include the full part list. Default false to keep payload tight.")]
            bool include_parts = false)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new McpException(ErrorCode.NoVessel, "No active vessel");

            double resourceMass = 0;
            if (v.parts != null)
            {
                foreach (Part p in v.parts)
                {
                    if (p == null) continue;
                    resourceMass += p.GetResourceMass();
                }
            }
            var dto = new VesselSummaryDto
            {
                name = v.GetDisplayName() ?? v.vesselName ?? "?",
                persistent_id = v.persistentId,
                situation = v.situation.ToString(),
                vessel_type = v.vesselType.ToString(),
                body = v.mainBody?.bodyName ?? "?",
                is_active = true,
                mass_total_t = v.totalMass,
                mass_dry_t = v.totalMass - resourceMass,
                atmospheric_density = v.atmDensity,
                altitude_asl_m = v.altitude,
                altitude_terrain_m = v.radarAltitude,
                speed_orbit_mps = v.obt_velocity.magnitude,
                speed_surface_mps = v.srf_velocity.magnitude,
                speed_vertical_mps = v.verticalSpeed,
                sas = v.ActionGroups[KSPActionGroup.SAS],
                rcs = v.ActionGroups[KSPActionGroup.RCS],
                gear = v.ActionGroups[KSPActionGroup.Gear],
                brakes = v.ActionGroups[KSPActionGroup.Brakes],
                lights = v.ActionGroups[KSPActionGroup.Light],
                throttle = v.ctrlState?.mainThrottle ?? 0.0,
                orbit = BuildOrbit(v),
                resources = BuildResources(v),
                parts_total = v.parts?.Count ?? 0,
            };

            MechJebCore master = v.GetMasterMechJeb();
            if (master != null)
            {
                // TWR from VesselState (already computed on the main thread).
                try { dto.twr_current = master.VesselState.thrustCurrent / (master.VesselState.mass * 9.81); }
                catch { dto.twr_current = 0.0; }

                dto.engaged_mechjeb_modules = ListEngagedModules(master);
                dto.stages = BuildStages(master);
            }
            else
            {
                dto.engaged_mechjeb_modules = new List<string>();
            }

            if (include_parts)
            {
                dto.parts = BuildParts(v, max: 500, out bool truncated);
                dto.parts_truncated = truncated;
            }

            return dto;
        }

        private static OrbitDto BuildOrbit(Vessel v)
        {
            if (v?.orbit == null || v.mainBody == null) return null;
            Orbit o = v.orbit;
            double bodyRadius = v.mainBody.Radius;
            return new OrbitDto
            {
                semi_major_axis_m = o.semiMajorAxis,
                eccentricity = o.eccentricity,
                inclination_deg = o.inclination,
                argument_of_periapsis_deg = o.argumentOfPeriapsis,
                longitude_of_ascending_node_deg = o.LAN,
                true_anomaly_deg = o.trueAnomaly * (180.0 / System.Math.PI),
                apoapsis_m = o.ApA,
                periapsis_m = o.PeA,
                period_s = o.period,
                time_to_apoapsis_s = o.timeToAp,
                time_to_periapsis_s = o.timeToPe,
                body = v.mainBody.bodyName ?? "?",
            };
        }

        private static List<ResourceDto> BuildResources(Vessel v)
        {
            var result = new List<ResourceDto>();
            if (v?.parts == null) return result;
            var totals = new Dictionary<string, (double cur, double max)>();
            foreach (Part p in v.parts)
            {
                if (p?.Resources == null) continue;
                foreach (PartResource r in p.Resources)
                {
                    if (r?.resourceName == null) continue;
                    var (cur, mx) = totals.TryGetValue(r.resourceName, out var existing) ? existing : (0.0, 0.0);
                    cur += r.amount;
                    mx += r.maxAmount;
                    totals[r.resourceName] = (cur, mx);
                }
            }
            foreach (KeyValuePair<string, (double cur, double max)> kv in totals)
            {
                PartResourceDefinition def = PartResourceLibrary.Instance?.GetDefinition(kv.Key);
                double density = def?.density ?? 0.0;
                result.Add(new ResourceDto
                {
                    name = kv.Key,
                    current_units = kv.Value.cur,
                    max_units = kv.Value.max,
                    current = kv.Value.cur * density,
                    max = kv.Value.max * density,
                });
            }
            return result;
        }

        private static List<StageDto> BuildStages(MechJebCore core)
        {
            var result = new List<StageDto>();
            if (core?.StageStats == null) return result;
            try
            {
                core.StageStats.RequestUpdate();
                var vac = core.StageStats.VacStats;
                var atm = core.StageStats.AtmoStats;
                if (vac == null) return result;
                // Surface gravity for TWR (m/s² per geeASL). Use main body gravity at sea level.
                double geeASL = core.vessel?.mainBody != null ? core.vessel.mainBody.GeeASL : 1.0;
                for (int i = 0; i < vac.Count; i++)
                {
                    var s = vac[i];
                    bool hasAtm = atm != null && i < atm.Count;
                    var a = hasAtm ? atm[i] : default;
                    result.Add(new StageDto
                    {
                        stage_index = i,
                        parts_count = 0,             // FuelStats doesn't carry parts count
                        mass_start_t = s.StartMass,
                        mass_end_t = s.EndMass,
                        dv_vac_mps = s.DeltaV,
                        dv_asl_mps = hasAtm ? a.DeltaV : 0.0,
                        burn_time_s = s.DeltaTime,
                        thrust_vac_kn = s.Thrust,
                        isp_vac_s = s.Isp,
                        twr_vac = s.StartTWR(geeASL),
                        twr_asl = hasAtm ? a.StartTWR(geeASL) : 0.0,
                    });
                }
            }
            catch { /* StageStats unavailable; return whatever we have */ }
            return result;
        }

        private static List<PartDto> BuildParts(Vessel v, int max, out bool truncated)
        {
            truncated = false;
            var result = new List<PartDto>(System.Math.Min(max, v.parts?.Count ?? 0));
            if (v?.parts == null) return result;
            int n = 0;
            foreach (Part p in v.parts)
            {
                if (n >= max) { truncated = true; break; }
                if (p == null) continue;
                result.Add(new PartDto
                {
                    flight_id = p.flightID,
                    name = p.partInfo?.name ?? p.name ?? "?",
                    title = p.partInfo?.title ?? "?",
                    stage = p.inverseStage,
                    mass_t = p.mass + p.GetResourceMass(),
                    engine = p.HasModuleImplementing<ModuleEngines>(),
                    decoupler = p.HasModuleImplementing<ModuleDecouple>() || p.HasModuleImplementing<ModuleAnchoredDecoupler>(),
                    fairing = p.HasModuleImplementing<ModuleProceduralFairing>(),
                    parachute = p.HasModuleImplementing<ModuleParachute>(),
                });
                n++;
            }
            return result;
        }

        private static List<string> ListEngagedModules(MechJebCore core)
        {
            var result = new List<string>();
            if (core == null) return result;
            foreach (ComputerModule m in core.GetComputerModules<ComputerModule>())
            {
                if (m == null) continue;
                if (m.Enabled || (m.Users != null && m.Users.Count > 0))
                    result.Add(m.GetType().Name);
            }
            return result;
        }
    }
}
