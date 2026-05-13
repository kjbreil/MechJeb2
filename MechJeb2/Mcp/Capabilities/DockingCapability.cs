namespace MuMech.Mcp
{
    // Docking autopilot — final-approach automation for rendezvous + docking.
    // Works with stock docking ports AND the Klaw / Advanced Grabbing Unit
    // (since the Klaw acts as the control reference once "Control From Here").
    // Requires:
    //   - A target set via target/set_vessel (or already active target)
    //   - RCS thrusters on the vessel (the docking AP uses them for translation)
    //   - "Control From Here" on the docking port / Klaw before engaging
    // Pair with maneuver/match_velocities_with_target before engaging — the
    // docking AP is for the last ~10 km, not for matching orbits.
    [McpDescription("Docking autopilot — final-approach automation. Pairs with target/set_vessel + Klaw for asteroid capture.", Version = "1.0.0")]
    public static class DockingCapability
    {
        [McpCommand("autopilot/docking/engage",
            Description = "Engage the docking autopilot. Returns a handle that completes when " +
                          "docking succeeds (onPartCouple fires) or the autopilot otherwise " +
                          "disengages. Requires a target and RCS; the active control reference " +
                          "becomes the 'docking port' (right-click your Klaw and 'Control From Here' " +
                          "before engaging for asteroid grabs).",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp Engage()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleDockingAutopilot dap = core.GetComputerModule<MechJebModuleDockingAutopilot>();
            if (dap == null) throw new McpException(ErrorCode.Internal, "DockingAutopilot module not present");
            if (FlightGlobals.ActiveVessel?.targetObject == null)
                throw new McpException(ErrorCode.NoTarget, "Docking autopilot needs a target");
            MechJebMcpUser.Instance.Engage(dap);
            return new AutopilotOp(dap, () => new JsonObject()
                .Set("status", dap.status ?? "")
                .Set("step", dap.dockingStep.ToString()));
        }

        [McpCommand("autopilot/docking/abort",
            Description = "Disengage the docking autopilot (removes MCP from its Users; human-engaged users keep theirs).",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject Abort()
        {
            MechJebCore core = AscentCapability.TryMaster();
            MechJebModuleDockingAutopilot dap = core?.GetComputerModule<MechJebModuleDockingAutopilot>();
            if (dap == null) return new JsonObject().Set("aborted", false);
            MechJebMcpUser.Instance.Disengage(dap);
            return new JsonObject().Set("aborted", true);
        }

        // -- properties --
        [McpProperty("autopilot/docking/status",
            Description = "Live status string from the docking autopilot (current sub-phase, localized).",
            Access = Access.Read, Version = "1.0.0")]
        public static string Status
        {
            get
            {
                MechJebCore core = AscentCapability.TryMaster();
                return core?.GetComputerModule<MechJebModuleDockingAutopilot>()?.status ?? "";
            }
        }

        [McpProperty("autopilot/docking/step",
            Description = "Current step in the docking sequence (INIT / WRONG_SIDE_* / BACKING_UP / MOVING_TO_START / DOCKING / OFF).",
            Access = Access.Read, Version = "1.0.0")]
        public static string Step
        {
            get
            {
                MechJebCore core = AscentCapability.TryMaster();
                MechJebModuleDockingAutopilot dap = core?.GetComputerModule<MechJebModuleDockingAutopilot>();
                return dap == null ? "OFF" : dap.dockingStep.ToString();
            }
        }

        [McpProperty("autopilot/docking/speed_limit_mps",
            Description = "Maximum approach speed (m/s). 0 = unlimited (not recommended). " +
                          "Default 1 m/s is good for stock docking ports; consider 0.5 m/s for asteroid Klaw grabs.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double SpeedLimit
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.speedLimit.Val ?? double.NaN;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().speedLimit.Val = value; }
        }

        [McpProperty("autopilot/docking/force_roll",
            Description = "Hold a specific roll angle during approach (relative to target's port). " +
                          "Useful for stock docking; usually unnecessary for asteroid grabs.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool ForceRoll
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.forceRol ?? false;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().forceRol = value; }
        }

        [McpProperty("autopilot/docking/roll_deg",
            Description = "Roll angle to hold when force_roll is true (degrees).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double RollDeg
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.rol.Val ?? double.NaN;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().rol.Val = value; }
        }

        [McpProperty("autopilot/docking/override_safe_distance",
            Description = "If true, use override_safe_distance_m instead of MechJeb's bounding-box estimate. " +
                          "Set true + a value of ~3-5m when grabbing irregular asteroids that confuse the auto-sizing.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool OverrideSafeDistance
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.overrideSafeDistance ?? false;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().overrideSafeDistance = value; }
        }

        [McpProperty("autopilot/docking/override_safe_distance_m",
            Description = "Override value for the 'safe distance' (m). Active only when override_safe_distance is true.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double OverrideSafeDistanceM
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.overridenSafeDistance.Val ?? double.NaN;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().overridenSafeDistance.Val = value; }
        }

        [McpProperty("autopilot/docking/override_target_size",
            Description = "If true, use override_target_size_m instead of auto-estimated target size.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool OverrideTargetSize
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.overrideTargetSize ?? false;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().overrideTargetSize = value; }
        }

        [McpProperty("autopilot/docking/override_target_size_m",
            Description = "Override value for the target size (m). Active only when override_target_size is true.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double OverrideTargetSizeM
        {
            get => AscentCapability.TryMaster()?.GetComputerModule<MechJebModuleDockingAutopilot>()?.overridenTargetSize.Val ?? double.NaN;
            set { AscentCapability.ResolveMaster().GetComputerModule<MechJebModuleDockingAutopilot>().overridenTargetSize.Val = value; }
        }
    }
}
