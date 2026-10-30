using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// The Festival Stones goal (FestivalStonesRule has the rule and the words). Mod code only: the stones are retail's
    /// (5376-5395, ItemUseable No, so a visit is standing within RANGE_M of one), the record is three quest flags on the
    /// character, and nothing in ace_world changes. While the Anniversary runs (SeasonalCalendar, forced states
    /// included), every CHECK_S each online player who still has a doll coming is checked against the stones in their
    /// landblock. A stone not yet counted this year says "Festival Stones: N of 20."; the twentieth gives both dolls in
    /// one hand-out (all the free slots first; a doll the burden refuses stays owed), retried at any stone or at login.
    /// </summary>
    static class FestivalStones
    {
        const double CHECK_S = 2, CACHE_S = 30, RETRY_S = 60;
        static double s_Last;
        static readonly Dictionary<uint, (double at, List<WorldObject> stones)> s_ByLandblock = new Dictionary<uint, (double, List<WorldObject>)>();
        static readonly Dictionary<uint, double> s_Retried = new Dictionary<uint, double>();

        static double Now => ACE.Common.Time.GetUnixTime();

        internal static List<uint> Owed(Player p) => FestivalStonesRule.Owed(p.QuestManager.GetCurrentSolves(FestivalStonesRule.DOLLS));

        static int Seen(Player p)
        {
            var qm = p.QuestManager;
            var q = qm.GetQuest(FestivalStonesRule.SEEN);
            if (q != null && FestivalStonesRule.Stale(q.LastTimeCompleted, Now))
            {
                qm.Erase(FestivalStonesRule.SEEN);
                qm.Erase(FestivalStonesRule.COUNTER);
                Mod.Log.Info($"[RevivalGuard] FestivalStones: {p.Name}'s stones were from an earlier year; cleared");
                return 0;
            }
            return q == null ? 0 : qm.GetCurrentSolves(FestivalStonesRule.SEEN);
        }

        internal static void Tick()
        {
            double now = Now;
            if (now - s_Last < CHECK_S) return;
            s_Last = now;
            if (!SeasonalCalendar.IsFestivalActive(FestivalStonesRule.Festival)) { if (s_ByLandblock.Count > 0) s_ByLandblock.Clear(); return; }

            foreach (var p in PlayerManager.GetAllOnline())
            {
                var lb = p?.CurrentLandblock;
                if (lb == null || p.QuestManager == null || p.Location == null || Owed(p).Count == 0) continue;
                uint id = lb.Id.Raw;
                if (!s_ByLandblock.TryGetValue(id, out var c) || now - c.at > CACHE_S)
                    s_ByLandblock[id] = c = (now, lb.GetAllWorldObjectsForDiagnostics().Where(o => o != null && FestivalStonesRule.IsStone(o.WeenieClassId)).ToList());
                if (c.stones.Count == 0) continue;
                var near = c.stones.FirstOrDefault(s => s.Location != null && p.Location.DistanceTo(s.Location) <= FestivalStonesRule.RANGE_M);
                if (near == null) continue;
                uint wcid = near.WeenieClassId;
                Later(p, () => Visit(p, wcid));
            }
        }

        static void Visit(Player p, uint stone)
        {
            var qm = p.QuestManager;
            if (qm == null || Owed(p).Count == 0) return;
            int seen = Seen(p);
            if ((seen & FestivalStonesRule.Bit(stone)) == 0)
            {
                seen |= FestivalStonesRule.Bit(stone);
                Set(p, FestivalStonesRule.SEEN, seen);
                int n = FestivalStonesRule.Count(seen);
                Set(p, FestivalStonesRule.COUNTER, n);
                Mod.Log.Info($"[RevivalGuard] FestivalStones: {p.Name} at stone {stone}, {n} of {FestivalStonesRule.N}");
                if (n < FestivalStonesRule.N) { Say(p, FestivalStonesRule.VisitLine(n)); return; }
                GiveDolls(p);
                return;
            }
            // all twenty seen and a doll still owed (pack was full): try again, at most once a minute
            if (FestivalStonesRule.Count(seen) < FestivalStonesRule.N) return;
            double now = Now;
            if (s_Retried.TryGetValue(p.Guid.Full, out var at) && now - at < RETRY_S) return;
            s_Retried[p.Guid.Full] = now;
            GiveDolls(p);
        }

        /// <summary>The hand-out: room for every doll owed first (or the full line), then each doll; a doll the burden refuses stays owed.</summary>
        internal static void GiveDolls(Player p)
        {
            var owed = Owed(p);
            if (owed.Count == 0) return;
            if (p.GetFreeInventorySlots() < owed.Count) { Say(p, FestivalStonesRule.FullLine(owed.Count)); return; }
            var names = new List<string>();
            int bits = p.QuestManager.GetCurrentSolves(FestivalStonesRule.DOLLS);
            foreach (var wcid in owed)
            {
                var doll = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (doll == null) { Mod.Log.Warn($"[RevivalGuard] FestivalStones: no weenie {wcid}"); continue; }
                if (!p.TryCreateInInventoryWithNetworking(doll)) { doll.Destroy(); continue; }
                bits |= FestivalStonesRule.DollBit(wcid);
                Set(p, FestivalStonesRule.DOLLS, bits);
                names.Add(doll.Name);
            }
            if (names.Count > 0)
            {
                Say(p, FestivalStonesRule.DoneLine(names));
                Mod.Log.Info($"[RevivalGuard] FestivalStones: {p.Name} got {string.Join(", ", names)}");
            }
            if (Owed(p).Count > 0) Say(p, FestivalStonesRule.FullLine(Owed(p).Count));
        }

        /// <summary>At login: all twenty seen this year and a doll still owed.</summary>
        internal static void Login(Player p)
        {
            if (p?.QuestManager == null || Owed(p).Count == 0 || !p.QuestManager.HasQuest(FestivalStonesRule.SEEN)) return;
            if (FestivalStonesRule.Count(Seen(p)) >= FestivalStonesRule.N) Later(p, () => GiveDolls(p));
        }

        /// <summary>
        /// A flag's value, written straight into the registry. ACE's SetQuestCompletions clamps to the quest row's
        /// MaxSolves and a flag with no row reads MaxSolves 0, so every value came back 0 (box 2026-10-30: each stone
        /// said "1 of 20"). These flags have no row on purpose (no world data), so the registry entry is set directly.
        /// </summary>
        static void Set(Player p, string flag, int value)
        {
            var qm = p.QuestManager;
            if (!qm.HasQuest(flag)) qm.Stamp(flag);                 // creates the entry (solves 1, now)
            var q = qm.GetQuest(flag);
            if (q == null) return;
            q.NumTimesCompleted = value;
            q.LastTimeCompleted = (uint)Now;
            p.CharacterChangesDetected = true;
        }

        static void Later(WorldObject o, Action a)
        {
            var chain = new ActionChain();
            chain.AddAction(o, () => { try { a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalStones: {e.Message}"); } });
            chain.EnqueueChain();
        }

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class FestivalStonesTick
    {
        static void Postfix()
        {
            try { FestivalStones.Tick(); }
            catch (Exception e) { TickGuard.Report("FestivalStones", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class FestivalStonesLogin
    {
        static void Postfix(Player __instance)
        {
            try { FestivalStones.Login(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalStones login: {e.Message}"); }
        }
    }
}
