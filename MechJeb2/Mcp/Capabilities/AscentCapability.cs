namespace MuMech.Mcp
{
    // Ascent autopilot: engage / abort / status + a curated subset of the
    // 60+ AscentSettings fields. v1 exposes the high-leverage knobs the
    // copilot would actually adjust ("target apoapsis 100km, classic
    // ascent type, turn end at 60km"). Full auto-exposure of every
    // [Persistent] field is deferred to v1.1 to avoid blasting the
    // LLM's context with low-value path noise.
    [McpDescription("Ascent autopilot (Classic and PSG). Engages MechJeb's guidance to reach a target orbit.", Version = "1.0.0")]
    public static class AscentCapability
    {
        // -- engage / abort / status ----------------------------------------
        public sealed class EngageResultDto
        {
            public string engaged_type;
            public string status;
            public double t_minus_s;
        }

        [McpCommand("autopilot/ascent/engage",
            Description = "Engage the ascent autopilot. Optionally switch type (CLASSIC/PSG) before engaging. " +
                          "Returns a handle; poll mj_ops_status.",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp Engage(
            [McpParam(Description = "Override the AscentType (CLASSIC or PSG); omit to use the current setting.",
                EnumValues = new[] { "CLASSIC", "PSG" })]
            AscentType? type = null)
        {
            MechJebCore core = ResolveMaster();
            if (type.HasValue) core.AscentSettings.AscentType = type.Value;
            MechJebModuleAscentBaseAutopilot ap = core.AscentSettings.AscentAutopilot;
            if (ap == null) throw new McpException(ErrorCode.Internal, "AscentAutopilot is null");
            MechJebMcpUser.Instance.Engage(ap);
            return new AutopilotOp(ap, () => new JsonObject()
                .Set("status", ap.Status ?? "")
                .Set("t_minus_s", ap.TMinus));
        }

        [McpCommand("autopilot/ascent/abort",
            Description = "Disengage the ascent autopilot (removes MCP from its Users; human-engaged users keep theirs).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject Abort()
        {
            MechJebCore core = ResolveMaster();
            MechJebModuleAscentBaseAutopilot ap = core.AscentSettings.AscentAutopilot;
            if (ap == null) return new JsonObject().Set("aborted", false);
            MechJebMcpUser.Instance.Disengage(ap);
            return new JsonObject().Set("aborted", true);
        }

        // -- properties: high-leverage settings -----------------------------
        // Exposed as static getters/setters that look up the active core's
        // AscentSettings at access time. Reads outside Flight return NaN
        // / default; writes throw NO_VESSEL.

        [McpProperty("autopilot/ascent/status",
            Description = "Live status string from the active ascent autopilot (localized).",
            Access = Access.Read, Version = "1.0.0")]
        public static string Status
        {
            get
            {
                MechJebCore core = TryMaster();
                return core?.AscentSettings?.AscentAutopilot?.Status ?? "";
            }
        }

        [McpProperty("autopilot/ascent/t_minus_s",
            Description = "Seconds until liftoff during launch countdown. Negative while burning.",
            Access = Access.Read, Version = "1.0.0")]
        public static double TMinus
        {
            get
            {
                MechJebCore core = TryMaster();
                return core?.AscentSettings?.AscentAutopilot?.TMinus ?? double.NaN;
            }
        }

        [McpProperty("autopilot/ascent/type",
            Description = "CLASSIC or PSG.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static AscentType Type
        {
            get => TryMaster()?.AscentSettings?.AscentType ?? AscentType.CLASSIC;
            set { ResolveMaster().AscentSettings.AscentType = value; }
        }

        [McpProperty("autopilot/ascent/desired_orbit_altitude_m",
            Description = "Target orbit altitude (meters ASL).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double DesiredOrbitAltitude
        {
            get => TryMaster()?.AscentSettings?.DesiredOrbitAltitude.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.DesiredOrbitAltitude.Val = value; }
        }

        [McpProperty("autopilot/ascent/desired_inclination_deg",
            Description = "Target inclination (degrees). Set during launch to match a target orbit plane.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double DesiredInclination
        {
            get => TryMaster()?.AscentSettings?.DesiredInclination.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.DesiredInclination.Val = value; }
        }

        [McpProperty("autopilot/ascent/desired_apoapsis_m",
            Description = "PSG-mode target apoapsis altitude (meters ASL).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double DesiredApoapsis
        {
            get => TryMaster()?.AscentSettings?.DesiredApoapsis.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.DesiredApoapsis.Val = value; }
        }

        [McpProperty("autopilot/ascent/turn_start_altitude_m",
            Description = "Classic-mode gravity-turn start altitude (meters).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double TurnStartAltitude
        {
            get => TryMaster()?.AscentSettings?.TurnStartAltitude.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.TurnStartAltitude.Val = value; }
        }

        [McpProperty("autopilot/ascent/turn_end_altitude_m",
            Description = "Classic-mode gravity-turn end altitude (meters).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double TurnEndAltitude
        {
            get => TryMaster()?.AscentSettings?.TurnEndAltitude.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.TurnEndAltitude.Val = value; }
        }

        [McpProperty("autopilot/ascent/turn_end_angle_deg",
            Description = "Classic-mode pitch at gravity-turn end (degrees).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double TurnEndAngle
        {
            get => TryMaster()?.AscentSettings?.TurnEndAngle.Val ?? double.NaN;
            set { ResolveMaster().AscentSettings.TurnEndAngle.Val = value; }
        }

        [McpProperty("autopilot/ascent/autostage",
            Description = "Auto-stage during ascent.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool Autostage
        {
            get => TryMaster()?.AscentSettings?.Autostage ?? false;
            set { ResolveMaster().AscentSettings.Autostage = value; }
        }

        [McpProperty("autopilot/ascent/skip_circularization",
            Description = "Skip the circularization burn at apoapsis.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool SkipCircularization
        {
            get => TryMaster()?.AscentSettings?.SkipCircularization ?? false;
            set { ResolveMaster().AscentSettings.SkipCircularization = value; }
        }

        [McpProperty("autopilot/ascent/corrective_steering",
            Description = "Corrective steering during gravity turn.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool CorrectiveSteering
        {
            get => TryMaster()?.AscentSettings?.CorrectiveSteering ?? false;
            set { ResolveMaster().AscentSettings.CorrectiveSteering = value; }
        }

        [McpProperty("autopilot/ascent/force_roll",
            Description = "Force a specific roll during ascent (use roll_altitude as the start altitude).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool ForceRoll
        {
            get => TryMaster()?.AscentSettings?.ForceRoll ?? false;
            set { ResolveMaster().AscentSettings.ForceRoll = value; }
        }

        // -- helpers --------------------------------------------------------
        internal static MechJebCore ResolveMaster()
        {
            MechJebCore core = TryMaster();
            if (core == null) throw new McpException(ErrorCode.NoVessel, "No master MechJeb on the active vessel");
            return core;
        }
        internal static MechJebCore TryMaster() => FlightGlobals.ActiveVessel?.GetMasterMechJeb();
    }
}
