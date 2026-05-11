namespace MuMech.Mcp
{
    [McpDescription("Live observables — cheap polled reads the LLM needs to decide \"are we aligned yet?\" / \"is the burn done?\"", Version = "1.0.0")]
    public static class LiveStateCapability
    {
        [McpProperty("vessel/attitude_error_deg",
            Description = "Angle (degrees) between current vessel attitude and the autopilot's target attitude. " +
                          "Small (<1°) = aligned and ready to burn.",
            Access = Access.Read, Version = "1.0.0")]
        public static double AttitudeErrorDeg
        {
            get
            {
                MechJebCore core = AscentCapability.TryMaster();
                return core?.Attitude?.attitudeError ?? double.NaN;
            }
        }

        [McpProperty("vessel/throttle",
            Description = "Current throttle [0..1]. 0 = no thrust.",
            Access = Access.Read, Version = "1.0.0")]
        public static double Throttle
        {
            get
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v?.ctrlState?.mainThrottle ?? 0.0;
            }
        }

        [McpProperty("vessel/time_warp_rate",
            Description = "Current time-warp rate (1.0 = real time).",
            Access = Access.Read, Version = "1.0.0")]
        public static double TimeWarpRate
        {
            get
            {
                try { return TimeWarp.CurrentRate; } catch { return 1.0; }
            }
        }

        [McpProperty("vessel/time_warp_mode",
            Description = "Time warp mode (LOW=physical, HIGH=on-rails).",
            Access = Access.Read, Version = "1.0.0")]
        public static string TimeWarpMode
        {
            get
            {
                try { return TimeWarp.WarpMode.ToString(); } catch { return "LOW"; }
            }
        }

        [McpProperty("vessel/sas_engaged",
            Description = "True if KSP's built-in SAS is on.",
            Access = Access.Read, Version = "1.0.0")]
        public static bool SasEngaged
        {
            get
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v != null && v.ActionGroups[KSPActionGroup.SAS];
            }
        }
    }
}
