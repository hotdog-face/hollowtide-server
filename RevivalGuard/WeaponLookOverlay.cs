using System;
using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A weapon look's icon overlay goes through tailoring.</summary>
    static class WeaponLookOverlay
    {
        internal const string P_ON = "tailoring_weapon_icon_overlay";
        static int s_Registered;

        static bool On
        {
            get
            {
                Register();
                return PropertyManager.GetBool(P_ON, true).Item;
            }
        }

        static void Register()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard WeaponLookOverlay: weapon tailoring moves the look's icon overlay (the flags' and pennants' emblem) (false: ACE)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WeaponLookOverlay: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        // ------------------------------------------------------------------ look -> applicator
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.SetWeaponProperties))]
        static class TakeLook
        {
            static void Postfix(WorldObject source, WorldObject target)
            {
                try
                {
                    if (source == null || target == null || !On) return;
                    target.IconOverlaySecondary = source.IconOverlayId;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WeaponLookOverlay.TakeLook: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ applicator -> weapon
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.UpdateWeaponProps))]
        static class PutLook
        {
            static void Postfix(Player player, WorldObject source, WorldObject target)
            {
                try
                {
                    if (player == null || source == null || target == null || !On) return;
                    uint? overlay = source.IconOverlaySecondary
                                    ?? DatabaseManager.World.GetCachedWeenie(target.WeenieClassId)?.GetProperty(PropertyDataId.IconOverlay);
                    if (overlay != target.IconOverlayId)
                        player.UpdateProperty(target, PropertyDataId.IconOverlay, overlay);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WeaponLookOverlay.PutLook: {e.Message}"); }
            }
        }
    }
}
