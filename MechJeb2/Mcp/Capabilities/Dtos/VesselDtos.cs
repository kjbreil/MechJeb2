using System.Collections.Generic;

namespace MuMech.Mcp
{
    // DTOs for vessel/summary. Plain primitive fields only — no Unity/KSP
    // refs, no circular references, no Vessel/Part/Orbit objects. The JSON
    // schema generator rejects unwrapped Unity types at registry build, so
    // these are the only way to expose vessel data to the LLM.
    public sealed class VesselSummaryDto
    {
        public string name;
        public uint persistent_id;
        public string situation;            // ORBITING / FLYING / LANDED / ...
        public string vessel_type;          // PROBE / SHIP / STATION / ...
        public string body;                 // current main body
        public bool is_active;
        public double mass_total_t;         // metric tons
        public double mass_dry_t;
        public double twr_current;          // current TWR vs current body's gravity at altitude
        public double atmospheric_density;  // kg/m³ at current altitude
        public double altitude_asl_m;
        public double altitude_terrain_m;
        public double speed_orbit_mps;
        public double speed_surface_mps;
        public double speed_vertical_mps;
        public bool sas;
        public bool rcs;
        public double throttle;
        public bool gear;
        public bool brakes;
        public bool lights;
        public OrbitDto orbit;
        public List<ResourceDto> resources;
        public List<StageDto> stages;       // omitted if MechJeb StageStats unavailable
        public List<string> engaged_mechjeb_modules; // module names with MCP+human users.
        public int parts_total;
        public List<PartDto> parts;         // only when include_parts:true; auto-truncated.
        public bool parts_truncated;
    }

    public sealed class OrbitDto
    {
        public double semi_major_axis_m;
        public double eccentricity;
        public double inclination_deg;
        public double argument_of_periapsis_deg;
        public double longitude_of_ascending_node_deg;
        public double true_anomaly_deg;
        public double apoapsis_m;            // ASL altitude above main body radius
        public double periapsis_m;           // ASL altitude
        public double period_s;
        public double time_to_apoapsis_s;
        public double time_to_periapsis_s;
        public string body;
    }

    public sealed class ResourceDto
    {
        public string name;       // e.g. LiquidFuel, Oxidizer, ElectricCharge
        public double current;
        public double max;
        public double current_units;
        public double max_units;
    }

    public sealed class StageDto
    {
        public int stage_index;
        public int parts_count;
        public double mass_start_t;
        public double mass_end_t;
        public double dv_vac_mps;
        public double dv_asl_mps;
        public double burn_time_s;
        public double thrust_vac_kn;
        public double isp_vac_s;
        public double twr_vac;
        public double twr_asl;
    }

    public sealed class PartDto
    {
        public uint flight_id;
        public string name;
        public string title;
        public int stage;
        public double mass_t;
        public bool engine;
        public bool decoupler;
        public bool fairing;
        public bool parachute;
    }
}
