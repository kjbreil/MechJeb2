using System.Collections.Generic;
using UnityEngine;

namespace MuMech.Mcp
{
    // The stable reference identity used as the user-token in
    // ComputerModule.Users.Add(...) for every MCP-initiated engagement.
    //
    // Critically NOT a ComputerModule: ComputerModule requires a MechJebCore
    // at construction (per-vessel by design), making "singleton ComputerModule"
    // a category error. UserPool.Add(object) accepts any reference type — no
    // base class needed.
    //
    // The singleton tracks which (core, module) pairs it has engaged so
    // DisengageAll() can release them cleanly without touching parallel
    // human-UI engagements (the human's GUI instance is its OWN entry in
    // the module's Users list; removing MechJebMcpUser.Instance leaves that
    // untouched).
    public sealed class MechJebMcpUser
    {
        public static readonly MechJebMcpUser Instance = new MechJebMcpUser();

        private readonly object _lock = new object();
        private readonly Dictionary<MechJebCore, HashSet<ComputerModule>> _engaged
            = new Dictionary<MechJebCore, HashSet<ComputerModule>>();

        private MechJebMcpUser() { }

        public void Engage(ComputerModule module)
        {
            if (module == null) return;
            module.Users.Add(this);
            lock (_lock)
            {
                MechJebCore core = module.Core;
                if (!_engaged.TryGetValue(core, out HashSet<ComputerModule> set))
                {
                    set = new HashSet<ComputerModule>();
                    _engaged[core] = set;
                }
                set.Add(module);
            }
        }

        public void Disengage(ComputerModule module)
        {
            if (module == null) return;
            module.Users.Remove(this);
            lock (_lock)
            {
                if (_engaged.TryGetValue(module.Core, out HashSet<ComputerModule> set))
                {
                    set.Remove(module);
                    if (set.Count == 0) _engaged.Remove(module.Core);
                }
            }
        }

        // Walk all tracked engagements and remove MCP from their Users sets.
        // Used by saves/load (force:true) and by OpRegistry on vessel/scene
        // change abort.
        public List<string> DisengageAll(string reason)
        {
            var disengaged = new List<string>();
            lock (_lock)
            {
                foreach (KeyValuePair<MechJebCore, HashSet<ComputerModule>> kv in _engaged)
                {
                    foreach (ComputerModule m in kv.Value)
                    {
                        try { m.Users.Remove(this); disengaged.Add(m.GetType().Name); }
                        catch { /* module may have been destroyed; ignore */ }
                    }
                }
                _engaged.Clear();
            }
            if (disengaged.Count > 0)
                Debug.Log("[MechJeb-MCP] DisengageAll(" + reason + "): " + string.Join(",", disengaged.ToArray()));
            return disengaged;
        }

        // Drop a specific MechJebCore from tracking (e.g. on vessel destroy).
        // Does NOT call Users.Remove — the core/modules are already gone.
        public void DropCore(MechJebCore core)
        {
            lock (_lock)
            {
                _engaged.Remove(core);
            }
        }

        public int EngagedCount
        {
            get
            {
                int n = 0;
                lock (_lock) foreach (HashSet<ComputerModule> s in _engaged.Values) n += s.Count;
                return n;
            }
        }
    }
}
