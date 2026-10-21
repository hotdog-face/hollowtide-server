using System;
using System.Collections.Generic;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;

namespace RevivalGuard
{
    /// <summary>
    /// HOW THE GAME RUNS FOR REAL PLAYERS (docs/PERF-PLAYABILITY-02.md item 8). The client (AcPerfReport) sends
    /// "@perfstat key=value ..." every 10 minutes in the world; this writes it to the player event log as ev "perf",
    /// with only the known keys, each value capped. tools/gpubox-ace/perf_report.py turns a week of them into a report
    /// by platform. Silent: the player sees no reply.
    /// </summary>
    public static class PerfStat
    {
        static readonly HashSet<string> NUMS = new HashSet<string> { "frames", "fps_med", "fps_p5", "slow_pct", "worst_ms", "heap_used_mb", "heap_mb" };
        static readonly HashSet<string> TEXTS = new HashSet<string> { "v", "plat", "gpu", "screen", "arrivals" };

        // ONE SAMPLE PER SESSION PER 5 MINUTES (security audit 2026-10-20): the client sends one every 10 minutes, and a
        // typed or scripted flood must not fill the event log. Weak keys: a session that ends takes its entry with it.
        static readonly TimeSpan MIN_GAP = TimeSpan.FromMinutes(5);
        sealed class Last { public DateTime At; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Session, Last> s_Last =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Session, Last>();

        public static void Register() =>
            CommandManager.TryAddCommand(Handle, "perfstat", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Client performance sample (sent by the client itself).", "");

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || parameters == null) return;
            var last = s_Last.GetValue(session, _ => new Last { At = DateTime.MinValue });
            lock (last)
            {
                var now = DateTime.UtcNow;
                if (now - last.At < MIN_GAP) return;   // silent, as the command always is
                last.At = now;
            }
            var fields = new Dictionary<string, object>();
            foreach (var tok in parameters)
            {
                int eq = tok.IndexOf('=');
                if (eq <= 0) continue;
                string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                if (v.Length > 80) v = v.Substring(0, 80);
                if (NUMS.Contains(k) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) fields[k] = d;
                else if (TEXTS.Contains(k)) fields[k] = v;
            }
            if (fields.Count == 0) return;
            Telemetry.Emit("perf", p, ",\"perf\":" + System.Text.Json.JsonSerializer.Serialize(fields));
        }
    }
}
