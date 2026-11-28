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
            static bool Prefix(Vendor __instance, uint weenieClassId) => !LeftOffShop(__instance?.Name, weenieClassId);
        }

        /// <summary>A shop item to leave off the list: a scroll whose spell does not exist.</summary>
        internal static bool LeftOffShop(string vendorName, uint weenieClassId)
        {
            try
            {
                var w = DatabaseManager.World.GetCachedWeenie(weenieClassId);
                if (w == null || w.WeenieType != WeenieType.Scroll) return false;
                var spell = w.GetProperty(PropertyDataId.Spell);
                if (spell == null || !SpellMissing(spell.Value)) return false;
                Say(weenieClassId, $"{vendorName} sells scroll {weenieClassId} whose spell {spell.Value} does not exist; left off the shop list instead of crashing");
                return true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MissingSpellGuard: shop: {e.Message}"); return false; }
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
