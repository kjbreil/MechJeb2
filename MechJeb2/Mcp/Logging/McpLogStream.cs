using System;
using System.Threading;
using UnityEngine;

namespace MuMech.Mcp
{
    // In-process log capture. Subscribes to Application.logMessageReceivedThreaded
    // and stores records in a fixed-size circular buffer keyed by monotonic
    // sequence number.
    //
    // Hot-path discipline (per deepen-plan finding E4):
    //   * The Unity callback fires on whatever thread emitted the log. We do
    //     the minimum work: monotonic seq, ticks, level, message, and the
    //     stack-trace string Unity already gives us (free for Error/Exception
    //     levels). NO stack-trace inspection in the callback.
    //   * Tag/module attribution is heuristic at READ time (parse "[MechJeb*]"
    //     prefix from message). The callback never touches strings except to
    //     store the reference.
    //   * Capacity rounded to a power of 2 so indexing is bitwise mask, no
    //     modulo or .Count walk.
    public sealed class McpLogStream : IDisposable
    {
        public const int DefaultCapacity = 16_384;       // power of 2; rounded up from plan's 10K target.

        private readonly McpLogRecord[] _buffer;
        private readonly int _mask;
        private long _seq;     // next slot to write; increment first, then write to (seq-1) & mask
        private bool _subscribed;

        public McpLogStream(int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            int cap = 1;
            while (cap < capacity) cap <<= 1;
            _buffer = new McpLogRecord[cap];
            _mask = cap - 1;
        }

        public int Capacity => _buffer.Length;
        public long NextSeq => Interlocked.Read(ref _seq);

        public void Subscribe()
        {
            if (_subscribed) return;
            Application.logMessageReceivedThreaded += OnLogReceived;
            _subscribed = true;
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            Application.logMessageReceivedThreaded -= OnLogReceived;
            _subscribed = false;
        }

        private void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            long s = Interlocked.Increment(ref _seq) - 1;
            int idx = (int)(s & _mask);
            _buffer[idx].Seq = s;
            _buffer[idx].Ticks = DateTime.UtcNow.Ticks;
            _buffer[idx].Level = type;
            _buffer[idx].Message = condition;
            _buffer[idx].StackTrace = (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                ? stackTrace
                : null;
        }

        // Snapshot the last N records, oldest-first. Filtering happens in the
        // caller — we just hand back raw records.
        public McpLogRecord[] Snapshot(long sinceSeq, long untilSeq, int limit)
        {
            long head = Interlocked.Read(ref _seq);
            if (head == 0) return new McpLogRecord[0];
            long startSeq = Math.Max(0, head - _buffer.Length);   // oldest still valid
            if (sinceSeq > 0) startSeq = Math.Max(startSeq, sinceSeq);
            long endSeq = head;
            if (untilSeq > 0) endSeq = Math.Min(endSeq, untilSeq);
            int span = (int)Math.Min(endSeq - startSeq, limit);
            if (span <= 0) return new McpLogRecord[0];
            var result = new McpLogRecord[span];
            for (int i = 0; i < span; i++)
            {
                long s = startSeq + i;
                int idx = (int)(s & _mask);
                McpLogRecord rec = _buffer[idx];
                // Detect slot overwrite — if the slot's seq has rolled past
                // what we expect, the record was overwritten by a newer one.
                if (rec.Seq != s)
                {
                    // Skip; truncate and return what we have so far.
                    var truncated = new McpLogRecord[i];
                    Array.Copy(result, truncated, i);
                    return truncated;
                }
                result[i] = rec;
            }
            return result;
        }

        public void Dispose() { Unsubscribe(); }
    }
}
