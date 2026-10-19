using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AZIMAR'S VOICE (owner 2026-10-19, docs/findings/FINDINGS-buff-npc.md). NOT RETAIL. His emote table
    /// (gen_buff_zefir.py) keeps the ceremony, timing and spells; this rule only picks WHAT he says, for the three
    /// things ACE emotes cannot test: the hour (server clock), the player's level band, and not repeating himself.
    /// A Say/Tell/DirectBroadcast step of his whose TestString is "AzimarLines:&lt;kind&gt;" is spoken with a line picked
    /// from his own GotoSet emote sets named "AzimarLines:&lt;pool&gt;" (inert: nothing Gotos them), so lines are data and
    /// a datafix changes them without a restart. Without this rule the step's own Message plays (a plain fallback).
    /// Visits: the AzimarBlessingWait quest his ceremony already stamps (count and last time per character).
    /// Also: a Use while a ceremony runs starts a second one (ACE drops a Use on a busy NPC), the paced spells stop
    /// if their player logs off, and idle lines play only with a player within IDLE_RANGE, at most one per IDLE_GAP.
    ///   @modifybool azimar_voice false     his plain fallback lines; ACE's busy rule
    ///   @modifylong azimar_hour_offset N   hours added to the server clock for his time of day (box/OVH are UTC)
    /// </summary>
    static class AzimarVoice
    {
        internal const string P_ON = "azimar_voice", P_HOUR = "azimar_hour_offset";
        internal const uint WCID = 901631;
        const string TAG = "AzimarLines:", VISITS = "AzimarBlessingWait";
        const double IDLE_GAP = 180, IDLE_PER_PLAYER = 600, IDLE_AFTER_USE = 30;
        const float IDLE_RANGE = 12f;

        static readonly object s_Lock = new();
        static readonly Dictionary<string, List<string>> s_Recent = new();     // pool -> lines spoken lately
        static readonly Dictionary<uint, List<string>> s_Heard = new();        // player -> lines heard lately
        static readonly Dictionary<uint, double> s_IdleHeard = new();          // player -> last idle line time
        static readonly HashSet<uint> s_FirstNow = new();                       // players in their first ceremony
        static double s_LastIdle, s_LastUse;

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        internal static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard AzimarVoice: Azimar greets by name, level, hour and visit");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_HOUR)) l[P_HOUR] = new Property<long>(0, "RevivalGuard AzimarVoice: hours added to the server clock for Azimar's time of day");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarVoice: switches not registered ({e.Message}); still read at their defaults"); }
            Mod.Log.Info("[RevivalGuard] AzimarVoice: Azimar's lines picked by hour, level and visit (azimar_voice)");
        }

        // ---- pure choices ----

        internal static string Band(int level) => level <= 15 ? "low" : level <= 50 ? "mid" : level <= 125 ? "high" : "legend";

        internal static string TimeOfDay(int hour) => hour >= 5 && hour < 12 ? "morning" : hour < 17 && hour >= 12 ? "day"
            : hour >= 17 && hour < 21 ? "evening" : "night";

        internal static string Ordinal(int n)
        {
            var s = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return n + s;
        }

        /// <summary>The greeting pools in the order tried: a first visit, a long absence, else one of the rest.</summary>
        internal static List<string> GreetPools(int visits, double daysAway, int level, int hour)
        {
            if (visits <= 0) return new() { "greet.first" };
            if (daysAway >= 3) return new() { "greet.away" };
            var l = new List<string> { "greet.return", "greet.band." + Band(level), "greet.time." + TimeOfDay(hour) };
            if (visits >= 9) l.Add("greet.often");
            return l;
        }

        // ---- the hooks ----

        static int Hour()
        {
            long off = 0;
            try { off = PropertyManager.GetLong(P_HOUR, 0).Item; } catch { }
            return ((DateTime.Now.Hour + (int)(off % 24)) % 24 + 24) % 24;
        }

        static Dictionary<string, List<string>> Pools(WorldObject wo)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var sets = wo?.Biota?.PropertiesEmote;
            if (sets == null) return d;
            foreach (var s in sets)
            {
                if (s.Category != EmoteCategory.GotoSet || s.Quest == null || !s.Quest.StartsWith(TAG, StringComparison.Ordinal)) continue;
                var key = s.Quest.Substring(TAG.Length);
                if (!d.TryGetValue(key, out var lines)) d[key] = lines = new List<string>();
                foreach (var a in s.PropertiesEmoteAction)
                    if (!string.IsNullOrWhiteSpace(a.Message)) lines.Add(a.Message);
            }
            return d;
        }

        /// <summary>A line from one of the named pools (each pool equally likely), avoiding what this pool said lately and what this player
        /// heard lately; when both cannot hold, the pool's own rule wins.</summary>
        static string Pick(Dictionary<string, List<string>> pools, IEnumerable<string> names, uint player)
        {
            var cands = new List<(string pool, string line)>();
            foreach (var n in names)
                if (pools.TryGetValue(n, out var ls)) foreach (var x in ls) cands.Add((n, x));
            if (cands.Count == 0) return null;
            lock (s_Lock)
            {
                s_Heard.TryGetValue(player, out var heard);
                bool RecentIn(string pool, string line) => s_Recent.TryGetValue(pool, out var r) && r.Contains(line);
                var pick = cands.Where(c => !RecentIn(c.pool, c.line) && (heard == null || !heard.Contains(c.line))).ToList();
                if (pick.Count == 0) pick = cands.Where(c => !RecentIn(c.pool, c.line)).ToList();
                if (pick.Count == 0) pick = cands;
                // each named pool equally likely (a 3-line band pool is as likely as a 6-line one), then a line in it
                var inPools = pick.Select(c => c.pool).Distinct().ToList();
                var chosen = inPools[Random.Shared.Next(inPools.Count)];
                var mine = pick.Where(c => c.pool == chosen).ToList();
                var (pool, line) = mine[Random.Shared.Next(mine.Count)];
                if (!s_Recent.TryGetValue(pool, out var rec)) s_Recent[pool] = rec = new List<string>();
                int poolSize = pools[pool].Count;
                rec.Add(line); while (rec.Count > Math.Max(1, Math.Min(poolSize - 1, poolSize / 2 + 1))) rec.RemoveAt(0);
                if (player != 0)
                {
                    if (heard == null) { if (s_Heard.Count > 2000) s_Heard.Clear(); s_Heard[player] = heard = new List<string>(); }
                    heard.Add(line); while (heard.Count > 8) heard.RemoveAt(0);
                }
                return line;
            }
        }

        static string Line(WorldObject wo, string kind, Player p)
        {
            var pools = Pools(wo);
            uint id = p?.Guid.Full ?? 0;
            int hour = Hour();
            switch (kind)
            {
                case "greet":
                {
                    if (p == null) return null;
                    var q = p.QuestManager.GetQuest(VISITS);
                    int visits = q?.NumTimesCompleted ?? 0;
                    double away = q == null ? 0 : (Now - q.LastTimeCompleted) / 86400.0;
                    lock (s_Lock) { if (visits <= 0) s_FirstNow.Add(id); else s_FirstNow.Remove(id); s_LastUse = Now; }
                    var line = Pick(pools, GreetPools(visits, away, p.Level ?? 1, hour), id);
                    return line?.Replace("%visit", Ordinal(visits + 1));
                }
                case "bless":
                {
                    bool first; lock (s_Lock) { first = s_FirstNow.Remove(id); s_LastUse = Now; }
                    return Pick(pools, first && pools.ContainsKey("bless.first") ? new[] { "bless.first" } : new[] { "bless" }, id);
                }
                case "idle":
                    return Pick(pools, new[] { "idle", "idle.time." + TimeOfDay(hour) }, id);
                default:
                    return Pick(pools, new[] { kind }, id);
            }
        }

        /// <summary>The nearest player in range who has not heard an idle line lately, or null when he should stay quiet.</summary>
        static Player IdleListener(WorldObject wo)
        {
            double now = Now;
            lock (s_Lock) { if (now - s_LastIdle < IDLE_GAP || now - s_LastUse < IDLE_AFTER_USE) return null; }
            if (wo.Location == null || wo.CurrentLandblock == null) return null;
            Player best = null; float bestD = IDLE_RANGE;
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p?.Location == null || p.CurrentLandblock != wo.CurrentLandblock || p.Teleporting) continue;
                float d = wo.Location.DistanceTo(p.Location);
                if (d > bestD) continue;
                lock (s_Lock) { if (s_IdleHeard.TryGetValue(p.Guid.Full, out var t) && now - t < IDLE_PER_PLAYER) continue; }
                best = p; bestD = d;
            }
            if (best != null)
                lock (s_Lock) { s_LastIdle = now; if (s_IdleHeard.Count > 2000) s_IdleHeard.Clear(); s_IdleHeard[best.Guid.Full] = now; }
            return best;
        }

        /// <summary>ExecuteEmote prefix body. false = handled here (__result set).</summary>
        internal static bool Emote(EmoteManager em, PropertiesEmote set, PropertiesEmoteAction a, WorldObject target, ref float result)
        {
            var wo = em.WorldObject;
            if (a == null || wo == null || wo.WeenieClassId != WCID || !On) return true;

            // a paced spell for a player who has gone: the rest of his ceremony stops quietly
            if (a.Type == (uint)EmoteType.CastSpellInstant)
            {
                if (target is Player tp && PlayerManager.GetOnlinePlayer(tp.Guid) == null) { result = 0; return false; }
                return true;
            }
            if (a.TestString == null || !a.TestString.StartsWith(TAG, StringComparison.Ordinal)) return true;
            var kind = a.TestString.Substring(TAG.Length);

            Player p = target as Player;
            if (kind == "idle")
            {
                p = IdleListener(wo);
                if (p == null) { result = 0; return false; }
                target = p;
            }
            else if (p != null && PlayerManager.GetOnlinePlayer(p.Guid) == null) { result = 0; return false; }

            var line = Line(wo, kind, p);
            if (line == null) return true;   // no pool: his own fallback Message
            var said = new PropertiesEmoteAction { Type = a.Type, Extent = a.Extent, Message = line };
            result = em.ExecuteEmote(set, said, target);
            return false;
        }
    }

    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote))]
    static class AzimarVoiceEmote
    {
        static bool s_Done;
        static bool Prepare() { if (!s_Done) { s_Done = true; AzimarVoice.Register(); } return true; }

        static bool Prefix(EmoteManager __instance, PropertiesEmote emoteSet, PropertiesEmoteAction emote, WorldObject targetObject, ref float __result)
        {
            try { return AzimarVoice.Emote(__instance, emoteSet, emote, targetObject, ref __result); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] AzimarVoice emote: {e.Message}"); return true; }
        }
    }

    /// <summary>ACE ignores a Use while an NPC's emotes run; his ceremony is paced (~14 s), so a second player's Use
    /// starts its own chain beside the first (ExecuteEmoteSet nested, as ACE's own InqQuest branches do).</summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnUse))]
    static class AzimarVoiceUse
    {
        static bool Prefix(EmoteManager __instance, Creature activator)
        {
            try
            {
                if (__instance.WorldObject?.WeenieClassId != AzimarVoice.WCID || !__instance.IsBusy || !AzimarVoice.On) return true;
                __instance.ExecuteEmoteSet(EmoteCategory.Use, null, activator, true);
                return false;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] AzimarVoice use: {e.Message}"); return true; }
        }
    }
}
