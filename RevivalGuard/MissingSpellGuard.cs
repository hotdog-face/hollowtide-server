using System;
using System.Reflection;
using ACE.DatLoader;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SCROLL WHOSE SPELL DOES NOT EXIST CANNOT CRASH THE SERVER (box test shard, 2026-10-24: four crashes).
    /// Scroll.SetEphemeralValues builds `new Spell(SpellDID)` and reads
    /// its Name; a spell id missing from the portal DAT's spell table leaves the spell's DAT half null and Name throws.
    /// It runs in the Scroll constructor, so a vendor opening (LoadInventory creates every item it sells), loot, @create
    /// or a character loading one all threw on the landblock tick and the process died. Retail data has no such scroll;
    /// imported or hand-made data can.
    ///   - Vendor.LoadInventoryItem: a scroll weenie whose spell is missing is left off the shop list.
    ///   - Scroll.SetEphemeralValues: such a scroll is built without a spell (Spell stays null, which ActOnUse already
    ///     answers with "SpellDID not found"), so every other path makes an inert scroll instead of crashing.
    /// Each logs once per weenie.
    /// </summary>
    static class MissingSpellGuard
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> s_Said = new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();

        internal static bool SpellMissing(uint spellId) => !DatManager.PortalDat.SpellTable.Spells.ContainsKey(spellId);

        static void Say(uint wcid, string what)
        {
            if (s_Said.TryAdd(wcid, true)) Mod.Log.Warn($"[RevivalGuard] MissingSpellGuard: {what}");
        }

        [HarmonyPatch]
        static class ShopList
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Vendor), "LoadInventoryItem");
            static bool Prepare(MethodBase original)
            {
                if (original != null || TargetMethod() != null) return true;
                Mod.Log.Error("[RevivalGuard] MissingSpellGuard: Vendor.LoadInventoryItem not found; the shop half is off");
                return false;
            }
            static bool Prefix(Vendor __instance, uint weenieClassId)
            {
                try
                {
                    var w = DatabaseManager.World.GetCachedWeenie(weenieClassId);
                    if (w == null || w.WeenieType != WeenieType.Scroll) return true;
                    var spell = w.GetProperty(PropertyDataId.Spell);
                    if (spell == null || !SpellMissing(spell.Value)) return true;
                    Say(weenieClassId, $"{__instance?.Name} sells scroll {weenieClassId} whose spell {spell.Value} does not exist; left off the shop list instead of crashing");
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MissingSpellGuard: shop: {e.Message}"); return true; }
            }
        }

        [HarmonyPatch]
        static class ScrollBuild
        {
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(Scroll), "SetEphemeralValues", Type.EmptyTypes);
            static bool Prepare(MethodBase original)
            {
                if (original != null || TargetMethod() != null) return true;
                Mod.Log.Error("[RevivalGuard] MissingSpellGuard: Scroll.SetEphemeralValues not found; the scroll half is off");
                return false;
            }
            static bool Prefix(Scroll __instance)
            {
                try
                {
                    var spell = __instance.SpellDID;
                    if (spell == null || !SpellMissing(spell.Value)) return true;
                    __instance.Spell = null;
                    __instance.Use = "Use this item to attempt to learn its spell.";
                    Say(__instance.WeenieClassId, $"scroll {__instance.Name} ({__instance.WeenieClassId}) has spell {spell.Value}, which does not exist; built without a spell instead of crashing");
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MissingSpellGuard: scroll: {e.Message}"); return true; }
            }
        }
    }
}
