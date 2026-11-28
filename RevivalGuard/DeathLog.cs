using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>/deaths: your last 10 DEATHS.</summary>
    public static class DeathLog
    {
        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, List<DeathLogRule.Entry>> s_Cache = new Dictionary<uint, List<DeathLogRule.Entry>>();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = false };
        static string s_Dir;
        static List<(string, float, float)> s_Towns = new List<(string, float, float)>();
        /// <summary>The towns DeathPlaces.json names (ChessLobby names its boards by them).</summary>
        internal static IList<(string name, float ns, float ew)> Towns => s_Towns;
        static Dictionary<string, string> s_Dungeons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static AccessTools.FieldRef<Player, bool> s_Suicide;
        [ThreadStatic] internal static bool t_Falling;

        public static void Register()
        {
            s_Dir = ModFolder.Sub("deaths");
            LoadPlaces();
            try { s_Suicide = AccessTools.FieldRefAccess<Player, bool>("suicideInProgress"); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DeathLog: no Player.suicideInProgress ({e.Message}); a /die reads as your own magic"); }
            CommandManager.TryAddCommand(Handle, "deaths", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Your last 10 deaths: who or what killed you, where, and when.", "");
            Mod.Log.Info($"[RevivalGuard] DeathLog: on, {s_Towns.Count} towns, {s_Dungeons.Count} dungeon names, files in {s_Dir ?? "(none: not recording)"}; /deaths");
        }

        static void LoadPlaces()
        {
            try
            {
                using var rs = typeof(DeathLog).Assembly.GetManifestResourceStream("RevivalGuard.DeathPlaces.json");
                if (rs == null) { Mod.Log.Warn("[RevivalGuard] DeathLog: DeathPlaces.json not embedded; coordinates only"); return; }
                using var doc = JsonDocument.Parse(rs);
                foreach (var t in doc.RootElement.GetProperty("towns").EnumerateArray())
                    s_Towns.Add((t[0].GetString(), (float)t[1].GetDouble(), (float)t[2].GetDouble()));
                foreach (var d in doc.RootElement.GetProperty("dungeons").EnumerateObject())
                    s_Dungeons[d.Name] = d.Value.GetString();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DeathLog: place names not read ({e.Message}); coordinates only"); }
        }

        static string FileOf(uint guid) => s_Dir == null ? null : Path.Combine(s_Dir, guid.ToString("X8") + ".json");

        /// <summary>The character's list, read once and cached.</summary>
        static List<DeathLogRule.Entry> Get(uint guid)
        {
            if (s_Cache.TryGetValue(guid, out var l)) return l;
            l = new List<DeathLogRule.Entry>();
            var f = FileOf(guid);
            try { if (f != null && File.Exists(f)) l = JsonSerializer.Deserialize<List<DeathLogRule.Entry>>(File.ReadAllText(f), s_Json) ?? l; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DeathLog: cannot read {f}: {e.Message}"); }
            s_Cache[guid] = l;
            return l;
        }

        static void Save(uint guid, List<DeathLogRule.Entry> l)
        {
            var f = FileOf(guid);
            if (f == null) return;
            try
            {
                var tmp = f + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(l, s_Json));
                File.Move(tmp, f, true);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DeathLog: cannot write {f}: {e.Message}"); }
        }

        /// <summary>The place words for where the player stands now ("at 42.1N, 33.6E near Holtburg"), or null.</summary>
        internal static string WhereOf(Player p)
        {
            try
            {
                var loc = p.Location;
                uint cell = loc?.Cell ?? 0;
                bool dungeon = p.CurrentLandblock?.IsDungeon ?? false;
                s_Dungeons.TryGetValue((cell >> 16).ToString("X4"), out var dname);
                return DeathLogRule.Where(cell, loc?.PositionX ?? 0, loc?.PositionY ?? 0, dungeon, dname, s_Towns);
            }
            catch (Exception) { return null; }
        }

        internal static void Record(Player p, DamageHistoryInfo last, DamageHistoryInfo top)
        {
            if (p == null || Mercenaries.IsMerc(p)) return;
            bool suicide = false;
            try { suicide = s_Suicide != null && s_Suicide(p); } catch (Exception) { }

            string owner = null;
            try { if (last?.PetOwner != null && last.PetOwner.TryGetTarget(out var o)) owner = o?.Name; } catch (Exception) { }
            var cause = DeathLogRule.Classify(last != null, last?.Guid.Full ?? 0, p.Guid.Full, last?.IsPlayer ?? false, owner, t_Falling, suicide);

            bool pk = false;
            try
            {
                pk = cause == DeathLogRule.Cause.Player
                     && (p.IsPKDeath(top) || p.IsPKLiteDeath(top) || p.IsPKDeath(last) || p.IsPKLiteDeath(last));
            }
            catch (Exception) { }

            string where = WhereOf(p);

            var e = new DeathLogRule.Entry
            {
                Utc = DateTime.UtcNow, Cause = cause, Pk = pk, Where = where,
                Killer = cause == DeathLogRule.Cause.Player || cause == DeathLogRule.Cause.Monster || cause == DeathLogRule.Cause.Pet ? last?.Name : null,
                Owner = cause == DeathLogRule.Cause.Pet ? owner : null,
            };
            lock (s_Lock)
            {
                var l = Get(p.Guid.Full);
                if (DeathLogRule.Add(l, e)) Save(p.Guid.Full, l);
            }
        }

        static void Say(Session s, string text) => s?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static string Clock(DateTime utc) => DeathLogRule.Clock(PkNight.PacificLocal(utc), PkNight.PacificOffsetHours(utc));

        static void Handle(Session session, params string[] parameters)
        {
            var me = session?.Player;
            if (me == null) return;
            string name = string.Join(" ", parameters ?? Array.Empty<string>()).Trim();
            uint guid = me.Guid.Full; string who = null;
            if (name.Length > 0 && !string.Equals(name.TrimStart('+'), me.Name.TrimStart('+'), StringComparison.OrdinalIgnoreCase))
            {
                if (session.AccessLevel < AccessLevel.Developer) { Say(session, "You can only look up your own deaths: /deaths"); return; }
                var target = PlayerManager.FindByName(name) ?? PlayerManager.FindByName("+" + name.TrimStart('+'));
                if (target == null) { Say(session, $"There is no character named {name}."); return; }
                guid = target.Guid.Full; who = target.Name;
            }
            List<DeathLogRule.Entry> copy;
            lock (s_Lock) copy = new List<DeathLogRule.Entry>(Get(guid));
            foreach (var line in DeathLogRule.Lines(copy, who, DateTime.UtcNow, Clock)) Say(session, line);
        }

        /// <summary>Every player death: Player.Die, declared on Player.</summary>
        [HarmonyPatch]
        static class OnDie
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(Player), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) });
                if (m == null) Mod.Log.Error("[RevivalGuard] DeathLog: Player.Die(DamageHistoryInfo, DamageHistoryInfo) not found; /deaths records nothing");
                return m;
            }
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Prefix(Player __instance, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
                try { Record(__instance, lastDamager, topDamager); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DeathLog: record: {e.Message}"); }
            }
        }

        /// <summary>A fall: ACE's falling damage calls OnDeath and Die from inside TakeDamage_Falling</summary>
        [HarmonyPatch]
        static class Falling
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(Player), "TakeDamage_Falling", new[] { typeof(float) });
                if (m == null) Mod.Log.Warn("[RevivalGuard] DeathLog: Player.TakeDamage_Falling(float) not found; a fall reads as your own magic");
                return m;
            }
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Prefix() => t_Falling = true;
            static Exception Finalizer(Exception __exception) { t_Falling = false; return __exception; }
        }
    }
}
