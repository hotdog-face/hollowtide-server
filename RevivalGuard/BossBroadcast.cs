using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Entity.Models;
using HarmonyLib;
using R = RevivalGuard.BossBroadcastRule;

namespace RevivalGuard
{
    /// <summary>
    /// BOSS KILL NEWS: "The Thunder Turkey has been slain by X and the fellowship Y!" to everyone online. Ours, not retail
    /// (owner 2026-11-21). The list and the limits are BossBroadcastRule's. Switch `rg_boss_broadcast`, default ON (owner's yes
    /// 2026-11-21). @bossbroadcast: status, on|off, test [wcid], list [text], add &lt;wcid&gt; [name], remove &lt;wcid&gt;, reload.
    /// Other code can make one creature a boss for its life with Mark (the Holtburg raid's leader).
    /// </summary>
    static class BossBroadcast
    {
        public const string P_ON = "rg_boss_broadcast";
        const string OVERRIDES = "BossBroadcastOverrides.json";

        static readonly object s_Lock = new object();
        static R.Data s_Data = new R.Data();
        static R.Overrides s_Over = new R.Overrides();
        static readonly R.Limiter s_Limit = new R.Limiter();
        static readonly ConditionalWeakTable<Creature, string> s_Marked = new ConditionalWeakTable<Creature, string>();
        static int s_Registered, s_Errors;
        static string s_LoadNote = "not loaded";

