namespace MuMech.Mcp
{
    // Wraps the operator-facing [KSPAction] hooks on MechJebCore (panic,
    // emergency landing) so the LLM can call them as named verbs.
    // Implementations delegate to the same MechJeb modules the in-game
    // action groups use; no new logic.
    [McpDescription("Emergency verbs: PANIC, LandSomewhere, LandAtKSC. Wraps MechJeb's existing [KSPAction] hooks.", Version = "1.0.0")]
    public static class EmergencyCapability
    {
        [McpCommand("emergency/panic",
            Description = "PANIC — invokes MechJebModuleTranslatron.PanicSwitch on the master MechJeb. " +
                          "Maximum throttle, attempts to escape current situation.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject Panic()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleTranslatron tr = core.GetComputerModule<MechJebModuleTranslatron>();
            if (tr == null) throw new McpException(ErrorCode.Internal, "Translatron module not present");
            if (tr.Hidden) throw new McpException(ErrorCode.ModuleHidden, "Translatron is hidden");
            tr.PanicSwitch();
            return new JsonObject().Set("panicked", true);
        }

        [McpCommand("emergency/land_somewhere",
            Description = "Engage MechJeb's landing autopilot at the current position. Returns a handle.",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp LandSomewhere()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleLandingGuidance lg = core.GetComputerModule<MechJebModuleLandingGuidance>();
            if (lg == null) throw new McpException(ErrorCode.Internal, "LandingGuidance module not present");
            lg.LandSomewhere();
            // Track engagement on the underlying landing autopilot if present.
            if (core.Landing != null) MechJebMcpUser.Instance.Engage(core.Landing);
            return new AutopilotOp(core.Landing ?? (ComputerModule)lg);
        }

        [McpCommand("emergency/land_at_ksc",
            Description = "Engage MechJeb's landing autopilot targeting KSC. Returns a handle.",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp LandAtKSC()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            MechJebModuleLandingGuidance lg = core.GetComputerModule<MechJebModuleLandingGuidance>();
            if (lg == null) throw new McpException(ErrorCode.Internal, "LandingGuidance module not present");
            lg.SetAndLandTargetKSC();
            if (core.Landing != null) MechJebMcpUser.Instance.Engage(core.Landing);
            return new AutopilotOp(core.Landing ?? (ComputerModule)lg);
        }
    }
}
