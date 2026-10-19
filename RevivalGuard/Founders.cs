using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE FOUNDERS (owner, 2026-10-11: "one time only bragging rights that only people who were early supporters get
    /// and they can keep for life ... 'i was there' when the game started. Im an OG"; per account; everyone who
    /// arrived by 31 October 2026).
    ///
    /// Once per ACCOUNT made by the cutoff (Account.CreateTime), the first character that enters the world after this
    /// went live is given:
    ///   * the Founder's Shirt (901913): retail's anniversary-shirt tradition, in a gold no other item has;
    ///   * the Founder's Banner (901914): a wall-hook banner for a house;
    ///   * the title "Founder" (ACE CharacterTitle.HollowtideFounder, 9001), made the displayed title.
    /// Both items are Attuned and Bonded (tools/gpubox-ace/founders.sql): never traded, sold, dropped or lost. An
    /// account made after the cutoff never qualifies, so nobody can come by them later. Hollow Company accounts are
    /// left out. A pack too full for the gifts is told so, and the gifts wait for the next login.
    ///
    /// Who has been given them: Founders/granted.txt beside the mod (one account id a line), so a set is never
    /// given twice whatever happens to the character it went to.
    /// </summary>
    static class Founders
    {
        static readonly DateTime CUTOFF = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);   // made before November
        const uint SHIRT = 901913, BANNER = 901914, TITLE = 9001;
        const double DELAY_S = 6;   // after the world has loaded around them, so the gifts and the words are seen

        static readonly object s_Lock = new object();
        static HashSet<uint> s_Granted;
        static readonly HashSet<uint> s_InFlight = new HashSet<uint>();

        static string FilePath()
        {
            var dir = ModFolder.Sub("Founders");
            return dir == null ? null : Path.Combine(dir, "granted.txt");
        }

        static HashSet<uint> Granted()
        {
            if (s_Granted != null) return s_Granted;
            s_Granted = new HashSet<uint>();
            try
            {
                var f = FilePath();
                if (f != null && File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                        if (uint.TryParse(line.Trim(), out uint id)) s_Granted.Add(id);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] founders: could not read the granted list: {e.Message}"); }
            return s_Granted;
        }

        static void Stamp(uint accountId)
        {
            Granted().Add(accountId);
            try { var f = FilePath(); if (f != null) File.AppendAllText(f, accountId + "\n"); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] founders: could not record account {accountId}: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    uint acct = p.Account?.AccountId ?? p.Character?.AccountId ?? 0;
                    if (acct == 0 || Mercenaries.IsMerc(p)) return;
                    lock (s_Lock) { if (Granted().Contains(acct) || !s_InFlight.Add(acct)) return; }
                    var account = DatabaseManager.Authentication.GetAccountById(acct);
                    if (account == null || account.CreateTime >= CUTOFF) { lock (s_Lock) s_InFlight.Remove(acct); return; }
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(DELAY_S);
                    chain.AddAction(p, () => Give(p, acct));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] founders enter: {e.Message}"); }
            }
        }

        static void Give(Player p, uint acct)
        {
            try
            {
                if (p.Session == null || p.IsDestroyed) return;
                if (p.GetFreeInventorySlots() < 2)
                {
                    Tell(p, "You are one of Hollowtide's founders, and gifts are waiting for you: make room for two items in your main pack and log in again.");
                    return;
                }
                // Both made and checked (slots AND burden) before either goes in: a shirt given and a banner refused
                // left the account unstamped, so the shirt came again at every login.
                var gifts = new List<WorldObject>();
                foreach (var wcid in new[] { SHIRT, BANNER })
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (wo == null) { gifts.ForEach(g => g.Destroy()); Mod.Log.Warn($"[RevivalGuard] founders: no weenie {wcid} (tools/gpubox-ace/founders.sql not applied?)"); return; }
                    gifts.Add(wo);
                }
                if (!p.CanAddToInventory(gifts))
                {
                    gifts.ForEach(g => g.Destroy());
                    Tell(p, "Your founder's gifts could not be placed in your pack (too full or too heavy); they will be offered again next time you log in.");
                    return;
                }
                foreach (var wo in gifts)
                    if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); Mod.Log.Warn($"[RevivalGuard] founders: {wo.Name} for {p.Name} refused after the pack check; not offered again"); }
                p.AddTitle(TITLE, setAsDisplayTitle: true);
                lock (s_Lock) Stamp(acct);
                Tell(p, "Thank you for being here from the start. You are one of Hollowtide's founders: the Founder's Shirt, the Founder's Banner and the title Founder are yours for life. No one who arrives after October can ever have them.");
                Mod.Log.Info($"[RevivalGuard] founders: account {acct} ({p.Name}) given the founder's shirt, banner and title");
                Telemetry.Emit("founder", p, "");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] founders give: {e.Message}"); }
            finally { lock (s_Lock) s_InFlight.Remove(acct); }
        }

        static void Tell(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }
}
