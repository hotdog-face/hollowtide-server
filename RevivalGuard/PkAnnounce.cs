using System;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// EVERY PLAYER KILL, SERVER-WIDE (owner, 2026-10-12: "do we have proper Pk kill messages? like lou just killed
    /// Lillypad. retail had this and it was always fun to see" ... "i want them serverwide").
    ///
    /// ACE already sends retail's death line to the players NEAR the fight (Player_Death.cs, GameMessagePlayerKilled)
    /// and the killer and victim their own. This adds one line to everyone else online: "Lou Sassel has slain
    /// Lillypad near 42.1N, 33.6E!" (players in the same landblock already had ACE's line). Players who have turned off retail's "Hear PK deaths" option
    /// (CharacterOption.ListenToPKDeathMessages) are left out, and so are the two who were in it.
    ///
    /// Only a real PK kill: the same test PkRenown uses (HandlePKDeathBroadcast, IsPKDeath, a player killer who
    /// is not the victim). Tunable pk.announce (1 on, 0 off).
    /// </summary>
    static class PkAnnounce
    {
        static bool On => Tunables.D("pk.announce", 1) > 0.5;

        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast))]
        static class OnPkDeath
        {
            static void Postfix(Player __instance, DamageHistoryInfo topDamager)
            {
                try
                {
                    if (!On || topDamager == null || !topDamager.IsPlayer || !__instance.IsPKDeath(topDamager)) return;
                    var killer = topDamager.TryGetAttacker() as Player;
                    if (killer == null || killer == __instance) return;
                    string where = __instance.Location?.GetMapCoordStr();
                    string line = $"{killer.Name} has slain {__instance.Name}{(string.IsNullOrEmpty(where) ? "" : " near " + where)}!";
                    var msg = new GameMessageSystemChat(line, ChatMessageType.Broadcast);
                    int told = 0;
                    foreach (var p in PlayerManager.GetAllOnline())
                    {
                        if (p == null || p == killer || p == __instance || p.Session == null) continue;
                        // near the fight: ACE's own death line already reached them (EnqueueBroadcast, the landblock's range)
                        if (p.CurrentLandblock != null && p.CurrentLandblock == __instance.CurrentLandblock) continue;
                        if (!p.GetCharacterOption(CharacterOption.ListenToPKDeathMessages)) continue;
                        p.Session.Network.EnqueueSend(msg);
                        told++;
                    }
                    Mod.Log.Info($"[RevivalGuard] PK announce: \"{line}\" to {told} player(s)");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkAnnounce: {e.Message}"); }
            }
        }
    }
}
