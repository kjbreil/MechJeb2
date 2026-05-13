using System;

namespace MuMech.Mcp
{
    [McpDescription("KSP science introspection: totals, earned subjects, current-location science, next easy recommendations, and per-body opportunities.", Version = "1.0.0")]
    public static class ScienceCapability
    {
        [McpCommand("science/totals",
            Description = "Aggregate science earned across all R&D subjects, broken down per body. Returns mode=sandbox with zeros when no R&D facility exists.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static ScienceGuidance.TotalsDto Totals() => ScienceGuidance.Totals();

        [McpCommand("science/earned",
            Description = "List collected ScienceSubject records with parsed body, situation, biome, experiment_id, earned, cap, and remaining. Optional filters: body (exact bodyName), experiment_id. limit defaults to 100 and maxes at 500.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static ScienceGuidance.EarnedDto Earned(
            [McpParam(Description = "Restrict to one body (exact bodyName, case-sensitive). Empty = all bodies.")]
            string body = "",
            [McpParam(Description = "Restrict to one experiment id (e.g. crewReport, mysteryGoo). Empty = all experiments.")]
            string experiment_id = "",
            [McpParam(Description = "Max entries to return (default 100, max 500).")]
            int limit = 100)
        {
            return ScienceGuidance.Earned(body, experiment_id, limit);
        }

        [McpCommand("science/here",
            Description = "Current active-vessel body/situation/biome, onboard experiments, and remaining science runnable here. Read-only; does not create ScienceSubject records.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ScienceGuidance.HereDto Here()
        {
            try
            {
                return ScienceGuidance.Here(FlightGlobals.ActiveVessel);
            }
            catch (InvalidOperationException e)
            {
                throw new McpException(ErrorCode.NoVessel, e.Message);
            }
        }

        [McpCommand("science/next_easy",
            Description = "Rank easy next science for the active vessel. Prioritizes runnable-now onboard experiments, then same-body local/situation changes. Read-only.",
            SideEffect = SideEffect.ReadOnly,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static ScienceGuidance.NextEasyDto NextEasy(
            [McpParam(Description = "Max recommendations to return (default 10, max 50).")]
            int limit = 10)
        {
            try
            {
                return ScienceGuidance.NextEasy(FlightGlobals.ActiveVessel, limit);
            }
            catch (InvalidOperationException e)
            {
                throw new McpException(ErrorCode.NoVessel, e.Message);
            }
        }

        [McpCommand("science/opportunities",
            Description = "Per-body, per-situation science gaps. Scope defaults to current_soi_tree; pass all for every body or an exact bodyName. Accounting tracks earned science per experiment/body/situation/biome.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static ScienceGuidance.OpportunitiesDto Opportunities(
            [McpParam(Description = "current_soi_tree (default), all, or an exact bodyName.")]
            string scope = "current_soi_tree")
        {
            return ScienceGuidance.Opportunities(scope);
        }
    }
}
