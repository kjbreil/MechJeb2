using System;
using System.Threading.Tasks;
using UnityToolbag;

namespace MuMech.Mcp
{
    // Marshals HTTP-thread work onto Unity's main thread via the existing
    // MechJeb2/UnityToolbag/Dispatcher.
    //
    // Why not Dispatcher.Invoke directly: the existing Invoke uses
    // Thread.Sleep(5) polling — 10-15ms latency floor on Mono.
    //
    // Implementation uses TaskCompletionSource because it has no disposal
    // contract: on timeout, the still-queued Dispatcher action will eventually
    // run on the main thread and call TrySetResult(...) which is safe even if
    // nobody is awaiting the Task anymore. (An earlier ManualResetEventSlim
    // version raced: disposing the MRE in the timeout branch while the
    // queued action held a live reference led to ObjectDisposedException on
    // the Unity main thread.)
    public static class MainThreadGate
    {
        public const int DefaultTimeoutMs = 5_000;

        public sealed class Result<T>
        {
            public bool Completed;
            public bool TimedOut;
            public T Value;
            public Exception Error;
        }

        public static Result<T> Run<T>(Func<T> fn, int timeoutMs = DefaultTimeoutMs)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            var result = new Result<T>();

            // Fast path: already on the main thread (e.g. KSP UI invoked the
            // server somehow, or unit-test scenario).
            if (Dispatcher.isMainThread)
            {
                try
                {
                    result.Value = fn();
                    result.Completed = true;
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                    result.Completed = true;
                }
                return result;
            }

            // RunContinuationsAsynchronously: defensive choice in case a future
            // caller awaits this Task — otherwise the continuation would run
            // synchronously on whichever thread called TrySetResult (in our
            // case, Unity's main thread), pulling HTTP-response writes onto
            // the main thread and blocking the next game frame.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Dispatcher.InvokeAsync(() =>
            {
                try { result.Value = fn(); }
                catch (Exception ex) { result.Error = ex; }
                finally
                {
                    result.Completed = true;
                    // TrySet is safe even after the HTTP thread gave up; no
                    // exception is raised in the "nobody's waiting" case.
                    tcs.TrySetResult(true);
                }
            });

            if (!tcs.Task.Wait(timeoutMs))
            {
                result.TimedOut = true;
                // The queued action remains in the Dispatcher's queue and will
                // run on the next main-thread tick. We just stopped waiting.
                // The action's TrySetResult is safe; result.Completed will be
                // set after the fact (observable for future polling, though
                // Phase 1 doesn't poll).
            }

            return result;
        }

        public static Result<object> Run(Action action, int timeoutMs = DefaultTimeoutMs)
        {
            return Run<object>(() => { action(); return null; }, timeoutMs);
        }
    }
}
