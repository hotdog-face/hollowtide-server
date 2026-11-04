using System;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A WELCOME FOR PLAYERS ON A RETAIL-STYLE CLIENT (owner 2026-11-03, after Hollowtide went on the community server
    /// list that ThwargLauncher and webAC read: "its a key way to convert players to real engaged players so they see
    /// the better way to play we've built"). Our client says hello on the mod channel as it enters the world
    /// (ModChannel, "@revivalclient"); a connection that has not said it by HELLO_WAIT_S after entering is a retail
    /// client, ThwargLauncher's or webAC's, and gets one line pointing at ours. Once per login; mercenaries never.
    ///   @modifybool rg_retail_welcome false     off
    /// </summary>
    static class RetailWelcome
    {
        internal const string P_ON = "rg_retail_welcome";
        const double HELLO_WAIT_S = 20;   // our client's hello comes within a few seconds of entering; slow loads included
        internal const string LINE = "Welcome to Hollowtide! You're on a classic client, so you're missing a lot. Our free client adds the built-in Assistant (VTank-style automation), item locks, in-game bug reports and many quality of life improvements, and players without it are at a real disadvantage. Get it at hollowtide.org, or play it right in your browser there.";

        internal static void Register() =>
            ExpansionMotions.RegisterProperty(P_ON, true, "RevivalGuard RetailWelcome: tell players on a retail-style client (ThwargLauncher, webAC) about our client");

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || Mercenaries.IsMerc(p) || !PropertyManager.GetBool(P_ON, true).Item) return;
                    var session = p.Session;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(HELLO_WAIT_S);
                    chain.AddAction(p, () =>
                    {
                        try
                        {
                            // the same login still in the world, and it never said our hello
                            if (session == null || p.Session != session || ModChannel.Capable(session)) return;
                            session.Network?.EnqueueSend(new GameMessageSystemChat(LINE, ChatMessageType.Broadcast));
                            Mod.Log.Info($"[RevivalGuard] RetailWelcome: {p.Name} ({session.Account}) is on a retail-style client; told about ours");
                        }
                        catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailWelcome: {e.Message}"); }
                    });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailWelcome enter: {e.Message}"); }
            }
        }
    }
}
