using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DARKTIDE TROPHIES (owner 2026-11-21, "yes all 1-5 ... the pk currency"; docs/findings/pk-server-plan-2026-11-21.md
    /// move 2). Off until `pk_trophies_enabled` is true. An honourable kill, judged by the renown ladder's own rules
    /// (PkCommon.Judge: a victim of level 15 or more, not far beneath the killer, not an ally, not the same account or
    /// connection, no mercenary) and the FIRST of that victim by that killer inside pk_honor_repeat_hours, puts one
    /// Darktide Trophy (908400, tools/gpubox-ace/pk-trophies.sql) in the killer's pack. A full pack keeps it owed to
    /// the next login. Trophies are bonded: no trading, so a second account cannot buy them for a farmer.
    /// They are the currency of the Bloodprice Trader in Arwic (ACE alternate currency, DID 57).
    ///
    /// THE COMBAT ARENA WRIT (908403, the Bloodprice Trader's stock): using it attunes a level 150+ character to the
    /// Derethian Combat Arena (quest DCAAccess, docs/COMBAT-ARENA.md) as the Arena Master does for 4 MMD, with the
    /// Master's own lines. Handled here so no retail weenie (the Master) changes.
    /// </summary>
    internal static class PkTrophies
    {
        internal const string P_ON = "pk_trophies_enabled", P_WCID = "pk_trophy_wcid";
        internal const uint TROPHY = 908400, WRIT = 908403;
        const string ARENA_QUEST = "DCAAccess";
        const int ARENA_LEVEL = 150;

        internal static bool On => PkCommon.On(P_ON);

        static readonly object s_Lock = new object();
        static Dictionary<uint, int> s_Owed = new Dictionary<uint, int>();
        static string s_Path;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "PK expansion: an honourable kill gives the killer a Darktide Trophy, the currency of the Darktide vendors (RevivalGuard PkTrophies)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_WCID)) l[P_WCID] = new Property<long>(TROPHY, "PK expansion: the weenie an honourable kill gives (PkTrophies)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkTrophies: {P_WCID} not registered ({e.Message}); read with its default"); }
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir != null)
                {
                    s_Path = Path.Combine(dir, "trophies-owed.json");
                    if (File.Exists(s_Path)) s_Owed = JsonSerializer.Deserialize<Dictionary<uint, int>>(File.ReadAllText(s_Path)) ?? new Dictionary<uint, int>();
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkTrophies: cannot read {s_Path}: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] PkTrophies: {(On ? "ON" : "off")} ({P_ON}), trophy {Wcid()}, {s_Owed.Count} owed");
        }

        static uint Wcid() => (uint)PkCommon.Long(P_WCID, TROPHY);

        /// <summary>From PkRenown's PK-death hook, after the judgement. `prior` = this killer's kills of this victim
        /// inside the repeat window before this one.</summary>
        internal static void OnKill(Player killer, Player victim, PkCommon.Judgement j, int prior)
        {
            if (j.Verdict != PkCommon.Verdict.Honourable) return;
            if (prior > 0) { Mod.Log.Info($"[RevivalGuard] PkTrophies: no trophy for {killer.Name}: killed {victim.Name} {(prior == 1 ? "once" : prior + " times")} already"); return; }
            if (Give(killer, 1, $"You take a Darktide Trophy from {victim.Name}."))
                Mod.Log.Info($"[RevivalGuard] PkTrophies: {killer.Name} (L{killer.Level}) took a trophy from {victim.Name} (L{victim.Level})");
        }

        static bool Give(Player p, int n, string line)
        {
            int given = 0;
            try
            {
                for (; given < n; given++)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(Wcid());
                    if (wo == null) { Mod.Log.Error($"[RevivalGuard] PkTrophies: trophy weenie {Wcid()} does not exist (pk-trophies.sql not applied?)"); return false; }
                    if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); break; }
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkTrophies: giving {p.Name} a trophy: {e.Message}"); }
            if (given > 0) PkCommon.Say(p, line);
            if (given < n)
            {
                lock (s_Lock) { s_Owed[p.Guid.Full] = (s_Owed.TryGetValue(p.Guid.Full, out var o) ? o : 0) + (n - given); Save(); }
                PkCommon.Say(p, "Your pack is too full for your Darktide Trophy. It will wait for you at your next login.");
                Mod.Log.Info($"[RevivalGuard] PkTrophies: {p.Name} owed {n - given} (pack full)");
            }
            return given > 0;
        }

        static void Save()
        {
            if (s_Path == null) return;
            try { var tmp = s_Path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(s_Owed)); File.Move(tmp, s_Path, true); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkTrophies: cannot write {s_Path}: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class AtLogin
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    int n;
                    lock (s_Lock)
                    {
                        if (!s_Owed.TryGetValue(__instance.Guid.Full, out n) || n <= 0) return;
                        s_Owed.Remove(__instance.Guid.Full); Save();
                    }
                    Give(__instance, n, n == 1 ? "The Darktide Trophy you were owed is in your pack." : $"The {n} Darktide Trophies you were owed are in your pack.");
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkTrophies: login of {__instance?.Name}: {e.Message}"); }
            }
        }

        /// <summary>The Combat Arena Writ. Works whether or not trophies are on: a writ someone holds keeps its use.</summary>
        [HarmonyPatch(typeof(Gem), nameof(Gem.UseGem))]
        [HarmonyPriority(Priority.First)]   // before GemNoEffectGuard, which would call a gem with no spell useless
        static class UseWrit
        {
            static bool Prefix(Gem __instance, Player player)
            {
                try
                {
                    if (__instance?.WeenieClassId != WRIT || player == null || player.IsDead) return true;
                    if ((player.Level ?? 1) < ARENA_LEVEL) { PkCommon.Say(player, "You are not yet ready for the Derethian Combat Arena."); return false; }
                    if (player.QuestManager.HasQuest(ARENA_QUEST)) { PkCommon.Say(player, "You have already been attuned with the portal's energy."); return false; }
                    if (!player.TryConsumeFromInventoryWithNetworking(__instance, 1)) return false;
                    player.QuestManager.Stamp(ARENA_QUEST);
                    PkCommon.Say(player, "You have now been attuned with the portal's energy and may enter.");
                    Mod.Log.Info($"[RevivalGuard] PkTrophies: {player.Name} used a Combat Arena Writ ({ARENA_QUEST})");
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkTrophies: writ: {e.Message}"); return false; }
            }
        }
    }
}
