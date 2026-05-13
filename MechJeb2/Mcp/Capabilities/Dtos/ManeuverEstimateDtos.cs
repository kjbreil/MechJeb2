using System.Collections.Generic;

namespace MuMech.Mcp
{
    public sealed class ManeuverEstimateDto
    {
        public bool possible;
        public string confidence;
        public string estimate_quality;
        public double estimated_delta_v_mps;
        public double available_delta_v_mps;
        public double margin_mps;
        public double margin_ratio;
        public double burn_ut;
        public double burn_in_s;
        public string operation;
        public string suggested_maneuver_path;
        public List<string> warnings;
        public List<string> blocking_reasons;
        public List<ManeuverStepEstimateDto> steps;
    }

    public sealed class ManeuverStepEstimateDto
    {
        public string operation;
        public double estimated_delta_v_mps;
        public double burn_ut;
        public double burn_in_s;
        public double dv_prograde_mps;
        public double dv_normal_mps;
        public double dv_radial_mps;
        public string suggested_maneuver_path;
        public List<string> warnings;
    }
}
