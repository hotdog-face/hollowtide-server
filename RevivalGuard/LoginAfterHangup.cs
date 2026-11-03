using System.Reflection;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using HarmonyLib;
using Character = ACE.Database.Models.Shard.Character;

namespace RevivalGuard
{
    /// <summary>
    /// A LOGIN WHOSE CLIENT HUNG UP WHILE IT LOADED NEVER ENTERS THE WORLD.
    ///
    /// WorldManager.PlayerEnterWorld loads the character's possessions on a database thread and only then queues
    /// DoPlayerEnterWorld, which makes the Player, SetPlayer on the session and puts it online, without asking whether
    /// the session is still there. When the client gives up first (its 15 s enter-world watchdog, a closed tab), the
    /// session drops with no Player, so nothing logs it out, and the load then puts the character in the world with
    /// nobody attached. It stays "online" until the next restart, and every later enter of it gets CharacterError 0x0D.
    /// Live 2026-11-03: the 14:56 UTC restart was slow to load landblocks, the reconnecting clients timed out, and
    /// Misty Buns, Numero-uno and Boo Bees were left standing; their accounts looped on 0x0D for hours (hundreds of
    /// logins each), and the client fell back to another character. So the enter is skipped for a session that is
    /// already ending; the character stays offline and the next login enters it normally.
    /// </summary>
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
