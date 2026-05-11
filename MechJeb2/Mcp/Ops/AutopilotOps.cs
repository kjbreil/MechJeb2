using System;

namespace MuMech.Mcp
{
    // IRunningOp wrapper for capabilities that engage a MechJeb autopilot.
    // The op is "done" when the module's Users count goes to zero — i.e.
    // when either the autopilot self-disengages on completion or the user/
    // a GameEvent removes it.
    //
    // ComputerModule.Enabled mirrors UserPool.Count > 0, so we sample
    // Enabled on each FixedUpdate poll (which is exactly what OpRegistry
    // already does).
    internal sealed class AutopilotOp : IRunningOp
    {
        private readonly ComputerModule _module;
        private readonly Func<JsonValue> _resultFactory;
        private bool _cancelled;

        public AutopilotOp(ComputerModule module, Func<JsonValue> resultFactory = null)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _resultFactory = resultFactory;
        }

        public bool IsDone => _module == null || !_module.Enabled || _cancelled;
        public JsonValue Result => _resultFactory != null ? _resultFactory() : new JsonObject().Set("done", true);
        public string ErrorMessage => null;
        public string ErrorCode => null;

        public void Cancel(string reason)
        {
            _cancelled = true;
            MechJebMcpUser.Instance.Disengage(_module);
        }
    }

    // For commands that complete synchronously but want to look like a
    // handle for uniform polling — uncommon in v1. Reserved for future use.
    internal sealed class CompletedOp : IRunningOp
    {
        private readonly JsonValue _result;
        public CompletedOp(JsonValue result) { _result = result ?? JsonNull.Instance; }
        public bool IsDone => true;
        public JsonValue Result => _result;
        public string ErrorMessage => null;
        public string ErrorCode => null;
        public void Cancel(string reason) { }
    }
}
