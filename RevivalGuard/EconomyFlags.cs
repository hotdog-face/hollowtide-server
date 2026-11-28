using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RevivalGuard
{
    /// <summary>EconomyWatch's pure pieces (no ACE types), so NetTest can run them (tools/nettest/EconomyFlagsCase.cs).</summary>
    sealed class FlagGate
    {
        public DateTime LastFlag;

        /// <summary>True when no flag was raised in the last windowMinutes.</summary>
        public bool TryFlag(DateTime now, double windowMinutes)
        {
            if (LastFlag != default && (now - LastFlag).TotalMinutes < windowMinutes) return false;
            LastFlag = now;
            return true;
        }
    }

    sealed class BackgroundAppender
    {
        public const int MaxQueued = 10_000;
        readonly BlockingCollection<(string path, string line)> _q = new BlockingCollection<(string, string)>(MaxQueued);
        readonly Action<string> _warn;
        long _dropped, _written, _pending;
        Thread _thread;

        public BackgroundAppender(Action<string> warn = null) { _warn = warn; }

        public long Dropped => Interlocked.Read(ref _dropped);
        public long Written => Interlocked.Read(ref _written);

        public bool Enqueue(string path, string line)
        {
            Start();
            Interlocked.Increment(ref _pending);
            if (_q.TryAdd((path, line))) return true;
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        /// <summary>Waits until everything queued so far is on disk.</summary>
        public bool Flush(int timeoutMs)
        {
            var until = Environment.TickCount64 + timeoutMs;
            while (Interlocked.Read(ref _pending) > 0)
            {
                if (Environment.TickCount64 > until) return false;
                Thread.Sleep(5);
            }
            return true;
        }

        void Start()
        {
            if (_thread != null) return;
            lock (_q)
            {
                if (_thread != null) return;
                var t = new Thread(Run) { IsBackground = true, Name = "RevivalGuard appender" };
                t.Start();
                _thread = t;
            }
        }

        void Run()
        {
            foreach (var (path, line) in _q.GetConsumingEnumerable())
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line + Environment.NewLine);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception e) { try { _warn?.Invoke($"append to {path} failed: {e.Message}"); } catch { } }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
    }
}
