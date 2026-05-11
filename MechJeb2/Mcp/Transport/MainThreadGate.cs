using System;
using System.Threading;
using UnityToolbag;

namespace MuMech.Mcp
{
    // Marshals HTTP-thread work onto Unity's main thread via the existing
    // MechJeb2/UnityToolbag/Dispatcher. Uses ManualResetEventSlim with a
    // short spin tail to keep cost ~zero when the main thread services the
    // queue within a frame or two (the common case), and a true kernel wait
    // for the deadline path.
    //
    // Why not Dispatcher.Invoke directly: the existing Invoke uses
    // Thread.Sleep(5) polling — 10-15ms latency floor on Mono. We use
    // InvokeAsync and our own event for the completion signal.
    //
    // Why not async/await + TaskCompletionSource: minimizes allocations in
    // the hot path; the HTTP listener thread that calls this can block
    // synchronously since it's already paying for one request.
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

            using (var done = new ManualResetEventSlim(false, spinCount: 10))
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try { result.Value = fn(); }
                    catch (Exception ex) { result.Error = ex; }
                    finally { result.Completed = true; done.Set(); }
                });

                if (!done.Wait(timeoutMs))
                {
                    result.TimedOut = true;
                    // Note: the action remains queued in the Dispatcher and will
                    // still execute on the next main-thread tick. The caller
                    // observes a timeout; we let the action run to completion
                    // rather than try to cancel it (the existing Dispatcher has
                    // no cancellation token concept).
                }
            }

            return result;
        }

        public static Result<object> Run(Action action, int timeoutMs = DefaultTimeoutMs)
        {
            return Run<object>(() => { action(); return null; }, timeoutMs);
        }
    }
}
