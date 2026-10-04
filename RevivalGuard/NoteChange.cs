using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A PYREAL VENDOR TAKES TRADE NOTES (owner, 2026-09-25: "I have loads of money but vendor seems
    /// to only take pyreal. thats like going to a store but the guy wants you to pay in pennies").
    /// Ours; retail and ACE pay only in coin (Vendor.BuyItems_ValidateTransaction tests
    /// Player.CoinValue, which counts coin stacks only, and SpendCurrency consumes wcid 273).
    ///
    /// While a purchase is validated, CoinValue also counts the notes carried, so the funds test
    /// passes when coin and notes together cover it. When the purchase is paid, coin goes first;
    /// only the shortfall breaks notes: the smallest single note that covers it, or the largest notes
    /// first when none does. The change comes back as notes, largest first, and under 100 as coin,
    /// not as a pile of 25,000-pyreal stacks. The player is told which notes were broken.
    ///
    /// Alternate-currency vendors are untouched. Our client counts notes into the vendor purse
    /// only when the shard answers the mod channel, so a stock ACE shard still refuses the buy.
    /// </summary>
    public static class NoteChange
    {
        public const uint PYREAL = 273;
        const int PYREAL_STACK = 25000;

        /// <summary>Every trade note in ace_world, largest first (wcid, face value).</summary>
        static readonly (uint Wcid, int Value)[] NOTES =
        {
            (20630, 250000), (20629, 200000), (20628, 150000), (2627, 100000), (7377, 75000),
            (2626, 50000), (7376, 25000), (7375, 20000), (7374, 15000), (2625, 10000),
            (2624, 5000), (2623, 1000), (2622, 500), (2621, 100),
        };

        /// <summary>Set by the validation prefix; only a purchase pays with notes.</summary>
        [ThreadStatic] internal static bool t_InBuy;

        static int CountOf(Player p, uint wcid) => p.GetInventoryItemsOfWCID(wcid).Sum(s => s.StackSize ?? 1);

        internal static long NoteValue(Player p)
        {
            long v = 0;
            foreach (var n in NOTES) v += (long)CountOf(p, n.Wcid) * n.Value;
            return v;
        }

        /// <summary>Which notes to break to cover `need`: (wcid, count) and their total.</summary>
        static List<(uint Wcid, int Value, int Count)> Choose(Player p, long need, out long total)
        {
            var held = NOTES.Select(n => (n.Wcid, n.Value, Held: CountOf(p, n.Wcid))).Where(n => n.Held > 0).ToList();
            var take = new Dictionary<uint, int>();
            total = 0;
            // One note that covers it all, the smallest such: the least change back.
            var one = held.Where(n => n.Value >= need).OrderBy(n => n.Value).FirstOrDefault();
            if (one.Held > 0) { take[one.Wcid] = 1; total = one.Value; }
            else
            {
                // Otherwise the largest first, then one more of the smallest left that tips it over.
                long remaining = need;
                foreach (var n in held)
                {
                    int k = (int)Math.Min(n.Held, remaining / n.Value);
                    if (k > 0) { take[n.Wcid] = k; remaining -= (long)k * n.Value; total += (long)k * n.Value; }
                }
                foreach (var n in held.OrderBy(n => n.Value))
                {
                    if (remaining <= 0) break;
                    take.TryGetValue(n.Wcid, out int already);
                    while (already < n.Held && remaining > 0) { already++; remaining -= n.Value; total += n.Value; }
                    take[n.Wcid] = already;
                }
                if (remaining > 0) return null;
            }
            return held.Where(n => take.ContainsKey(n.Wcid) && take[n.Wcid] > 0).Select(n => (n.Wcid, n.Value, take[n.Wcid])).ToList();
        }

        /// <summary>`amount` back as notes, largest first, and the rest under 100 as coin.</summary>
        static void GiveChange(Player p, long amount)
        {
            foreach (var n in NOTES)
            {
                int k = (int)(amount / n.Value);
                if (k <= 0) continue;
                amount -= (long)k * n.Value;
                while (k > 0) { int s = Math.Min(k, 250); Give(p, n.Wcid, s); k -= s; }
            }
            while (amount > 0) { int s = (int)Math.Min(amount, PYREAL_STACK); Give(p, PYREAL, s); amount -= s; }
        }

        static void Give(Player p, uint wcid, int count)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return;
            wo.SetStackSize(count);
            if (p.TryCreateInInventoryWithNetworking(wo)) return;
            // No room anywhere: at the player's feet rather than lost.
            wo.Location = new ACE.Entity.Position(p.Location);
            if (LandblockManager.AddObject(wo))
                p.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your pack is full, so {wo.Name} was set at your feet.", ChatMessageType.Broadcast));
            else wo.Destroy();
        }

        static string Named(int value) => $"{value:N0}";

        /// <summary>Pays `amount` with every coin carried plus the notes that cover the rest.
        /// False when coin and notes together fall short (nothing is taken then).</summary>
        internal static bool Pay(Player p, long amount)
        {
            long coins = CountOf(p, PYREAL);
            long shortfall = amount - coins;
            if (shortfall <= 0) return p.TryConsumeFromInventoryWithNetworking(PYREAL, (int)amount);
            var plan = Choose(p, shortfall, out long total);
            if (plan == null) return false;
            if (coins > 0) p.TryConsumeFromInventoryWithNetworking(PYREAL, (int)coins);
            foreach (var n in plan) p.TryConsumeFromInventoryWithNetworking(n.Wcid, n.Count);
            long change = total - shortfall;
            if (change > 0) GiveChange(p, change);
            var broke = string.Join(", ", plan.Select(n => n.Count > 1 ? $"{n.Count} x {Named(n.Value)}" : Named(n.Value)));
            p.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You paid with trade notes ({broke})" + (change > 0 ? $"; {change:N0} in change went to your pack." : "."),
                ChatMessageType.Broadcast));
            return true;
        }

        static readonly System.Reflection.MethodInfo s_UpdateCoinValue = AccessTools.Method(typeof(Player), "UpdateCoinValue");
        internal static void Recount(Player p) => s_UpdateCoinValue?.Invoke(p, new object[] { true });
    }

    /// <summary>The funds test counts notes too, for the length of one validation.</summary>
    [HarmonyPatch(typeof(Vendor), nameof(Vendor.BuyItems_ValidateTransaction))]
    static class NoteChange_Validate
    {
        static void Prefix(Vendor __instance, Player player, out long __state)
        {
            __state = 0;
            if (__instance.AlternateCurrency != null || player == null) return;
            long notes = NoteChange.NoteValue(player);
            if (notes <= 0) return;
            __state = notes;
            player.CoinValue = (int)Math.Min(int.MaxValue, (player.CoinValue ?? 0) + notes);
            NoteChange.t_InBuy = true;
        }

        static void Finalizer(Player player, long __state)
        {
            if (__state == 0) return;
            NoteChange.t_InBuy = false;
            NoteChange.Recount(player);   // the true coin total, sent to the client
        }
    }

    /// <summary>A purchase's payment: coin first, then notes with change.</summary>
    [HarmonyPatch(typeof(Player), "SpendCurrency")]
    static class NoteChange_Spend
    {
        static bool Prefix(Player __instance, uint currentWcid, uint amount, bool destroy, ref List<WorldObject> __result)
        {
            if (!NoteChange.t_InBuy || currentWcid != NoteChange.PYREAL || !destroy || amount == 0) return true;
            NoteChange.Pay(__instance, amount);
            __result = new List<WorldObject>();
            return false;
        }
    }
}
