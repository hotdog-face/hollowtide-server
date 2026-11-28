using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevivalGuard
{
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
