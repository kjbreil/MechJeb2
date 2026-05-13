using System.Linq;

namespace MuMech.Mcp
{
    [McpDescription("Maneuver node executor. Aligns, warps, and burns through the next or all nodes.", Version = "1.0.0")]
    public static class NodeExecutorCapability
    {
        [McpCommand("autopilot/node_executor/execute_next",
            Description = "Execute the next maneuver node (warps to lead time, aligns, burns). Returns a handle.",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp ExecuteNext()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            if (FlightGlobals.ActiveVessel?.patchedConicSolver?.maneuverNodes == null
                || FlightGlobals.ActiveVessel.patchedConicSolver.maneuverNodes.Count == 0)
                throw new McpException(ErrorCode.NoManeuverNode, "No maneuver node to execute. Plan one first.");

            MechJebModuleNodeExecutor exec = core.Node;
            if (exec == null) throw new McpException(ErrorCode.Internal, "Core.Node is null");
            // Track engagement first so GameEvent aborts can disengage us cleanly,
            // then call ExecuteOneNode which runs Init() — sets state to WARPALIGN
            // and locks the Attitude/Thrust controllers. Users.Add inside
            // ExecuteOneNode is idempotent (HashSet).
            MechJebMcpUser.Instance.Engage(exec);
            exec.ExecuteOneNode(MechJebMcpUser.Instance);
            return new AutopilotOp(exec, () => new JsonObject().Set("status", "complete"));
        }

        [McpCommand("autopilot/node_executor/execute_all",
            Description = "Execute every maneuver node in sequence. Returns a single handle that " +
                          "completes when the queue empties.",
            SideEffect = SideEffect.MutatingLongRunning,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static IRunningOp ExecuteAll()
        {
            MechJebCore core = AscentCapability.ResolveMaster();
            if (FlightGlobals.ActiveVessel?.patchedConicSolver?.maneuverNodes == null
                || FlightGlobals.ActiveVessel.patchedConicSolver.maneuverNodes.Count == 0)
                throw new McpException(ErrorCode.NoManeuverNode, "No maneuver nodes to execute. Plan one first.");
            MechJebModuleNodeExecutor exec = core.Node;
            if (exec == null) throw new McpException(ErrorCode.Internal, "Core.Node is null");
            MechJebMcpUser.Instance.Engage(exec);
            exec.ExecuteAllNodes(MechJebMcpUser.Instance);
            return new AutopilotOp(exec);
        }

        [McpCommand("autopilot/node_executor/abort",
            Description = "Abort the running node execution. MCP-only — does not disengage human users.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT",
            Version = "1.0.0")]
        public static JsonObject Abort()
        {
            MechJebCore core = AscentCapability.TryMaster();
            if (core?.Node == null) return new JsonObject().Set("aborted", false);
            MechJebMcpUser.Instance.Disengage(core.Node);
            return new JsonObject().Set("aborted", true);
        }

        // -- live state -----------------------------------------------------
        [McpProperty("autopilot/node_executor/state",
            Description = "Current state of the node executor (WARPALIGN/LEAD/BURN/IDLE).",
            Access = Access.Read, Version = "1.0.0")]
        public static string State
        {
            get
            {
                MechJebCore core = AscentCapability.TryMaster();
                return core?.Node?.State.ToString() ?? "IDLE";
            }
        }

        [McpProperty("autopilot/node_executor/autowarp",
            Description = "Time-warp to the burn lead-time automatically.",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool Autowarp
        {
            get => AscentCapability.TryMaster()?.Node?.Autowarp ?? true;
            set { AscentCapability.ResolveMaster().Node.Autowarp = value; }
        }

        [McpProperty("autopilot/node_executor/lead_time_s",
            Description = "Seconds to lead the maneuver node (start the burn early to center it on the node).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static double LeadTime
        {
            get => AscentCapability.TryMaster()?.Node?.LeadTime.Val ?? double.NaN;
            set { AscentCapability.ResolveMaster().Node.LeadTime.Val = value; }
        }

        [McpProperty("autopilot/node_executor/rcs_only",
            Description = "Burn using RCS only (no main engine).",
            Access = Access.ReadWrite, Version = "1.0.0")]
        public static bool RcsOnly
        {
            get => AscentCapability.TryMaster()?.Node?.RCSOnly ?? false;
            set { AscentCapability.ResolveMaster().Node.RCSOnly = value; }
        }
    }
}
