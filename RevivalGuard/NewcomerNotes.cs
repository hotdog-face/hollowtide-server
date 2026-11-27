using System;
using System.Collections.Generic;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NOTES FOR NEW PLAYERS (owner 2026-11-21). Ours, not retail. Rules and every line: NewcomerNotesRule.cs (nettest).
    ///   * The first gank: a character under 30 killed by another player for the first time gets one note (a notice at
    ///     the top of the screen plus chat lines) once it is out of portal space at its lifestone.
    ///   * The swap coins: at level 30, a character that has not collected its Imbue Swap Coins is told where they are.
    ///   * Luminance (owner 2026-11-27): at level 200, or at the first login past it, a character without luminance is told
    ///     where it starts (Nalicana's Test).
    /// Each once per character (a quest stamp). Switch rg_newcomer_notes (default on); off, nothing is said or stamped.
    ///
    /// Hooks: Player.HandlePKDeathBroadcast (ACE's PK-death step, still at the death spot; PkRenown uses it too) marks the
    /// victim pending; Corpse.RecalculateDecayTime(player) (called after the death items are on the corpse) records the
    /// corpse's real timer and item count; the note waits until the player is no longer teleporting.
    /// </summary>
    public static class NewcomerNotes
    {
        internal const string P_ON = "rg_newcomer_notes";
        const double FIRST_CHECK_S = 4, POLL_S = 2;
        const int POLLS = 45;   // 90 s of portal space at most, then the note goes anyway

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return false; } } }

        sealed class Pending { public string Killer, Where; public bool Wanted, Outdoors; public double CorpseS = -1; public int Items = -1; }
        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, Pending> s_Pending = new Dictionary<uint, Pending>();

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard NewcomerNotes: the first-gank note to characters under 30, the Imbue Swap Coin notice at 30 and the luminance notice at 200 (false: say nothing)");
            Mod.Log.Info($"[RevivalGuard] NewcomerNotes: {(On ? "ON" : "off")} ({P_ON}); first gank under {NewcomerNotesRule.GANK_UNDER}, swap coins at {NewcomerNotesRule.SWAP_LEVEL}");
        }

        // ------------------------------------------------------------------ the first gank

        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast))]
        static class OnPkDeath
        {
            static void Postfix(Player __instance, DamageHistoryInfo topDamager)
            {
                try
                {
                    if (!On) return;
                    var p = __instance;
                    if (p?.Session == null || topDamager == null || !topDamager.IsPlayer || !p.IsPKDeath(topDamager)) return;
                    var killer = topDamager.TryGetAttacker() as Player;
                    if (killer == null || Mercenaries.IsMerc(killer)) return;
                    if (!NewcomerNotesRule.GankNoteDue(p.Level ?? 1, p.QuestManager.HasQuest(NewcomerNotesRule.GANK_FLAG),
                            Recruiting.BotOrStaff(p) || Mercenaries.IsMerc(p), killer == p)) return;
                    p.QuestManager.Stamp(NewcomerNotesRule.GANK_FLAG);   // now: a second death in portal space is not a second note
                    var pend = new Pending
                    {
                        Killer = killer.Name,
                        Where = DeathLog.WhereOf(p),
                        Outdoors = ((p.Location?.Cell ?? 0) & 0xFFFFu) < 0x100u,
                        Wanted = PkRenown.On && PkRenown.IsWanted(killer.Guid.Full),
                    };
                    lock (s_Lock) s_Pending[p.Guid.Full] = pend;
                    Mod.Log.Info($"[RevivalGuard] NewcomerNotes: first gank of {p.Name} (L{p.Level}) by {killer.Name}; note after the lifestone");
                    Wait(p, 0);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes: death of {__instance?.Name}: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.RecalculateDecayTime))]
        static class CorpseTimer
        {
            [HarmonyPriority(Priority.Last)]   // after MansionPk's own timer
            static void Postfix(Corpse __instance, Player player)
            {
                try
                {
                    if (player == null) return;
                    lock (s_Lock)
                        if (s_Pending.TryGetValue(player.Guid.Full, out var pend))
                        { pend.CorpseS = __instance.TimeToRot ?? -1; pend.Items = __instance.Inventory?.Count ?? -1; }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes: corpse timer: {e.Message}"); }
            }
        }

        static void Wait(Player p, int polls)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(polls == 0 ? FIRST_CHECK_S : POLL_S);
            chain.AddAction(p, () =>
            {
                try
                {
                    if (PlayerManager.GetOnlinePlayer(p.Guid) != p) { lock (s_Lock) s_Pending.Remove(p.Guid.Full); return; }
                    if ((p.IsDead || p.Teleporting) && polls < POLLS) { Wait(p, polls + 1); return; }
                    Tell(p);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes: note to {p.Name}: {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        static void Tell(Player p)
        {
            Pending pend;
            lock (s_Lock) { if (!s_Pending.TryGetValue(p.Guid.Full, out pend)) return; s_Pending.Remove(p.Guid.Full); }
            var now = DateTime.UtcNow;
            var f = new NewcomerNotesRule.Facts
            {
                Killer = pend.Killer, Where = pend.Where, Outdoors = pend.Outdoors, CorpseSeconds = pend.CorpseS, CorpseItems = pend.Items,
                KillerWanted = pend.Wanted, InAllegiance = p.Allegiance != null && p.Allegiance.TotalMembers > 1,
                FestivalSeason = NewcomerNotesRule.FestivalSeason(PkNight.PacificLocal(now)),
            };
            try
            {
                var v = p.EnchantmentManager.GetVitae();
                if (v != null && v.StatModValue < 1f) f.VitaePct = Math.Round((1.0 - v.StatModValue) * 100.0, 1);
                f.VitaeStepPct = PkCommon.Dbl("vitae_penalty", 0.05) * 100.0;
                f.VitaeMaxPct = Math.Round((1.0 - p.EnchantmentManager.GetMinVitae((uint)(p.Level ?? 1))) * 100.0);
            }
            catch (Exception) { f.VitaePct = -1; }
            try { f.RecruitingListings = Recruiting.OpenListingsFor(p); } catch (Exception) { f.RecruitingListings = -1; }
            try
            {
                if (PkNight.On)
                {
                    var s = PkNight.Next(now);
                    if (s != null)
                    {
                        f.PkNightStartUtc = s.StartUtc;
                        f.PkNightLocal = PkNight.PacificLocal(s.StartUtc);
                        f.PkNightRunningNow = s.Running(now);
                    }
                }
            }
            catch (Exception) { f.PkNightStartUtc = null; }

            p.Session?.Network.EnqueueSend(new GameEventCommunicationTransientString(p.Session, NewcomerNotesRule.NOTICE));
            foreach (var line in NewcomerNotesRule.GankLines(f)) PkCommon.Say(p, line);
            Telemetry.Emit("first_gank_note", p, $",\"vitae\":{(f.VitaePct < 0 ? 0 : f.VitaePct)},\"recruit\":{f.RecruitingListings}");
            Mod.Log.Info($"[RevivalGuard] NewcomerNotes: first-gank note to {p.Name} (corpse {pend.CorpseS:0}s, {pend.Items} items, vitae {f.VitaePct}, recruit {f.RecruitingListings})");
        }

        // ------------------------------------------------------------------ the swap coins at level 30

        [HarmonyPatch(typeof(Player), "CheckForLevelup")]
        static class SwapCoinsAtLevel
        {
            static void Prefix(Player __instance, out int __state) => __state = __instance.Level ?? 1;
            static void Postfix(Player __instance, int __state)
            {
                try { SwapCheck(__instance, __state); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes level-up: {e.Message}"); }
                try { LuminanceCheck(__instance, 10); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes luminance: {e.Message}"); }
            }
        }

        static void SwapCheck(Player p, int before)
        {
            if (!On || p?.Session == null) return;
            if (!NewcomerNotesRule.SwapNoticeDue(before, p.Level ?? 1, p.QuestManager.HasQuest(NewcomerNotesRule.SWAP_FLAG),
                    p.QuestManager.HasQuest(NewcomerNotesRule.SWAP_GIFT_QUEST), Recruiting.BotOrStaff(p) || Mercenaries.IsMerc(p))) return;
            p.QuestManager.Stamp(NewcomerNotesRule.SWAP_FLAG);
            SayLater(p, 8, NewcomerNotesRule.SwapLine());   // after the level-up lines and the recruit offer
            Mod.Log.Info($"[RevivalGuard] NewcomerNotes: swap-coin notice to {p.Name} at level {p.Level}");
        }

        // ------------------------------------------------------------------ luminance at 200

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class LuminanceAtLogin
        {
            static void Postfix(Player __instance)
            {
                try { LuminanceCheck(__instance, 12); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes luminance login: {e.Message}"); }
            }
        }

        static void LuminanceCheck(Player p, double delayS)
        {
            if (!On || p?.Session == null) return;
            bool open = p.QuestManager.HasQuest(NewcomerNotesRule.LUM_OPEN_QUEST) || (p.MaximumLuminance ?? 0) > 0;
            if (!NewcomerNotesRule.LuminanceNoticeDue(p.Level ?? 1, p.QuestManager.HasQuest(NewcomerNotesRule.LUM_FLAG), open,
                    Recruiting.BotOrStaff(p) || Mercenaries.IsMerc(p))) return;
            p.QuestManager.Stamp(NewcomerNotesRule.LUM_FLAG);
            SayLater(p, delayS, NewcomerNotesRule.LuminanceLine());
            Mod.Log.Info($"[RevivalGuard] NewcomerNotes: luminance notice to {p.Name} at level {p.Level}");
        }

        static void SayLater(Player p, double delayS, string line)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delayS);
            chain.AddAction(p, () =>
            {
                try { if (PlayerManager.GetOnlinePlayer(p.Guid) == p) PkCommon.Say(p, line); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerNotes line: {e.Message}"); }
            });
            chain.EnqueueChain();
        }
    }
}
