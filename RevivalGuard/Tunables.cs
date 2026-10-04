using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevivalGuard
{
    /// <summary>
    /// THE ANTI-CHEAT LIMITS LIVE ON THE SERVER, NOT IN THE SOURCE. This mod's source is published (ACE is
    /// AGPL, and the mod runs inside it), so a limit written here is a limit a cheater can read and stay
    /// just under. The values the shard actually uses are in tunables.json beside the dll (ModFolder),
    /// which is deployed separately and never published; the numbers in the code are only what runs
    /// when that file is missing.
    ///
    /// Format: one flat JSON object of numbers, e.g. { "movement.slack": 1.5 }. Read once, at first use;
    /// a change takes effect at the next restart. Unknown keys are ignored; a bad file is logged and the
    /// defaults are used.
    /// </summary>
    static class Tunables
    {
        static Dictionary<string, double> s_Values;
        static readonly object s_Lock = new object();

        public static double D(string key, double fallback)
        {
            Load();
            return s_Values.TryGetValue(key, out var v) ? v : fallback;
        }

        public static long L(string key, long fallback) => (long)D(key, fallback);

        static void Load()
        {
            if (s_Values != null) return;
            lock (s_Lock)
            {
                if (s_Values != null) return;
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                try
                {
                    string dir = ModFolder.Path();
                    string f = dir == null ? null : System.IO.Path.Combine(dir, "tunables.json");
                    if (f != null && File.Exists(f))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Number) values[p.Name] = p.Value.GetDouble();
                        Mod.Log.Info($"[RevivalGuard] tunables: {values.Count} values from tunables.json");
                    }
                    else Mod.Log.Warn("[RevivalGuard] tunables: no tunables.json beside the dll; using the built-in defaults");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] tunables: {e.Message}; using the built-in defaults"); }
                s_Values = values;
            }
        }
    }
}