        static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static string OverridePath => SeasonalCalendar.FileBeside(OVERRIDES);

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "BossBroadcast: world line when a boss is slain (ours, docs/findings/boss-broadcast-2026-11-21.md)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BossBroadcast: switch not registered with @modify* ({e.Message}); default on"); }
            Load();
            CommandManager.TryAddCommand(Handle, "bossbroadcast", AccessLevel.Admin, CommandHandlerFlag.None,
                "Boss kill news (ours): status; on|off; test [wcid] (to you only); list [text]; add <wcid> [name to say]; remove <wcid>; reload.",
                "[on|off|test [wcid]|list [text]|add <wcid> [name]|remove <wcid>|reload]");
            Mod.Log.Info($"[RevivalGuard] BossBroadcast: {(On ? "ON" : "off")}; {s_LoadNote}");
        }

        static void Load()
        {
            lock (s_Lock)
            {
                try
                {
                    using var rs = typeof(BossBroadcast).Assembly.GetManifestResourceStream("RevivalGuard.BossBroadcast.json");
                    using var rd = new StreamReader(rs);
                    s_Data = R.ParseData(rd.ReadToEnd());
                }
                catch (Exception e) { s_Data = new R.Data(); Mod.Log.Error($"[RevivalGuard] BossBroadcast: list unreadable: {e.Message}"); }
                try { s_Over = File.Exists(OverridePath) ? R.ParseOverrides(File.ReadAllText(OverridePath)) : new R.Overrides(); }
                catch (Exception e) { s_Over = new R.Overrides(); Mod.Log.Error($"[RevivalGuard] BossBroadcast: {OVERRIDES} unreadable, ignored: {e.Message}"); }
                s_LoadNote = $"{R.Effective(s_Data, s_Over).Count} bosses ({s_Data.Bosses.Count} listed, {s_Data.Own.Count} with their own line, "
                    + $"{s_Data.Deny.Count} denied; overrides +{s_Over.Allow.Count} -{s_Over.Deny.Count}); cooldown {s_Data.CooldownSeconds} s, {s_Data.PerMinute}/min";
            }
        }

        static bool SaveOverrides(out string err)
        {
            err = null;
            try { var tmp = OverridePath + ".tmp"; File.WriteAllText(tmp, R.Write(s_Over)); File.Move(tmp, OverridePath, true); return true; }
            catch (Exception e) { err = e.Message; return false; }
        }

        /// <summary>This creature is a boss for its life, said as <paramref name="name"/> (TownRaid's leader).</summary>
        internal static void Mark(Creature c, string name)
        {
            if (c == null) return;
            try { s_Marked.AddOrUpdate(c, name); } catch { }
        }

        internal static void OnDeath(Creature c, DamageHistoryInfo lastDamager)
        {
            if (c == null || c is Player || !On) return;
            if (MonsterFun.IsChampion(c)) return;            // MonsterFun says its own line
            string name; uint key;
            if (s_Marked.TryGetValue(c, out var marked)) { name = marked; key = 0xFFFF0000u | (c.WeenieClassId & 0xFFFF); }
            else
            {
                lock (s_Lock) { if (R.Listed(s_Data, s_Over, c.WeenieClassId, out name) != R.Why.Say) return; }
                name ??= c.Name;
                key = c.WeenieClassId;
            }
            Player killer = null;
            try { killer = (lastDamager ?? c.DamageHistory?.LastDamager)?.TryGetPetOwnerOrAttacker() as Player; } catch { }
            if (killer == null) return;                      // a guard or another monster: no news
            R.Why why;
            lock (s_Lock) why = s_Limit.Decide(ACE.Common.Time.GetUnixTime(), key, c.Guid.Full, s_Data.CooldownSeconds, s_Data.PerMinute);
            if (why != R.Why.Say)
            {
                if (why != R.Why.SameInstance) Mod.Log.Info($"[RevivalGuard] BossBroadcast: {name} ({c.WeenieClassId}) by {killer.Name}: quiet, {why}");
                return;
            }
            var line = R.Line(name, killer.Name, killer.Fellowship?.FellowshipName);
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(line, ChatMessageType.WorldBroadcast));
            Mod.Log.Info($"[RevivalGuard] BossBroadcast: {line} ({c.WeenieClassId} 0x{c.Guid.Full:X8})");
        }

        internal static void Err(string where, Exception e)
        {
            if (Interlocked.Increment(ref s_Errors) <= 5 || s_Errors % 1000 == 0)
                Mod.Log.Error($"[RevivalGuard] BossBroadcast {where} threw (error {s_Errors})", e);
        }

        static void Reply(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)).Trim() : "";
            double now = ACE.Common.Time.GetUnixTime();
            switch (verb)
            {
                case "on":
                case "off":
                    PropertyManager.ModifyBool(P_ON, verb == "on");
                    Reply(session, $"[BossBroadcast] {verb}: boss kill news is {(On ? "ON" : "off")}");
                    return;
                case "reload":
                    Load();
                    Reply(session, $"[BossBroadcast] reloaded: {s_LoadNote}");
                    return;
                case "test":
                {
                    uint wcid = 0; Creature sel = null;
                    if (!uint.TryParse(rest, out wcid) && session?.Player?.RequestedAppraisalTarget is uint g)
                        sel = session.Player.CurrentLandblock?.GetObject(g) as Creature;
                    if (sel != null) wcid = sel.WeenieClassId;
                    if (wcid == 0) { Reply(session, "[BossBroadcast] test <wcid>, or select a creature"); return; }
                    R.Why why; string name;
                    lock (s_Lock) why = R.Listed(s_Data, s_Over, wcid, out name);
                    name ??= sel?.Name ?? DatabaseName(wcid) ?? $"wcid {wcid}";
                    var me = session?.Player;
                    var line = R.Line(name, me?.Name ?? "Someone", me?.Fellowship?.FellowshipName);
                    double left; lock (s_Lock) left = s_Limit.SecondsLeft(now, wcid, s_Data.CooldownSeconds);
                    Reply(session, $"[BossBroadcast] {wcid}: {why}{(left > 0 ? $", cooling down {left:0} s" : "")}. It would say (to you only): {line}");
                    return;
                }
                case "list":
                {
                    System.Collections.Generic.SortedDictionary<uint, string> all;
                    lock (s_Lock) all = R.Effective(s_Data, s_Over);
                    var hits = all.Select(kv => $"{kv.Key} {kv.Value ?? DatabaseName(kv.Key) ?? "?"}")
                        .Where(t => rest.Length == 0 || t.IndexOf(rest, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    Reply(session, $"[BossBroadcast] {hits.Count} of {all.Count}{(rest.Length > 0 ? $" matching \"{rest}\"" : "")}: {string.Join(", ", hits.Take(60))}{(hits.Count > 60 ? ", ..." : "")}");
                    return;
                }
                case "add":
                case "remove":
                {
                    var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !uint.TryParse(parts[0], out var wcid)) { Reply(session, $"[BossBroadcast] {verb} <wcid>{(verb == "add" ? " [name to say]" : "")}"); return; }
                    string err;
                    lock (s_Lock)
                    {
                        if (verb == "add") { s_Over.Deny.Remove(wcid); s_Over.Allow[wcid] = parts.Length > 1 ? parts[1] : ""; }
                        else { s_Over.Allow.Remove(wcid); if (!s_Over.Deny.Contains(wcid)) s_Over.Deny.Add(wcid); }
                        SaveOverrides(out err);
                    }
                    Load();
                    Reply(session, err != null ? $"[BossBroadcast] {verb} {wcid} applied but {OVERRIDES} not saved: {err}"
                        : $"[BossBroadcast] {(verb == "add" ? "added" : "removed")} {wcid} ({DatabaseName(wcid) ?? "?"}); {s_LoadNote}");
                    Mod.Log.Info($"[RevivalGuard] BossBroadcast: {session?.Player?.Name ?? "console"} {verb} {wcid} {(parts.Length > 1 ? parts[1] : "")}");
                    return;
                }
                default:
                    lock (s_Lock)
                        Reply(session, $"[BossBroadcast] {(On ? "ON" : "off")} ({P_ON}); {s_LoadNote}; said {s_Limit.Said}, held by cooldown {s_Limit.Cooled}, by the cap {s_Limit.Capped}");
                    return;
            }
        }

        static string DatabaseName(uint wcid)
        {
            try { return ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid)?.GetName(); }
            catch { return null; }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class BossBroadcastDeath
    {
        static bool Prepare() { BossBroadcast.Register(); return true; }
        // a prefix: OnDeath resets the damage history on its way out (RevivalTrialKill's note)
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            try { BossBroadcast.OnDeath(__instance, lastDamager); } catch (Exception e) { BossBroadcast.Err("death", e); }
        }
    }
}
