using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// WHICH SHOP SELLS WHAT THE ASSISTANT IS SHORT OF (Wolfgang Puck 261112-0012, 0.1.105: "assistant restock tried to go to
    /// fletcher for stamina tinctures"). Ours. A supply run visited the vendors near its landing nearest first, knowing
    /// nothing of their stock until a window opened, and only the ones its client could see: indoor vendors in other
    /// buildings are not sent to a client outside them. In that town (0xA1A4) the Fletcher was nearer; the one vendor that
    /// sells Stamina Tinctures is Danifa Gondmad the Healer, in another building.
    /// Our client asks "@shopsfor name;name;..." (its Keep List) on landing and gets, on the mod channel "shopsfor", every
    /// vendor within R of the player (this landblock and the ones around it), nearest first: guid, name, position, and which
    /// of the asked names its shop list holds (exact name, or a name the asked one is the singular of). The shop list is
    /// the vendor's create list (DestinationType Shop), read from the weenie cache: nothing is made, no vendor is loaded.
    /// Our client only (ModChannel.Capable); one answer per player per 2 s.
    /// </summary>
    static class ShopsFor
    {
        internal const string CHANNEL = "shopsfor";
        const float R = 120f;
        const int MAX = 16, MAX_NAMES = 40;
        static readonly ConcurrentDictionary<uint, DateTime> s_Last = new();

        internal static void Register() =>
            CommandManager.TryAddCommand(Ask, "shopsfor", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client's supply run asks which vendors near you sell the items on its Keep List (answered on the mod channel).", "<name;name;...>");

        static void Ask(Session session, params string[] parameters)
        {
            try
            {
                var p = session?.Player; var lb = p?.CurrentLandblock;
                if (p?.Location == null || lb == null || !ModChannel.Capable(session)) return;
                var now = DateTime.UtcNow;
                if (s_Last.TryGetValue(p.Guid.Full, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
                s_Last[p.Guid.Full] = now;
                var want = ShopsForRule.Names(string.Join(" ", parameters ?? Array.Empty<string>()), MAX_NAMES);
                var rows = new List<(float d, Vendor v, List<string> sells)>();
                var seen = new HashSet<uint>();
                foreach (var block in new[] { lb }.Concat(lb.Adjacents.Where(a => a != null)))
                    foreach (var o in block.GetAllWorldObjectsForDiagnostics())
                    {
                        if (!(o is Vendor v) || v.Location == null || v.Visibility || !seen.Add(v.Guid.Full)) continue;
                        float d = p.Location.DistanceTo(v.Location);
                        if (d > R) continue;
                        rows.Add((d, v, Sells(v, want)));
                    }
                var sb = new StringBuilder("{\"v\":[");
                int n = 0;
                foreach (var r in rows.OrderBy(x => x.d).Take(MAX))
                {
                    var loc = r.v.Location;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"g\":").Append(r.v.Guid.Full).Append(",\"n\":").Append(Telemetry.J(r.v.Name ?? ""))
                      .Append(",\"c\":").Append(loc.Cell)
                      .Append(",\"x\":").Append(F(loc.PositionX)).Append(",\"y\":").Append(F(loc.PositionY)).Append(",\"z\":").Append(F(loc.PositionZ))
                      .Append(",\"d\":").Append(F(r.d)).Append(",\"s\":[")
                      .Append(string.Join(",", r.sells.Select(Telemetry.J))).Append("]}");
                }
                sb.Append("]}");
                ModChannel.Send(session, CHANNEL, sb.ToString());
                if (now.Minute != last.Minute || s_Last.Count > 2000)
                    foreach (var kv in s_Last) if (now - kv.Value > TimeSpan.FromMinutes(5)) s_Last.TryRemove(kv.Key, out _);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] shopsfor: {e.Message}"); }
        }

        static string F(float f) => f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        static List<string> Sells(Vendor v, List<string> want)
        {
            var hits = new List<string>();
            if (want.Count == 0) return hits;
            var list = v.Biota?.PropertiesCreateList;
            if (list == null) return hits;
            var names = new List<string>();
            foreach (var e in list)
            {
                if (e.DestinationType != DestinationType.Shop) continue;
                var w = DatabaseManager.World.GetCachedWeenie(e.WeenieClassId);
                if (w != null) names.Add(w.GetName());
            }
            foreach (var a in want) if (names.Any(n => ShopsForRule.Matches(n, a))) hits.Add(a);
            return hits;
        }
    }

    /// <summary>ShopsFor's rule, pure (tools/modcheck).</summary>
    public static class ShopsForRule
    {
        /// <summary>"a;b; c" -> distinct trimmed names, at most <paramref name="max"/>.</summary>
        public static List<string> Names(string text, int max)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(text)) return r;
            foreach (var part in text.Split(';'))
            {
                string s = part.Trim();
                if (s.Length == 0 || s.Length > 80 || r.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) continue;
                r.Add(s);
                if (r.Count >= max) break;
            }
            return r;
        }

        /// <summary>A shop item answers an asked name: the same name, or the asked name is a plural whose singular the item's
        /// name holds ("healing kits" -> "Crude Healing Kit").</summary>
        public static bool Matches(string item, string asked)
        {
            if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(asked)) return false;
            if (string.Equals(item.Trim(), asked, StringComparison.OrdinalIgnoreCase)) return true;
            if (asked.Length > 3 && asked.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                return item.IndexOf(asked.Substring(0, asked.Length - 1), StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }
    }
}
