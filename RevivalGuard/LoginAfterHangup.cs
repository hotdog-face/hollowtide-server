using System.Reflection;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using HarmonyLib;
using Character = ACE.Database.Models.Shard.Character;

namespace RevivalGuard
{
    [HarmonyPatch]
    static class LoginAfterHangup
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(WorldManager), "DoPlayerEnterWorld");

        static bool Prepare(MethodBase original)
        {
            if (original != null || TargetMethod() != null) return true;
            Mod.Log.Error("[RevivalGuard] LoginAfterHangup: WorldManager.DoPlayerEnterWorld not found; a login that hangs up while loading can still leave its character in the world");
            return false;
        }

        static bool Prefix(Session session, Character character)
        {
            if (session == null || (session.PendingTermination == null && session.State != SessionState.TerminationStarted)) return true;
            Mod.Log.Warn($"[RevivalGuard] LoginAfterHangup: {character?.Name} (0x{character?.Id:X8}, account {session.Account}) finished loading after its session ended; not entering the world");
            return false;
        }
    }
}
