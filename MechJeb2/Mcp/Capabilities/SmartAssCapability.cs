namespace MuMech.Mcp
{
    [McpDescription("SmartASS attitude hold — point the vessel at prograde/retrograde/target/etc.", Version = "1.0.0")]
    public static class SmartAssCapability
    {
        public sealed class SmartAssResultDto
        {
            public string mode;
            public string target;
            public bool engaged;
        }

        [McpCommand("attitude/smartass/engage",
            Description = "Engage SmartASS with the given mode + target. " +
                          "mode=ORBITAL most common (PROGRADE/RETROGRADE/NORMAL_PLUS/NORMAL_MINUS/" +
                          "RADIAL_PLUS/RADIAL_MINUS). mode=TARGET requires target/* to be set " +
                          "(returns NO_TARGET otherwise). Completes synchronously.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static SmartAssResultDto Engage(MechJebModuleSmartASS.Mode mode, MechJebModuleSmartASS.Target target)
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleSmartASS sas = core.SmartASS;
            if (sas == null) throw new McpException(ErrorCode.Internal, "SmartASS module not present");
            if (sas.Hidden) throw new McpException(ErrorCode.ModuleHidden, "SmartASS is hidden on this vessel");

            // Mode=TARGET requires a target.
            if (RequiresTarget(target) && FlightGlobals.ActiveVessel?.targetObject == null)
                throw new McpException(ErrorCode.NoTarget, "Target-relative mode requires a target. Call target/set_vessel first.");

            sas.mode = mode;
            sas.target = target;
            sas.Engage();

            return new SmartAssResultDto
            {
                mode = mode.ToString(),
                target = target.ToString(),
                engaged = true,
            };
        }

        [McpCommand("attitude/smartass/disengage",
            Description = "Disengage SmartASS (sets target to OFF).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static SmartAssResultDto Disengage()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleSmartASS sas = core.SmartASS;
            if (sas == null) return new SmartAssResultDto { mode = "OFF", target = "OFF", engaged = false };
            sas.target = MechJebModuleSmartASS.Target.OFF;
            sas.Engage();
            return new SmartAssResultDto { mode = sas.mode.ToString(), target = "OFF", engaged = false };
        }

        [McpProperty("attitude/smartass/current_mode",
            Description = "Current SmartASS Mode.",
            Access = Access.Read, Version = "1.0.0")]
        public static string CurrentMode => AscentCapability.TryMaster()?.SmartASS?.mode.ToString() ?? "OFF";

        [McpProperty("attitude/smartass/current_target",
            Description = "Current SmartASS Target.",
            Access = Access.Read, Version = "1.0.0")]
        public static string CurrentTarget => AscentCapability.TryMaster()?.SmartASS?.target.ToString() ?? "OFF";

        private static bool RequiresTarget(MechJebModuleSmartASS.Target t)
        {
            // The TARGET_*, RELATIVE_*, and PARALLEL_* values all require a target.
            string n = t.ToString();
            return n.StartsWith("TARGET", System.StringComparison.Ordinal)
                || n.StartsWith("RELATIVE", System.StringComparison.Ordinal)
                || n.StartsWith("PARALLEL", System.StringComparison.Ordinal);
        }
    }
}
