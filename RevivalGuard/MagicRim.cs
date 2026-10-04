using System;
using System.Collections.Generic;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BLUE ICON RIM ON MAGIC ITEMS. Retail's IconData::RenderIcons @0058d180 draws the rim from
    /// the item's UiEffects bitfield (Magical = 0x1 is the blue border, 0x060011CA through DidMapper
    /// 0x25000009), and the client already does exactly that (AcIconUnderlay.Effect, MAE 0.000 against
    /// a retail capture). What the client never gets is the bit: ACE sets UiEffects = Magical only on
    /// generated loot (LootGenerationFactory_Magic.cs AssignMagic) and the world database is patchy.
    /// Measured on ace_world 2026-09-22: 916 static weenies carry item spells and a mana pool but no
    /// UiEffects row at all (421 melee weapons, 307 armour, 72 clothing, 44 jewelry, 52 bows and
    /// atlatls, 20 casters), so Leggings of Noble Strength or a Fallen Axe arrive with the plain rim.
    ///
    /// A POSTFIX ON CREATION, not a startup pass over the weenie cache. ACE builds a WorldObject from
    /// one of two sources, a cached Weenie (fresh spawns, vendor stock, loot bases) or a saved Biota
    /// (everything already in a pack, on a corpse or placed), and only the first ever consults the
    /// weenie again. A pass over the weenies could never reach an item already in a player's pack; a
    /// postfix on both WorldObjectFactory.CreateWorldObject overloads reaches every item the shard
    /// will ever send, and costs one property read per object. The stamp lands on the object's own
    /// biota, so it is saved with the item and is a no-op the next time round, which is exactly how
    /// ACE's own loot stamp behaves.
    ///
    /// WHAT QUALIFIES, and why the set is narrow. The rule is taken from the rows retail DID send
    /// (the UiEffects rows in ace_world are capture-derived):
    ///   * an item, never a creature (2,647 creature weenies have spell books for casting);
    ///   * no UiEffects row at all. An explicit row, even an element rim, is retail's word and stands;
    ///   * at least one item spell (weenie_properties_spell_book) AND ItemMaxMana > 0: the two
    ///     properties every retail magic item carried, and the pair all 916 share;
    ///   * armour, clothing, jewelry or a melee weapon of any damage type: on rowed weenies Magical is
    ///     the rim in 82-100% of each class (cold 135 vs 27 Frost, fire 159 vs 28, acid 155 vs 32,
    ///     electric 333 vs 25; armour 278 vs 42; jewelry 265 vs 75, the Nuhmudira gorgets);
    ///   * a bow, atlatl or caster ONLY when it has no W_DamageType. With one, the rowed data splits
    ///     almost evenly between Magical and the element or physical rim (bows with fire 8 vs 8,
    ///     casters with slash 8 vs 10), so nothing can be stamped there without guessing.
    /// Items without spells, potions and gems (Boost rims) and anything retail drew with an element
    /// rim are untouched: only Magical is ever written, and only where no rim existed.
    /// </summary>
    [HarmonyPatch]
    static class MagicRim
    {
        const ItemType WEARABLE = ItemType.Armor | ItemType.Clothing | ItemType.Jewelry | ItemType.MeleeWeapon;
        const ItemType RANGED_OR_CASTER = ItemType.MissileWeapon | ItemType.Caster;
        static int s_Hooked;
        internal static long Stamped;

        static IEnumerable<MethodBase> TargetMethods()
        {
            // Both overloads are declared on WorldObjectFactory itself (AGENTS.md: patch the declaring
            // type). A missing target is logged and skipped, never thrown: a throw inside PatchAll
            // aborts Mod.Initialize and leaves every other guard off.
            var t = typeof(WorldObjectFactory);
            var fromWeenie = AccessTools.Method(t, nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(ACE.Entity.Models.Weenie), typeof(ObjectGuid) });
            var fromBiota = AccessTools.Method(t, nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(ACE.Entity.Models.Biota) });
            foreach (var (m, nm) in new[] { (fromWeenie, "CreateWorldObject(Weenie, ObjectGuid)"), (fromBiota, "CreateWorldObject(Biota)") })
            {
                if (m == null) { Mod.Log.Error($"[RevivalGuard] MagicRim: WorldObjectFactory.{nm} not found; items from that path keep the plain rim"); continue; }
                s_Hooked++;
                yield return m;
            }
        }

        public static void Register()
        {
            Mod.Log.Info($"[RevivalGuard] MagicRim: {s_Hooked}/2 creation paths hooked");
        }

        static void Postfix(WorldObject __result)
        {
            try
            {
                if (!Qualifies(__result)) return;
                __result.UiEffects = UiEffects.Magical;
                Stamped++;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MagicRim: {e.GetType().Name} stamping {__result?.Name}: {e.Message}"); }
        }

        internal static bool Qualifies(WorldObject wo)
        {
            if (wo == null || wo is Creature) return false;
            if (wo.UiEffects != null) return false;
            var spells = wo.Biota?.PropertiesSpellBook;
            if (spells == null || spells.Count == 0) return false;
            if ((wo.ItemMaxMana ?? 0) <= 0) return false;
            var type = wo.ItemType;
            if ((type & WEARABLE) != 0) return true;
            if ((type & RANGED_OR_CASTER) != 0) return wo.W_DamageType == DamageType.Undef;
            return false;
        }
    }
}
