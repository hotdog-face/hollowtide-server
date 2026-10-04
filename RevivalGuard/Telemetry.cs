using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE PLAYER EVENT LOG (docs/PLAYER-DATA-PLAN.md, phase 1; owner 2026-10-03 "go phase 1"). From the
    /// first soft-launch player on, one JSON line per event in ~/ace/reports/events/&lt;yyyy-MM-dd&gt;.jsonl,
    /// so questions we think of later (where do new players stop? what kills them? who comes back?) can
    /// still be answered. ACE's database holds only the current state of a character, never its history.
    ///
    /// Every field: t (UTC), ev, and for a player a (account: a salted hash, never the name), c
    /// (character name), g (guid), lvl, lb (landblock), staff:1 for admin and bot accounts (filter them
    /// out of any analysis). NOT RECORDED: chat, IP addresses, passwords. The salt is
    /// events/.salt, made once; without it the hashes cannot be tied back to accounts.
    ///
    /// Events: login, logout (seconds), chargen (build), academy_exit (RecallsDisabled cleared), where (every 60 s: position, coin, total XP,
    /// kills since the last sample by creature name), levelup, death (killer), quest (stamp), tp
    /// (teleports: portal or not), buy (vendor purchases), fellow, swear, house.
    ///
    /// ALL POSTFIXES/PREFIXES THAT ONLY READ. Nothing here changes what ACE does, and every hook
    /// swallows its own exceptions: a telemetry failure must never cost a player anything.
    /// </summary>
    static class Telemetry
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "events");
        static readonly object s_FileLock = new object();
        static string s_Salt;

        const double WHERE_SECONDS = 60;

        internal sealed class Live
        {
            public DateTime Start = DateTime.UtcNow, LastWhere = DateTime.MinValue;
            public int Level;
            public bool InAcademy;    // RecallsDisabled at login: its clearing is the Academy exit
            public readonly Dictionary<string, int> Kills = new Dictionary<string, int>();
        }
        internal static readonly ConditionalWeakTable<Player, Live> s_Live = new ConditionalWeakTable<Player, Live>();

        // ---------------------------------------------------------------- writing

        static string Salt()
        {
            if (s_Salt != null) return s_Salt;
            lock (s_FileLock)
            {
                if (s_Salt != null) return s_Salt;
                Directory.CreateDirectory(Dir);
                string f = Path.Combine(Dir, ".salt");
                if (!File.Exists(f))
                {
                    var b = new byte[16];
                    RandomNumberGenerator.Fill(b);
                    File.WriteAllText(f, Convert.ToHexString(b));
                    try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                }
                s_Salt = File.ReadAllText(f).Trim();
                return s_Salt;
            }
        }

        internal static string Acct(uint accountId)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Salt() + ":" + accountId.ToString(CultureInfo.InvariantCulture)));
            return Convert.ToHexString(h, 0, 6).ToLowerInvariant();
        }

        internal static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch); break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>Write one event. `extra` is a JSON fragment starting with a comma (or empty).</summary>
        internal static void Emit(string ev, Player p, string extra, uint? accountId = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                var sb = new StringBuilder(256);
                sb.Append("{\"t\":\"").Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z\",\"ev\":\"").Append(ev).Append('"');
                if (p != null)
                {
                    uint acct = accountId ?? p.Account?.AccountId ?? p.Character?.AccountId ?? 0;
                    sb.Append(",\"a\":\"").Append(Acct(acct)).Append("\",\"c\":").Append(J(p.Name))
                      .Append(",\"g\":\"").Append(p.Guid.Full.ToString("X8")).Append("\",\"lvl\":").Append(p.Level ?? 1);
                    if ((p.Account?.AccessLevel ?? 0) > 0) sb.Append(",\"staff\":1");
                    var loc = p.Location;
                    if (loc != null) sb.Append(",\"lb\":\"").Append((loc.Cell >> 16).ToString("X4")).Append('"');
                }
                sb.Append(extra).Append('}');
                lock (s_FileLock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"), sb.Append('\n').ToString());
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry {ev}: {e.Message}"); }
        }

        /// <summary>The client's feature counters (AcUsage, through the ticket desk as kind "usage"): one
        /// JSON object, re-serialised here so only well-formed JSON reaches the log.</summary>
        internal static void Usage(Player p, string body)
        {
            try
            {
                if (body == null || body.Length > 8000) return;
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                Emit("usage", p, ",\"u\":" + System.Text.Json.JsonSerializer.Serialize(doc.RootElement));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry usage: {e.Message}"); }
        }

        static string KillsJson(Live l)
        {
            lock (l)
            {
                if (l.Kills.Count == 0) return "";
                var s = ",\"kills\":{" + string.Join(",", l.Kills.Select(k => J(k.Key) + ":" + k.Value)) + "}";
                l.Kills.Clear();
                return s;
            }
        }

        // ---------------------------------------------------------------- sessions

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.SwitchPlayerFromOfflineToOnline))]
        static class Login
        {
            static void Postfix(Player player, bool __result)
            {
                try
                {
                    if (!__result || player == null) return;
                    var l = s_Live.GetValue(player, _ => new Live());
                    l.Start = DateTime.UtcNow; l.Level = player.Level ?? 1;
                    l.InAcademy = player.GetProperty(PropertyBool.RecallsDisabled) ?? false;
                    Emit("login", player, $",\"played\":{player.Age ?? 0}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry login: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.SwitchPlayerFromOnlineToOffline))]
        static class Logout
        {
            static void Prefix(Player player)
            {
                try
                {
                    if (player == null) return;
                    string extra = "";
                    if (s_Live.TryGetValue(player, out var l))
                    {
                        extra = $",\"secs\":{(int)(DateTime.UtcNow - l.Start).TotalSeconds}" + KillsJson(l);
                        s_Live.Remove(player);
                    }
                    Emit("logout", player, extra + $",\"played\":{player.Age ?? 0}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry logout: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- character creation

        [HarmonyPatch(typeof(PlayerFactory), nameof(PlayerFactory.Create))]
        static class Chargen
        {
            static void Postfix(uint accountId, ref Player player, PlayerFactory.CreateResult __result)
            {
                try
                {
                    if (__result != PlayerFactory.CreateResult.Success || player == null) return;
                    var p = player;
                    var sb = new StringBuilder();
                    sb.Append(",\"heritage\":").Append(J(p.HeritageGroup.ToString()))
                      .Append(",\"gender\":").Append(p.Gender ?? 0)
                      .Append(",\"template\":").Append(J(p.GetProperty(PropertyString.Template)));
                    sb.Append(",\"attrs\":{");
                    sb.Append(string.Join(",", p.Attributes.Select(a => J(a.Key.ToString()) + ":" + a.Value.StartingValue)));
                    sb.Append("},\"spec\":[").Append(string.Join(",", p.Skills.Where(s => s.Value.AdvancementClass == SkillAdvancementClass.Specialized).Select(s => J(s.Key.ToString()))))
                      .Append("],\"trained\":[").Append(string.Join(",", p.Skills.Where(s => s.Value.AdvancementClass == SkillAdvancementClass.Trained).Select(s => J(s.Key.ToString()))))
                      .Append(']');
                    Emit("chargen", p, sb.ToString(), accountId);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry chargen: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- the minute sample

        [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat))]
        static class Where
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p?.Session == null) return;
                    var l = s_Live.GetValue(p, _ => new Live { Level = p.Level ?? 1 });
                    int lvl = p.Level ?? 1;
                    if (l.Level != 0 && lvl > l.Level)
                        Emit("levelup", p, $",\"from\":{l.Level},\"played\":{p.Age ?? 0}");
                    l.Level = lvl;
                    if (l.InAcademy && !(p.GetProperty(PropertyBool.RecallsDisabled) ?? false))
                    {
                        l.InAcademy = false;
                        Emit("academy_exit", p, $",\"played\":{p.Age ?? 0}");
                    }

                    var now = DateTime.UtcNow;
                    if ((now - l.LastWhere).TotalSeconds < WHERE_SECONDS) return;
                    l.LastWhere = now;
                    var loc = p.Location;
                    string pos = loc == null ? "" :
                        $",\"cell\":\"{loc.Cell:X8}\",\"x\":{F(loc.PositionX)},\"y\":{F(loc.PositionY)},\"z\":{F(loc.PositionZ)}";
                    Emit("where", p, pos + $",\"coin\":{p.CoinValue ?? 0},\"xp\":{p.TotalExperience ?? 0}" + KillsJson(l));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry where: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- deaths and kills

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
        static class PlayerDeath
        {
            static void Postfix(Player __instance, DamageHistoryInfo lastDamager, DamageType damageType)
            {
                try
                {
                    var killer = lastDamager?.TryGetPetOwnerOrAttacker();
                    var loc = __instance.Location;
                    Emit("death", __instance,
                        $",\"by\":{J(lastDamager?.Name)},\"by_player\":{((lastDamager?.IsPlayer ?? false) ? 1 : 0)}" +
                        $",\"by_wcid\":{killer?.WeenieClassId ?? 0},\"dmg\":{J(damageType.ToString())}" +
                        (loc == null ? "" : $",\"cell\":\"{loc.Cell:X8}\",\"x\":{F(loc.PositionX)},\"y\":{F(loc.PositionY)},\"z\":{F(loc.PositionZ)}"));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry death: {e.Message}"); }
            }
        }

        // Die(lastDamager, topDamager), not OnDeath: every death passes through it with the killer, and
        // @smite calls OnDeath() with no damager at all (Creature_Death.cs, Smite).
        [HarmonyPatch(typeof(Creature), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        static class Kill
        {
            static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
            {
                try
                {
                    if (__instance is Player) return;
                    if (!(lastDamager?.TryGetPetOwnerOrAttacker() is Player killer)) return;
                    var l = s_Live.GetValue(killer, _ => new Live { Level = killer.Level ?? 1 });
                    string name = __instance.Name ?? ("wcid " + __instance.WeenieClassId);
                    lock (l) { l.Kills.TryGetValue(name, out int n); l.Kills[name] = n + 1; }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry kill: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- content, travel, economy, social

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update))]
        static class Quest
        {
            static void Postfix(QuestManager __instance, string questFormat)
            {
                try
                {
                    if (__instance?.Creature is Player p)
                        Emit("quest", p, $",\"q\":{J(QuestManager.GetQuestName(questFormat))}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry quest: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Teleport))]
        static class Tp
        {
            static void Prefix(Player __instance, Position _newPosition, bool fromPortal)
            {
                try
                {
                    if (__instance?.Session == null || _newPosition == null) return;
                    Emit("tp", __instance, $",\"to\":\"{_newPosition.Cell:X8}\",\"portal\":{(fromPortal ? 1 : 0)}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry tp: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.FinalizeBuyTransaction))]
        static class Buy
        {
            static void Postfix(Player __instance, Vendor vendor, List<WorldObject> genericItems, List<WorldObject> uniqueItems, uint cost)
            {
                try
                {
                    var items = (genericItems ?? new List<WorldObject>()).Concat(uniqueItems ?? new List<WorldObject>())
                        .GroupBy(w => w.Name ?? "?").Take(12)
                        .Select(g => J(g.Key) + ":" + g.Sum(w => (long)(w.StackSize ?? 1)));
                    Emit("buy", __instance, $",\"vendor\":{J(vendor?.Name)},\"cost\":{cost},\"items\":{{{string.Join(",", items)}}}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry buy: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.FellowshipCreate))]
        static class Fellow
        {
            static void Postfix(Player __instance) => Emit("fellow", __instance, "");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SwearAllegiance))]
        static class Swear
        {
            static void Postfix(Player __instance, uint targetGuid)
            {
                try { if (__instance?.PatronId == targetGuid) Emit("swear", __instance, $",\"patron\":\"{targetGuid:X8}\""); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry swear: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetHouseOwner))]
        static class House
        {
            static void Postfix(Player __instance, SlumLord slumlord)
            {
                try { Emit("house", __instance, $",\"house\":{J(slumlord?.Name)}"); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] telemetry house: {e.Message}"); }
            }
        }
    }
}
