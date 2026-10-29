using System.Collections.Generic;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE OFF-HAND GRIP AS RETAIL SENT IT (owner 2026-10-24, "a spear held as a dual-wield sword";
    /// docs/findings/weapon-audit-2026-10.md). ACE hangs every off-hand weapon on LeftWeapon with the
    /// RightHandNonCombat placement; the retail captures show RightHandCombat for all of them and LeftUnarmed for a
    /// cestus or nekode (OffHandGripRule). Both clients draw what the server says, so the fix is here.
    /// ACE's "is the shield slot free" check (WieldedLocationIsAvailable via GetEquippedItems) compares hold points,
    /// so with two off-hand hold points it is widened to anything already in the shield slot.
    ///   @modifybool offhand_retail_grip false   ACE's grip
    /// </summary>
    static class OffHandGrip
    {
        internal const string P_ON = "offhand_retail_grip";
        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        static MethodBase Find(string name)
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), name);
            if (m == null) Mod.Log.Error($"[RevivalGuard] OffHandGrip: Creature.{name} not found; the off hand keeps ACE's grip");
            return m;
        }

        static int s_Registered;
        static void RegisterSwitch()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard OffHandGrip: off-hand weapons use retail's grip (RightHandCombat; cestus on LeftUnarmed). false: ACE's");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] OffHandGrip: switch not registered with @modifybool ({e.Message}); read at its default"); }
        }

        [HarmonyPatch]
        static class Placement_
        {
            static MethodBase TargetMethod() => Find("GetPlacementLocation");
            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                RegisterSwitch();
                Mod.Log.Info("[RevivalGuard] OffHandGrip: off-hand weapons held with retail's grip");
                return true;
            }

            static void Postfix(WorldObject item, EquipMask wieldedLocation, ref Placement placement, ref ParentLocation parentLocation)
            {
                if (item == null || !On) return;
                if (!OffHandGripRule.Retail((uint)wieldedLocation, item.ItemType == ItemType.Armor,
                        (int)(item.DefaultCombatStyle ?? 0), item.GetProperty(PropertyInt.WeaponType) ?? 0, out int loc, out int pose)) return;
                parentLocation = (ParentLocation)loc;
                placement = (Placement)pose;
            }
        }

        [HarmonyPatch]
        static class ShieldSlotTaken
        {
            static MethodBase TargetMethod() => Find(nameof(Creature.GetEquippedItems));
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(Creature __instance, WorldObject item, EquipMask wieldedLocation, List<WorldObject> __result)
            {
                if (__result == null || wieldedLocation != EquipMask.Shield || !On) return;
                foreach (var worn in __instance.EquippedObjects.Values)
                    if (worn != item && worn.CurrentWieldedLocation == EquipMask.Shield && !__result.Contains(worn))
                        __result.Add(worn);
            }
        }
    }
}
