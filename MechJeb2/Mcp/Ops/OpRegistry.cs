using System;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech.Mcp
{
    // Tracks active and recently-terminated MCP long-running operations.
    //
    // Guarantees (per plan deepen-review findings A10, D9):
    //   * At most one RUNNING op per capability path. Second invoke returns BUSY.
    //   * GameEvents.onVesselChange / onVesselSwitching / onGameSceneSwitchRequested
    //     / onGameStateLoad / onLevelWasLoaded synchronously transition all
    //     RUNNING handles to ABORTED before further game-state mutation.
    //   * Terminal handles retained 5 minutes for late polling, cap 100.
    //
    // Threading: registry methods are called from both the main thread
    // (engagement / FixedUpdate poll / GameEvents callbacks) and HTTP worker
    // threads (mj_ops_status). All access guarded by a single lock; ops
    // are small and lock contention is negligible at copilot rates.
    public sealed class OpRegistry : IDisposable
    {
        private const int RetainedCap = 100;
        private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(5);

        private readonly object _lock = new object();
        private readonly Dictionary<string, OpHandle> _activeByPath = new Dictionary<string, OpHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, OpHandle> _byHandle = new Dictionary<string, OpHandle>(StringComparer.Ordinal);
        // Terminal handles ordered by terminate-time (oldest first) for GC.
        private readonly LinkedList<OpHandle> _terminated = new LinkedList<OpHandle>();
        private bool _eventsSubscribed;

        public void SubscribeEvents()
        {
            if (_eventsSubscribed) return;
            try
            {
                GameEvents.onVesselChange.Add(OnVesselChange);
                GameEvents.onVesselSwitching.Add(OnVesselSwitching);
                GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitchRequested);
                GameEvents.onGameStateLoad.Add(OnGameStateLoad);
                GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
                _eventsSubscribed = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MechJeb-MCP] OpRegistry GameEvents subscribe failed: " + ex.Message);
            }
        }

        public void UnsubscribeEvents()
        {
            if (!_eventsSubscribed) return;
            try
            {
                GameEvents.onVesselChange.Remove(OnVesselChange);
                GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
                GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitchRequested);
                GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
                GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            }
            catch { }
            _eventsSubscribed = false;
        }

        // Attempt to register a new op for `path`. Returns null + reason if
        // another op already holds that path; caller turns that into BUSY.
        public OpHandle TryStart(string path, IRunningOp op, string requestId, out OpHandle conflict)
        {
            conflict = null;
            lock (_lock)
            {
                if (_activeByPath.TryGetValue(path, out OpHandle existing))
                {
                    conflict = existing;
                    return null;
                }
                var handle = new OpHandle
                {
                    Handle = OpHandle.NewHandleId(),
                    Path = path,
                    RequestId = requestId,
                    Op = op,
                    StartedUtc = DateTime.UtcNow,
                    Status = OpStatus.Running,
                };
                _activeByPath[path] = handle;
                _byHandle[handle.Handle] = handle;
                return handle;
            }
        }

        public OpHandle Get(string handle)
        {
            lock (_lock) { _byHandle.TryGetValue(handle, out OpHandle h); return h; }
        }

        public List<OpHandle> SnapshotAll()
        {
            lock (_lock)
            {
                var list = new List<OpHandle>(_byHandle.Count);
                foreach (OpHandle h in _byHandle.Values) list.Add(h);
                return list;
            }
        }

        // Drives terminal-state transitions when an op's underlying IRunningOp
        // reports IsDone. Called from McpServerAddon's FixedUpdate via the
        // main-thread dispatcher.
        public void Poll()
        {
            lock (_lock)
            {
                List<string> toRemove = null;
                foreach (KeyValuePair<string, OpHandle> kv in _activeByPath)
                {
                    OpHandle h = kv.Value;
                    if (h.Status != OpStatus.Running) { (toRemove ?? (toRemove = new List<string>())).Add(kv.Key); continue; }
                    if (h.Op == null) continue;
                    if (h.Op.IsDone)
                    {
                        h.TerminatedUtc = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(h.Op.ErrorCode))
                        {
                            h.Status = OpStatus.Failed;
                            h.ErrorCode = h.Op.ErrorCode;
                            h.ErrorMessage = h.Op.ErrorMessage ?? "";
                        }
                        else
                        {
                            h.Status = OpStatus.Succeeded;
                            h.Result = h.Op.Result;
                        }
                        (toRemove ?? (toRemove = new List<string>())).Add(kv.Key);
                    }
                }
                if (toRemove != null)
                {
                    foreach (string p in toRemove)
                    {
                        OpHandle h = _activeByPath[p];
                        _activeByPath.Remove(p);
                        _terminated.AddLast(h);
                    }
                    GcTerminated();
                }
            }
        }

        public bool Cancel(string handle, string reason = "client_cancel")
        {
            OpHandle h;
            lock (_lock) _byHandle.TryGetValue(handle, out h);
            if (h == null) return false;
            if (h.Status != OpStatus.Running) return false;
            try { h.Op?.Cancel(reason); } catch (Exception ex)
            {
                Debug.LogWarning("[MechJeb-MCP] op.Cancel threw: " + ex.Message);
            }
            lock (_lock)
            {
                h.Status = OpStatus.Cancelled;
                h.Reason = reason;
                h.TerminatedUtc = DateTime.UtcNow;
                _activeByPath.Remove(h.Path);
                _terminated.AddLast(h);
                GcTerminated();
            }
            return true;
        }

        // Synchronously transition all RUNNING handles to ABORTED. Used by
        // GameEvents callbacks and by saves/load (force:true).
        public void AbortAllSync(string reason)
        {
            List<OpHandle> aborted = new List<OpHandle>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, OpHandle> kv in _activeByPath)
                {
                    OpHandle h = kv.Value;
                    if (h.Status != OpStatus.Running) continue;
                    try { h.Op?.Cancel(reason); } catch { }
                    h.Status = OpStatus.Aborted;
                    h.Reason = reason;
                    h.TerminatedUtc = DateTime.UtcNow;
                    aborted.Add(h);
                }
                foreach (OpHandle h in aborted)
                {
                    _activeByPath.Remove(h.Path);
                    _terminated.AddLast(h);
                }
                GcTerminated();
            }
            if (aborted.Count > 0)
                Debug.Log("[MechJeb-MCP] AbortAllSync(" + reason + ") aborted " + aborted.Count + " handles");
        }

        private void GcTerminated()
        {
            DateTime cutoff = DateTime.UtcNow - RetentionWindow;
            // Drop expired records.
            while (_terminated.First != null && _terminated.First.Value.TerminatedUtc < cutoff)
            {
                _byHandle.Remove(_terminated.First.Value.Handle);
                _terminated.RemoveFirst();
            }
            // Enforce cap.
            while (_terminated.Count > RetainedCap)
            {
                _byHandle.Remove(_terminated.First.Value.Handle);
                _terminated.RemoveFirst();
            }
        }

        // -- GameEvents handlers ---------------------------------------------
        private void OnVesselChange(Vessel v) => AbortAllSync("vessel_changed");
        private void OnVesselSwitching(Vessel from, Vessel to) => AbortAllSync("vessel_switching");
        private void OnSceneSwitchRequested(GameEvents.FromToAction<GameScenes, GameScenes> e) => AbortAllSync("scene_changed");
        private void OnGameStateLoad(ConfigNode _) => AbortAllSync("game_loaded");
        private void OnLevelWasLoaded(GameScenes _) => AbortAllSync("level_loaded");

        public void Dispose()
        {
            UnsubscribeEvents();
            AbortAllSync("server_shutdown");
        }
    }
}
