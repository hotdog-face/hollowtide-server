using System;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// REFUNDS (owner 2026-11-23: "make it so the gem is in his inventory on next server restart"). A staff refund for an
    /// item a bug took, without reading or editing anything on live: add a line to LIST and stage the mod. At the named
    /// character's next login the item goes into their pack, ONCE: the refund's id is kept on the character (DONE, a
    /// PropertyString of ids), so a relog, a restart or a later stage never gives it again. A pack that cannot take it says
    /// so and tries again at the next login. Names match the character's name exactly, case aside (staff chars keep '+').
    /// </summary>
    public static class Refunds
    {
        internal const PropertyString DONE = (PropertyString)29190;   // ours: comma-separated refund ids already given

        internal sealed class Refund
        {
            public string Id, Character, Why; public uint Wcid; public int Count = 1;
        }

        /// <summary>Each line: the ticket it answers (its id, never reused), who, what, and the line they read.</summary>
        internal static readonly Refund[] LIST =
        {
            // F7 261123-0021: a Rage of Grael used with no weapon wielded was eaten by ACE ("You fail to affect ...").
            // GemNoEffectGuard keeps such gems from 67efdeb3 on.
            new Refund { Id = "261123-0021", Character = "Chemist", Wcid = 31425,
                         Why = "a Rage of Grael Gem, returned: a bug used yours up. Wield your weapon, then use the gem by itself" },
        };

        internal static bool Given(string done, string id) =>
            !string.IsNullOrEmpty(done) && done.Split(',').Any(x => x.Trim() == id);

        internal static string With(string done, string id) => string.IsNullOrEmpty(done) ? id : done + "," + id;

        static void AtLogin(Player p)
        {
            if (p?.Session == null || LIST.Length == 0) return;
            var mine = LIST.Where(r => string.Equals(r.Character, p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) return;
            // a few seconds in, so the pack is loaded and the line is not lost in the login burst
            var chain = new ActionChain();
            chain.AddDelaySeconds(5);
            chain.AddAction(p, () => Deliver(p, mine));
            chain.EnqueueChain();
        }

        static void Deliver(Player p, System.Collections.Generic.List<Refund> mine)
        {
            if (p?.Session == null) return;
            foreach (var r in mine)
            {
                try
                {
                    if (Given(p.GetProperty(DONE), r.Id)) continue;
                    var wo = WorldObjectFactory.CreateNewWorldObject(r.Wcid);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] Refunds: {r.Id}: wcid {r.Wcid} could not be made for {p.Name}"); continue; }
                    if (r.Count > 1 && wo.MaxStackSize > 1) wo.SetStackSize(Math.Min(r.Count, wo.MaxStackSize ?? 1));
                    if (!p.TryCreateInInventoryWithNetworking(wo))
                    {
                        wo.Destroy();
                        Say(p, $"A refund is waiting for you ({r.Why}), but your pack has no room. Make room and log in again.");
                        Mod.Log.Info($"[RevivalGuard] Refunds: {r.Id}: {p.Name}'s pack could not take {r.Wcid}; next login");
                        continue;
                    }
                    p.SetProperty(DONE, With(p.GetProperty(DONE), r.Id));
                    p.SaveBiotaToDatabase();
                    Say(p, $"You receive {r.Why}.");
                    Mod.Log.Info($"[RevivalGuard] Refunds: {r.Id}: {p.Name} received {wo.Name} ({r.Wcid})");
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Refunds: {r.Id} for {p.Name}: {e.Message}"); }
            }
        }

        static void Say(Player p, string text) =>
            p.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class AtLoginPatch
        {
            static void Postfix(Player __instance)
            {
                try { AtLogin(__instance); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Refunds: login of {__instance?.Name}: {e.Message}"); }
            }
        }
    }
}
