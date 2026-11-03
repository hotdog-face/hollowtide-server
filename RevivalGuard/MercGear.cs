using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A MERCENARY WEARS WHAT YOU GIVE IT, IF IT IS BETTER (owner, 2026-10-13: "if i hand items to mercenaries
    /// they equip the best gear they are given"). One postfix on Player.GiveObjectToPlayer: when the receiver is
    /// a Hollow Company sellsword (Mercenaries.IsMerc), the sellsword looks over its packs a moment later and
    /// puts on one upgrade at a time, through the same server actions a player's own drag runs
    /// (PutItemInContainer to take the old piece off, GetAndWieldItem to put the new one on), so every wield
    /// requirement and slot rule ACE has still applies.
    ///
    /// What counts as better, kept simple and visible:
    ///   * armor and clothing: more armor level than every worn piece it would replace, and covering at least
    ///     what they covered (a coat never strips a breastplate and sleeves for less); an empty slot always.
    ///   * shield: more armor level, or none worn, and only beside a one-handed melee weapon.
    ///   * melee weapon: more average damage, in a weapon skill the sellsword has trained; empty hands take
    ///     any trained melee weapon. Bows and casters only replace their own kind.
    ///   * jewelry and the rest: an empty slot only (no fair measure between two rings).
    /// Things it cannot wield (level, skill, attunement to someone else) are simply carried.
    ///
    /// GIVING DIDN'T WORK (owner tickets 261103-0015, with Brannoc's own 0013/0014: "Unable to put Acid Cestus into
    /// container" and "You must remove your Acid Cestus to wield Frost Katar", 80 tries in a minute on live). The old
    /// piece was always put away into the MAIN pack; Brannoc's was full (the gift itself landed in a side pack), so
    /// UnwieldToFullPack refused it, the wield was refused, and the next step picked the same swap again. Five gives
    /// ran five such chains side by side. Now:
    ///   * one run per merc at a time; a give during a run joins it;
    ///   * the old piece goes into whichever pack has room (main pack first, then the side packs);
    ///   * each piece is tried once per run: a wield ACE refuses is never asked again;
    ///   * at the end, gear it was given and is not wearing goes back to the giver, with a line saying why (no room to
    ///     put the old piece away, can't wield it, or already wears better). Anything that is not gear (kits, food,
    ///     notes) it keeps, as before. A giver out of reach or with a full pack is told and the merc carries it.
    [HarmonyPatch]
    static class MercGear
    {
        const int MAX_STEPS = 16;           // upgrades per run, so a bad rule can never loop
        const float RETURN_RANGE = 50f;     // a giver further than this is told, not handed the gift
        static MethodInfo s_WieldReq, s_GiveDirect;

        /// <summary>One merc's gear pass: the gifts it is looking over and what it already tried.</summary>
        sealed class Run
        {
            public readonly List<(uint item, uint giver)> Gifts = new List<(uint, uint)>();
            public readonly HashSet<uint> Tried = new HashSet<uint>();
            public readonly HashSet<uint> Worn = new HashSet<uint>();      // gifts seen on the merc during the run
            public readonly Dictionary<uint, string> NoRoom = new Dictionary<uint, string>();   // piece -> what would not fit
        }
        static readonly Dictionary<uint, Run> s_Runs = new Dictionary<uint, Run>();
        static readonly object s_Lock = new object();

        static MethodBase TargetMethod()
        {
            s_WieldReq = AccessTools.Method(typeof(Player), "CheckWieldRequirements", new[] { typeof(WorldObject) });
            // our ACE's give without the walk-to (headless agents); the stock request is the fallback
            s_GiveDirect = AccessTools.Method(typeof(Player), "AgentGiveDirect", new[] { typeof(uint), typeof(uint), typeof(int) });
            var m = AccessTools.Method(typeof(Player), "GiveObjectToPlayer");
            Mod.Log.Info($"[RevivalGuard] MercGear: {(m != null ? "on" : "OFF (Player.GiveObjectToPlayer not found)")}");
            return m;
        }

        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(Player __instance, Player target, WorldObject item)
        {
            try
            {
                if (target == null || !Mercenaries.IsMerc(target)) return;
                bool start = false;
                lock (s_Lock)
                {
                    if (!s_Runs.TryGetValue(target.Guid.Full, out var run))
                    {
                        s_Runs[target.Guid.Full] = run = new Run();
                        start = true;
                    }
                    if (item != null && (item.MaxStackSize ?? 1) <= 1 && __instance != null)
                        run.Gifts.Add((item.Guid.Full, __instance.Guid.Full));
                }
                if (start) Later(target, 1.5, MAX_STEPS);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercGear: {e.GetType().Name}: {e.Message}"); }
        }

        static void Later(Player merc, double seconds, int stepsLeft)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(seconds);
            chain.AddAction(merc, () => Step(merc, stepsLeft));
            chain.EnqueueChain();
        }

        /// <summary>Find the best single upgrade not yet tried, put away what it replaces, put it on, then look again.</summary>
        static void Step(Player merc, int stepsLeft)
        {
            Run run;
            lock (s_Lock) s_Runs.TryGetValue(merc.Guid.Full, out run);
            if (run == null) return;
            try
            {
                lock (s_Lock)
                    foreach (var (gift, _) in run.Gifts)
                        if (merc.EquippedObjects.ContainsKey(new ObjectGuid(gift))) run.Worn.Add(gift);
                if (stepsLeft <= 0 || merc.CurrentLandblock == null || merc.IsDead || merc.Teleporting
                    || !Pick(merc, run.Tried, out var item, out var loc, out var off))
                {
                    Finish(merc, run);
                    return;
                }
                run.Tried.Add(item.Guid.Full);

                var homes = Homes(merc, off);
                if (homes == null)
                {
                    var what = string.Join(" and ", off.Select(o => o.Name));
                    run.NoRoom[item.Guid.Full] = what;
                    Mod.Log.Info($"[RevivalGuard] MercGear: {merc.Name} has no room to put away {what} for {item.Name} 0x{item.Guid.Full:X8}");
                    Later(merc, 0.1, stepsLeft - 1);       // another piece may need no room (a ring, an empty slot)
                    return;
                }
                for (int i = 0; i < off.Count; i++) merc.HandleActionPutItemInContainer(off[i].Guid.Full, homes[i].Guid.Full);
                var guid = item.Guid.Full;
                var chain = new ActionChain();
                chain.AddDelaySeconds(off.Count > 0 ? 0.75 : 0.1);
                chain.AddAction(merc, () =>
                {
                    merc.HandleActionGetAndWieldItem(guid, loc);
                    Mod.Log.Info($"[RevivalGuard] MercGear: {merc.Name} puts on {item.Name} 0x{guid:X8} ({loc})"
                                 + (off.Count > 0 ? $" for {string.Join(", ", off.Select(o => o.Name))}" : ""));
                });
                chain.EnqueueChain();
                Later(merc, 2.0, stepsLeft - 1);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] MercGear: {merc?.Name}: {e.GetType().Name}: {e.Message}");
                lock (s_Lock) s_Runs.Remove(merc.Guid.Full);
            }
        }

        /// <summary>A pack with room for each piece coming off (main pack first, then side packs), or null: the same
        /// capacity test UnwieldToFullPack makes, counting what this swap already sends to each pack.</summary>
        static List<Container> Homes(Player merc, List<WorldObject> off)
        {
            var packs = new List<Container> { merc };
            packs.AddRange(merc.Inventory.Values.OfType<Container>());
            var used = new Dictionary<Container, int>();
            var homes = new List<Container>();
            foreach (var o in off)
            {
                var home = packs.FirstOrDefault(c =>
                {
                    used.TryGetValue(c, out var n);
                    if (n == 0) return UnwieldToFullPack.Fits(c, o);
                    var cap = o.UseBackpackSlot ? (c.ContainerCapacity ?? 0) : (c.ItemCapacity ?? 0);
                    var have = c.Inventory.Values.Count(i => i.UseBackpackSlot == o.UseBackpackSlot);
                    return cap > have + n;
                });
                if (home == null) return null;
                used[home] = used.TryGetValue(home, out var k) ? k + 1 : 1;
                homes.Add(home);
            }
            return homes;
        }

        /// <summary>The run is over: gear given and not worn goes back to whoever gave it, with the reason.</summary>
        static void Finish(Player merc, Run run)
        {
            List<(uint item, uint giver)> gifts;
            lock (s_Lock) { s_Runs.Remove(merc.Guid.Full); gifts = run.Gifts.ToList(); }
            if (merc.CurrentLandblock == null) return;      // logged out or in portal space: it carries them
            foreach (var (gift, giverGuid) in gifts)
            {
                try
                {
                    var wo = merc.GetInventoryItem(gift);         // null: worn now, or the give never landed
                    if (wo == null || (wo.ValidLocations ?? 0) == 0) continue;   // not gear: kept, as before
                    string why = run.NoRoom.TryGetValue(gift, out var what) ? $"no room left in the packs to put away the {what}"
                               : run.Worn.Contains(gift) ? "it already wears something better"
                               : run.Tried.Contains(gift) || !CanWield(merc, wo) ? "it can't wield that"
                               : "it already wears something better";
                    var giver = PlayerManager.GetOnlinePlayer(giverGuid);
                    if (giver?.Session == null || giver.Location == null || merc.Location == null
                        || merc.Location.DistanceTo(giver.Location) > RETURN_RANGE)
                    {
                        Mod.Log.Info($"[RevivalGuard] MercGear: {merc.Name} keeps {wo.Name} 0x{gift:X8} ({why}); giver gone or out of reach");
                        continue;
                    }
                    if (!giver.CanAddToInventory(wo))
                    {
                        Tell(giver, $"{merc.Name} can't use the {wo.Name} ({why}) and carries it for you: your pack is full.");
                        Mod.Log.Info($"[RevivalGuard] MercGear: {merc.Name} keeps {wo.Name} 0x{gift:X8} ({why}); {giver.Name}'s pack is full");
                        continue;
                    }
                    Tell(giver, $"{merc.Name} hands the {wo.Name} back: {why}.");
                    Mod.Log.Info($"[RevivalGuard] MercGear: {merc.Name} hands {wo.Name} 0x{gift:X8} back to {giver.Name} ({why})");
                    if (s_GiveDirect != null) s_GiveDirect.Invoke(merc, new object[] { giver.Guid.Full, gift, 1 });
                    else merc.HandleActionGiveObjectRequest(giver.Guid.Full, gift, 1);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercGear: {merc.Name} return 0x{gift:X8}: {e.GetType().Name}: {e.Message}"); }
            }
        }

        static void Tell(Player p, string text) =>
            p.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static bool CanWield(Player merc, WorldObject item)
        {
            if (s_WieldReq == null) return true;   // ACE refuses it anyway if not
            try { return (WeenieError)s_WieldReq.Invoke(merc, new object[] { item }) == WeenieError.None; }
            catch { return false; }
        }

        static bool Trained(Player merc, WorldObject w)
        {
            var s = merc.ConvertToMoASkill(w.WeaponSkill);
            if (s == Skill.None) s = w.WeaponSkill;
            var cs = merc.GetCreatureSkill(s, false);
            return cs != null && cs.AdvancementClass >= SkillAdvancementClass.Trained;
        }

        static double MeleeScore(WorldObject w) =>
            (w.GetProperty(PropertyInt.Damage) ?? 0) * (1.0 - (w.DamageVariance ?? 0) / 2.0);

        static int AL(WorldObject w) => w.ArmorLevel ?? 0;

        static bool Pick(Player merc, HashSet<uint> tried, out WorldObject pick, out EquipMask loc, out List<WorldObject> off)
        {
            pick = null; loc = 0; off = new List<WorldObject>();
            var carried = merc.GetAllPossessions()
                .Where(i => !merc.EquippedObjects.ContainsKey(i.Guid) && !(i is Container)
                            && (i.ValidLocations ?? 0) != 0 && (i.MaxStackSize ?? 1) <= 1 && !tried.Contains(i.Guid.Full)
                            && CanWield(merc, i))
                .ToList();
            var melee = merc.GetEquippedMeleeWeapon(true);
            var missile = merc.GetEquippedMissileWeapon();
            var wand = merc.GetEquippedWand();
            var shield = merc.GetEquippedShield();

            // 1. the weapon hand
            foreach (var w in carried.Where(i => i is MeleeWeapon && Trained(merc, i)).OrderByDescending(MeleeScore))
            {
                if (missile != null || wand != null) break;                           // a bow or wand stays its own kind
                if (melee != null && MeleeScore(w) <= MeleeScore(melee)) break;
                pick = w; loc = EquipMask.MeleeWeapon;
                if (melee != null) off.Add(melee);
                if (w.IsTwoHanded && shield != null) off.Add(shield);
                return true;
            }
            if (missile != null)
                foreach (var w in carried.Where(i => i is MissileLauncher && Trained(merc, i)).OrderByDescending(i => i.DamageMod ?? 1))
                {
                    if ((w.DamageMod ?? 1) <= (missile.DamageMod ?? 1) || w.AmmoType != missile.AmmoType) break;
                    pick = w; loc = EquipMask.MissileWeapon; off.Add(missile);
                    return true;
                }

            // 2. the shield, beside a one-handed melee weapon or empty hands
            if ((melee == null || !melee.IsTwoHanded) && missile == null && wand == null)
                foreach (var s in carried.Where(i => i.IsShield).OrderByDescending(AL))
                {
                    if (shield != null && AL(s) <= AL(shield)) break;
                    pick = s; loc = EquipMask.Shield;
                    if (shield != null) off.Add(shield);
                    return true;
                }

            // 3. armor and clothing, the biggest armor gain first
            WorldObject best = null; List<WorldObject> bestOff = null; int bestGain = int.MinValue;
            foreach (var a in carried.Where(i => i is Clothing && (i.ClothingPriority ?? 0) != 0 && !i.IsShield))
            {
                var worn = merc.GetEquippedClothingArmor(a.ClothingPriority ?? 0);
                int gain;
                if (worn.Count == 0) gain = 1000 + AL(a);                               // an empty slot first
                else
                {
                    CoverageMask covered = 0;
                    foreach (var w in worn) covered |= w.ClothingPriority ?? 0;
                    if ((covered & ~(a.ClothingPriority ?? 0)) != 0) continue;         // would strip more than it covers
                    gain = AL(a) - worn.Max(AL);
                    if (gain <= 0) continue;
                }
                if (gain > bestGain) { best = a; bestOff = worn; bestGain = gain; }
            }
            if (best != null) { pick = best; loc = best.ValidLocations ?? 0; off = bestOff; return true; }

            // 4. jewelry and the rest: an empty slot of the ones it fits
            const EquipMask HANDS = EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.Shield
                                  | EquipMask.TwoHanded | EquipMask.MissileAmmo;
            foreach (var j in carried.Where(i => !(i is Clothing) && ((i.ValidLocations ?? 0) & HANDS) == 0))
                for (uint bit = 1; bit != 0; bit <<= 1)
                {
                    var b = (EquipMask)bit;
                    if (((j.ValidLocations ?? 0) & b) == 0) continue;
                    if (merc.EquippedObjects.Values.Any(e => ((e.CurrentWieldedLocation ?? 0) & b) != 0)) continue;
                    pick = j; loc = b;
                    return true;
                }
            return false;
        }
    }
}
