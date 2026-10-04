using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// TRADE NOTES PAY THE PYREAL PART OF HOUSE MAINTENANCE, WITH CHANGE. Ours, not retail. Owner, 2026-10-01:
    /// "we should allow players to pay maintenance with mmds".
    ///
    /// Stock ACE already lets a note into the rent (HousePayment.GetConsumeItems counts PromissoryNotes toward
    /// wcid 273, and HouseProfile.SetPaidItems counts a note in the slumlord at its Value), but it takes whole
    /// notes and caps Paid at the rent: one 250,000 MMD against 50,000 owed vanished 200,000 pyreals. So when the
    /// payment carries notes, this replaces the payment:
    ///   - the non-note items go through ACE's own GetConsumeItems / TryConsumeItemForRent, as before;
    ///   - each note pays its face value toward the PYREAL rent still owed (smallest notes first); the slumlord
    ///     is credited in pyreals (coin stacks in its inventory, which is what IsRentPaid, ProcessRent and the
    ///     house panel all read), and any overpayment comes back as notes and pyreals;
    ///   - the change is checked against the pack first: no room, no payment, the note stays.
    /// Trade-good rent items still need the real items. A note given (dragged) onto an owned house's slumlord
    /// is routed into the same payment.
    /// </summary>
    static class HouseRentNotes
    {
        const uint PYREAL = 273;
        const int COIN_STACK = 25000;
        static readonly (uint wcid, int value)[] NOTES =
        {
            (20630, 250000), (20629, 200000), (20628, 150000), (2627, 100000), (7377, 75000), (2626, 50000),
            (7376, 25000), (7375, 20000), (7374, 15000), (2625, 10000), (2624, 5000), (2623, 1000), (2622, 500), (2621, 100),
        };

        static int UnitValue(WorldObject note) =>
            note.StackUnitValue ?? ((note.Value ?? 0) / Math.Max(1, note.StackSize ?? 1));

        /// <summary>The whole maintenance payment when notes are in it. Runs on the player's own action thread.</summary>
        internal static void Pay(Player p, SlumLord slumlord, List<WorldObject> others, List<(WorldObject note, int units)> notes)
        {
            bool consumed = false;
            if (others.Count > 0)
            {
                var consumeItems = p.GetConsumeItems(slumlord.GetHouseProfile().Rent, others);
                if (p.IsTrading) consumeItems.RemoveAll(c => p.ItemsInTradeWindow.Contains(c.Guid));
                foreach (var c in consumeItems)
                    consumed |= p.TryConsumeItemForRent(slumlord, c);
            }

            var credited = PayWithNotes(p, slumlord, notes);
            if (!consumed && credited <= 0)
            {
                if (credited == 0)
                    Say(p, "No pyreals are owed for this period's maintenance.");
                foreach (var (note, _) in notes)
                    p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, note.Guid.Full));
                return;
            }

            // ACE's own tail of HandleActionRentHouse
            slumlord.MergeAllStackables();
            slumlord.SaveBiotaToDatabase();
            foreach (var item in slumlord.Inventory.Values)
                item.SaveBiotaToDatabase();
            slumlord.ActOnUse(p);
            p.HandleActionQueryHouse();
            Say(p, $"Maintenance {(slumlord.IsRentPaid() ? "" : "partially ")}paid.");
        }

        /// <summary>Pays notes toward the pyreal rent owed. Returns pyreals credited, 0 when none were owed,
        /// -1 when refused (the reason already told).</summary>
        static long PayWithNotes(Player p, SlumLord slumlord, List<(WorldObject note, int units)> notes)
        {
            if (notes.Count == 0) return 0;
            var owed = slumlord.GetHouseProfile().Rent.FirstOrDefault(r => r.WeenieID == PYREAL);
            long remaining = owed?.Remaining ?? 0;
            if (remaining <= 0) return 0;

            long credited = 0, change = 0;
            var take = new List<(WorldObject note, int units)>();
            foreach (var (note, units) in notes.OrderBy(n => UnitValue(n.note)))
            {
                int v = UnitValue(note), k = 0;
                if (v <= 0) continue;
                while (k < units && remaining > 0)
                {
                    k++;
                    long pay = Math.Min(v, remaining);
                    credited += pay; remaining -= pay; change += v - pay;
                }
                if (k > 0) take.Add((note, k));
                if (remaining <= 0) break;
            }
            if (credited <= 0) return 0;

            var changeItems = Make(change, true);
            var coins = Make(credited, false);
            if (changeItems.Count > 0 && !p.CanAddToInventory(changeItems))
            {
                Destroy(changeItems); Destroy(coins);
                Say(p, $"You do not have room in your pack for {change:N0} pyreals in change. Make some room and try again.");
                return -1;
            }
            if (!slumlord.CanAddToInventory(coins))
            {
                Destroy(changeItems); Destroy(coins);
                Say(p, "The maintenance cannot be paid with trade notes right now.");
                Mod.Log.Warn($"[RevivalGuard] HouseRentNotes: slumlord 0x{slumlord.Guid.Full:X8} has no room for {credited} pyreals");
                return -1;
            }

            int taken = 0;
            foreach (var (note, k) in take)
            {
                if (!p.TryConsumeFromInventoryWithNetworking(note, k))
                {
                    // nothing of this note was taken; give back what it would have paid
                    Mod.Log.Warn($"[RevivalGuard] HouseRentNotes: could not take {k} x {note.Name} from {p.Name}");
                    Destroy(changeItems); Destroy(coins);
                    long back = 0;
                    foreach (var (n2, k2) in take.Take(taken)) back += (long)UnitValue(n2) * k2;
                    if (back > 0) foreach (var o in Make(back, true)) p.TryCreateInInventoryWithNetworking(o);
                    Say(p, "The trade notes could not be taken. Nothing was paid with them.");
                    return -1;
                }
                taken++;
            }
            foreach (var c in coins) slumlord.TryAddToInventory(c);
            foreach (var o in changeItems) p.TryCreateInInventoryWithNetworking(o);

            int count = take.Sum(t => t.units);
            Say(p, change > 0
                ? $"Your {(count == 1 ? "trade note pays" : $"{count} trade notes pay")} {credited:N0} pyreals of the maintenance. You receive {change:N0} pyreals in change."
                : $"Your {(count == 1 ? "trade note pays" : $"{count} trade notes pay")} {credited:N0} pyreals of the maintenance.");
            Mod.Log.Info($"[RevivalGuard] HouseRentNotes: {p.Name} paid {credited} with {count} note(s) at 0x{slumlord.Guid.Full:X8}, change {change}");
            return credited;
        }

        /// <summary>Pyreals as objects: notes for the hundreds when asked (change), then coin stacks.</summary>
        static List<WorldObject> Make(long amount, bool useNotes)
        {
            var list = new List<WorldObject>();
            if (useNotes)
                foreach (var (wcid, value) in NOTES)
                {
                    int n = (int)(amount / value);
                    if (n <= 0) continue;
                    var note = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (note == null) continue;
                    note.SetStackSize(n);
                    list.Add(note);
                    amount -= (long)n * value;
                }
            while (amount > 0)
            {
                int n = (int)Math.Min(amount, COIN_STACK);
                var coin = WorldObjectFactory.CreateNewWorldObject(PYREAL);
                if (coin == null) break;
                coin.SetStackSize(n);
                list.Add(coin);
                amount -= n;
            }
            return list;
        }

        static void Destroy(List<WorldObject> items) { foreach (var o in items) o.Destroy(); }

        internal static void Say(Player p, string text) =>
            p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>The stock refusals of HandleActionRentHouse, so a note payment refuses the same way.</summary>
        internal static bool StockWouldRefuse(Player p, SlumLord slumlord)
        {
            if (slumlord.HouseOwner == null) return true;
            if (slumlord.IsRentPaid()) return true;
            var owner = PlayerManager.FindByGuid(slumlord.HouseOwner ?? 0);
            if (owner == null) return true;
            var houses = PropertyManager.GetBool("house_per_char").Item
                ? HouseManager.GetCharacterHouses(owner.Guid.Full)
                : HouseManager.GetAccountHouses(owner.Account.AccountId);
            return houses.Count() > 1;
        }
    }

    /// <summary>The house panel's pay (and the give route below): when notes are in the payment, ours runs it.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionRentHouse))]
    static class HouseRentNotesPanel
    {
        [HarmonyPriority(Priority.Low)]
        static bool Prefix(Player __instance, uint slumlord_id, List<uint> item_ids, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal) return false;
                if (item_ids == null || item_ids.Count == 0) return true;
                var items = __instance.GetInventoryItems(item_ids);
                var notes = items.Where(i => i.IsTradeNote).ToList();
                if (notes.Count == 0) return true;
                var slumlord = __instance.FindObject(slumlord_id, Player.SearchLocations.Landblock) as SlumLord;
                if (slumlord == null || HouseRentNotes.StockWouldRefuse(__instance, slumlord)) return true;   // stock says why
                if (__instance.IsTrading) notes.RemoveAll(n => __instance.ItemsInTradeWindow.Contains(n.Guid));
                var others = items.Where(i => !i.IsTradeNote).ToList();
                HouseRentNotes.Pay(__instance, slumlord, others, notes.Select(n => (n, n.StackSize ?? 1)).ToList());
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] HouseRentNotes panel: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>A trade note given (dragged) onto an owned house's slumlord pays its maintenance.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    static class HouseRentNotesGive
    {
        [HarmonyPriority(Priority.Low)]     // after ItemLock's refusal of a locked item
        static bool Prefix(Player __instance, uint targetGuid, uint itemGuid, int amount, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal) return false;
                if (__instance.IsBusy || __instance.Teleporting || amount <= 0) return true;
                if (!(__instance.FindObject(targetGuid, Player.SearchLocations.Landblock) is SlumLord slumlord)) return true;
                var note = __instance.FindObject(itemGuid, Player.SearchLocations.MyInventory);
                if (note == null || !note.IsTradeNote || (note.StackSize ?? 1) < amount) return true;
                if (slumlord.HouseOwner == null) return true;
                if (HouseRentNotes.StockWouldRefuse(__instance, slumlord))
                {
                    HouseRentNotes.Say(__instance, slumlord.IsRentPaid()
                        ? "The maintenance has already been paid for this period.\nYou may not prepay next period's maintenance."
                        : "The owner of this house currently owns multiple houses. Maintenance cannot be paid until they only own 1 house.");
                    __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
                    return false;
                }
                // walk to it first, as a stock give does
                __instance.CreateMoveToChain(slumlord, ok =>
                {
                    try
                    {
                        var still = __instance.FindObject(itemGuid, Player.SearchLocations.MyInventory);
                        if (ok && still != null && (still.StackSize ?? 1) >= amount)
                            HouseRentNotes.Pay(__instance, slumlord, new List<WorldObject>(), new List<(WorldObject, int)> { (still, amount) });
                        else
                            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
                    }
                    catch (Exception e) { Mod.Log.Error($"[RevivalGuard] HouseRentNotes give: {e}"); }
                });
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] HouseRentNotes give: {e.Message}");
                return true;
            }
        }
    }
}
