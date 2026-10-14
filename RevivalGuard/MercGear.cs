using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
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
    /// </summary>
    [HarmonyPatch]
    static class MercGear
    {
        const int MAX_STEPS = 16;           // upgrades per give, so a bad rule can never loop
        static MethodInfo s_WieldReq;

        static MethodBase TargetMethod()
        {
            s_WieldReq = AccessTools.Method(typeof(Player), "CheckWieldRequirements", new[] { typeof(WorldObject) });
            var m = AccessTools.Method(typeof(Player), "GiveObjectToPlayer");
            Mod.Log.Info($"[RevivalGuard] MercGear: {(m != null ? "on" : "OFF (Player.GiveObjectToPlayer not found)")}");
            return m;
        }

        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(Player __instance, Player target)
        {
            try
            {
                if (target == null || !Mercenaries.IsMerc(target)) return;
                Later(target, 1.5, MAX_STEPS);
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

        /// <summary>Find the best single upgrade, take off what it replaces, put it on, then look again.</summary>
        static void Step(Player merc, int stepsLeft)
        {
            try
            {
                if (stepsLeft <= 0 || merc.CurrentLandblock == null || merc.IsDead || merc.Teleporting) return;
                if (!Pick(merc, out var item, out var loc, out var off)) return;

                foreach (var o in off) merc.HandleActionPutItemInContainer(o.Guid.Full, merc.Guid.Full);
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
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercGear: {merc?.Name}: {e.GetType().Name}: {e.Message}"); }
        }

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

        static bool Pick(Player merc, out WorldObject pick, out EquipMask loc, out List<WorldObject> off)
        {
            pick = null; loc = 0; off = new List<WorldObject>();
            var carried = merc.GetAllPossessions()
                .Where(i => !merc.EquippedObjects.ContainsKey(i.Guid) && !(i is Container)
                            && (i.ValidLocations ?? 0) != 0 && (i.MaxStackSize ?? 1) <= 1 && CanWield(merc, i))
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
