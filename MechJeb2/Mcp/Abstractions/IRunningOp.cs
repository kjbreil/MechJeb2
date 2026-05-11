namespace MuMech.Mcp
{
    // Long-running operation contract. Returned by [McpCommand(returnsHandle=true)]
    // methods so the registry can register a handle and the client can poll
    // /cancel it later.
    //
    // Implementations live with the capability that produces them — e.g.
    // AscentOp in AscentCapability, NodeExecOp in NodeExecutorCapability —
    // and call into MechJebMcpUser to track which modules are engaged on
    // behalf of MCP.
    public interface IRunningOp
    {
        // True once the op has reached a terminal state (succeeded, failed,
        // cancelled, or aborted by scene/vessel change).
        bool IsDone { get; }

        // Optional terminal-state result payload. Inspected once IsDone == true.
        // Null until done. Polled by OpRegistry on each FixedUpdate tick.
        JsonValue Result { get; }

        // Optional terminal-state error message. Non-null only on failure.
        string ErrorMessage { get; }

        // Optional terminal-state error code (wire string). Defaults to INTERNAL
        // for unhandled exceptions.
        string ErrorCode { get; }

        // Best-effort cancel. Called from the main thread. Implementations are
        // expected to disengage MechJeb modules (via MechJebMcpUser) and set
        // IsDone after cleanup.
        void Cancel(string reason);
    }
}
