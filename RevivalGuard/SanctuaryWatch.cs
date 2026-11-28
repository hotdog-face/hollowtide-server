using System;
using System.Diagnostics;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    static class SanctuaryWatch
    {
        internal static string Loc(Position p) => p == null ? "none" : $"0x{p.Cell:X8} [{p.PositionX:F1} {p.PositionY:F1} {p.PositionZ:F1}]";

        internal static string Callers()
        {
            var st = new StackTrace(2, false);
            var s = "";
            for (int i = 0, n = 0; i < st.FrameCount && n < 5; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null || m.DeclaringType == null) continue;
                var t = m.DeclaringType.Name;
                if (t.StartsWith("SanctuaryWatch", StringComparison.Ordinal) || t.Contains("Harmony") || t.StartsWith("<", StringComparison.Ordinal)) continue;
                s += (n++ == 0 ? "" : " < ") + t + "." + m.Name;
            }
            return s;
        }

        internal static bool InAcademy(Position p) => p != null && AcademyExitAmnesty.ACADEMY.Contains((ushort)(p.Cell >> 16));
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.SetPosition), new[] { typeof(PositionType), typeof(Position) })]
    static class SanctuaryWatchSet
    {
        static void Prefix(WorldObject __instance, PositionType positionType, Position position, out string __state)
        {
            __state = null;
            try
            {
                if (positionType != PositionType.Sanctuary || !(__instance is Player)) return;
                __state = SanctuaryWatch.Loc(__instance.GetPosition(PositionType.Sanctuary));
            }
            catch { }
        }

        static void Postfix(WorldObject __instance, PositionType positionType, Position position, string __state)
        {
            if (__state == null) return;
            try
            {
                Mod.Log.Info($"[RevivalGuard] SanctuaryWatch: {__instance.Name} (0x{__instance.Guid.Full:X8}, level {(__instance as Player)?.Level}) "
                    + $"bind {__state} -> {SanctuaryWatch.Loc(position)} by {SanctuaryWatch.Callers()}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.RemovePosition), new[] { typeof(PositionType) })]
    static class SanctuaryWatchRemove
    {
        static void Prefix(WorldObject __instance, PositionType positionType)
        {
            try
            {
                if (positionType != PositionType.Sanctuary || !(__instance is Player)) return;
                Mod.Log.Warn($"[RevivalGuard] SanctuaryWatch: {__instance.Name} (0x{__instance.Guid.Full:X8}) bind "
                    + $"{SanctuaryWatch.Loc(__instance.GetPosition(PositionType.Sanctuary))} REMOVED by {SanctuaryWatch.Callers()}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ThreadSafeTeleportOnDeath))]
    static class SanctuaryWatchDeath
    {
        static void Prefix(Player __instance)
        {
            try
            {
                if (__instance.Sanctuary != null) return;
                Mod.Log.Warn($"[RevivalGuard] SanctuaryWatch: {__instance.Name} (0x{__instance.Guid.Full:X8}) died with no bind; "
                    + $"ACE sends the body to the creation spot {SanctuaryWatch.Loc(__instance.Instantiation)}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote))]
    static class SanctuaryAcademyGuard
    {
        static bool Prefix(EmoteManager __instance, PropertiesEmoteAction emote, WorldObject targetObject, ref float __result)
        {
            try
            {
                if (emote == null || (EmoteType)emote.Type != EmoteType.SetSanctuaryPosition || !(targetObject is Player p)) return true;
                var bind = p.Sanctuary;
                if (bind == null || SanctuaryWatch.InAcademy(bind)) return true;   // a new character leaving: as retail
                var to = emote.ObjCellId ?? 0;
                if (!AcademyExitAmnesty.ACADEMY.Contains((ushort)(p.Location?.Cell >> 16 ?? 0))) return true;   // not at an Academy door
                Mod.Log.Warn($"[RevivalGuard] SanctuaryWatch: kept {p.Name}'s bind {SanctuaryWatch.Loc(bind)}: the Training Academy's "
                    + $"exit would have moved it to 0x{to:X8} (an emote of {__instance.WorldObject?.Name} 0x{__instance.WorldObject?.WeenieClassId})");
                __result = 0f;
                return false;
            }
            catch { return true; }
        }
    }
}
