using System.Runtime.CompilerServices;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE ALLEGIANCE BANK (owner, 2026-09-27; docs/MANSIONS-AND-PK.md). Ours.
    ///
    /// One storage chest of a mansion, chosen by its owner (the monarch): open it, type
    /// /mansion bank on. From then on:
    ///   - any member of the owner's allegiance may OPEN it and put things in, without the house's
    ///     storage permission (House.HasPermission is widened for that one chest only);
    ///   - only the owner's account and officers of the chosen level (/mansion bank rank 1-3, default
    ///     1 = Speaker and up) may take anything OUT. Every way an item leaves an open container --
    ///     moving it to a pack, wielding it, splitting or merging a stack -- first finds it through
    ///     Player.FindObject with LastUsedContainer in the search, so the refusal is made there: a
    ///     member who is not allowed simply cannot pick it up. Appraisal searches Everywhere and is
    ///     left alone, so members can still see what the bank holds;
    ///   - every opening is diffed against what the chest held when it opened, and what left or
    ///     arrived is written to the bank's log (/mansion bank log, kept in mansions.json).
    ///     Withdrawals are also said to every online member as an Allegiance line.
    /// The contents are ACE's own house storage, saved by ACE on every add and remove.
    /// The designation dies with the owner's ownership: a new owner of the mansion does not inherit
    /// a bank, because every check compares the chest's record with the house's current owner.
    /// </summary>
    internal static class MansionBank
    {
        sealed class Snap { public uint Opener; public string OpenerName; public Dictionary<uint, (string Name, int Count)> Items; }
        static readonly ConditionalWeakTable<Storage, Snap> s_Open = new ConditionalWeakTable<Storage, Snap>();
        [ThreadStatic] static Storage t_Checking;
        static readonly Dictionary<uint, double> s_LastRefusal = new Dictionary<uint, double>();

        /// <summary>The live bank record for this chest, or null when it is not a bank (or its
        /// mansion changed hands since it was made one).</summary>
        internal static BankRec BankFor(Storage s)
        {
            if (s == null) return null;
            var rec = Store.Bank(s.Guid.Full);
            if (rec == null) return null;
            var owner = PlayerManager.FindByGuid(rec.Owner);
            return owner != null && owner.HouseInstance == rec.House ? rec : null;
        }

        static bool IsMember(Player p, BankRec rec) =>
            p.Guid.Full == rec.Owner || p.Allegiance?.MonarchId == rec.Owner || SameAccount(p, rec.Owner);

        static bool SameAccount(Player p, uint ownerGuid)
        {
            var owner = PlayerManager.FindByGuid(ownerGuid);
            return owner?.Account != null && p.Account != null && owner.Account.AccountId == p.Account.AccountId;
        }

        internal static bool CanWithdraw(Player p, BankRec rec) =>
            p.Guid.Full == rec.Owner || SameAccount(p, rec.Owner)
            || (p.Allegiance?.MonarchId == rec.Owner && (p.AllegianceOfficerRank ?? 0) >= rec.Rank);

        // ------------------------------------------------------------ members may open it

        [HarmonyPatch(typeof(Storage), nameof(Storage.CheckUseRequirements))]
        static class OpenCheck
        {
            static void Prefix(Storage __instance) => t_Checking = __instance;
            static void Finalizer() => t_Checking = null;
        }

        [HarmonyPatch(typeof(House), nameof(House.HasPermission))]
        static class Permission
        {
            static void Postfix(Player player, bool storage, ref bool __result)
            {
                if (__result || !storage || t_Checking == null || player == null) return;
                var rec = BankFor(t_Checking);
                if (rec != null && IsMember(player, rec)) __result = true;
            }
        }

        // ------------------------------------------------------------ only officers take out

        [HarmonyPatch(typeof(Player), nameof(Player.FindObject),
            new[] { typeof(ObjectGuid), typeof(Player.SearchLocations), typeof(Container), typeof(Container), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
        static class Withdraw
        {
            static void Postfix(Player __instance, Player.SearchLocations searchLocations, ref WorldObject __result,
                ref Container foundInContainer, ref Container rootOwner)
            {
                if (!(rootOwner is Storage s) || __result == null || ReferenceEquals(__result, s)) return;
                if (searchLocations == Player.SearchLocations.Everywhere
                    || (searchLocations & Player.SearchLocations.LastUsedContainer) == 0) return;
                var rec = BankFor(s);
                if (rec == null || CanWithdraw(__instance, rec)) return;

                __result = null;
                foundInContainer = null;
                rootOwner = null;
                double now = ACE.Common.Time.GetUnixTime();
                lock (s_LastRefusal)
                {
                    if (s_LastRefusal.TryGetValue(__instance.Guid.Full, out var at) && now - at < 2) return;
                    s_LastRefusal[__instance.Guid.Full] = now;
                }
                string who = rec.Rank <= 1 ? "an officer of your allegiance" : $"an officer of level {rec.Rank} or higher";
                __instance.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(__instance.Session,
                    $"Only {who} or the monarch may take from the allegiance bank."));
            }
        }

        // ------------------------------------------------------------ the log

        static Dictionary<uint, (string, int)> Contents(Container c)
        {
            var d = new Dictionary<uint, (string, int)>();
            foreach (var wo in c.Inventory.Values.ToList())
            {
                d[wo.Guid.Full] = (wo.Name, wo.StackSize ?? 1);
                if (wo is Container sub && !(wo is Storage))
                    foreach (var kv in Contents(sub)) d[kv.Key] = kv.Value;
            }
            return d;
        }

        internal static void TakeSnapshot(Storage s, Player opener)
        {
            s_Open.Remove(s);
            s_Open.Add(s, new Snap { Opener = opener.Guid.Full, OpenerName = opener.Name, Items = Contents(s) });
        }

        [HarmonyPatch(typeof(Chest), nameof(Chest.Open))]
        static class Opened
        {
            static void Postfix(Chest __instance, Player player)
            {
                if (!(__instance is Storage s) || player == null) return;
                try
                {
                    var rec = BankFor(s);
                    if (rec == null) return;
                    Settle(s, rec);                   // a close that never reached FinishClose
                    TakeSnapshot(s, player);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionBank open: {e}"); }
            }
        }

        [HarmonyPatch(typeof(Chest), nameof(Chest.FinishClose))]
        static class Closed
        {
            static void Postfix(Chest __instance)
            {
                if (!(__instance is Storage s)) return;
                try { var rec = BankFor(s); if (rec != null) Settle(s, rec); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionBank close: {e}"); }
            }
        }

        static string Describe(IEnumerable<(string Name, int Count)> items) =>
            string.Join(", ", items.Select(i => i.Count > 1 ? $"{i.Count:N0} {i.Name}" : i.Name));

        static void Settle(Storage s, BankRec rec)
        {
            if (!s_Open.TryGetValue(s, out var snap)) return;
            s_Open.Remove(s);
            var now = Contents(s);
            var took = new List<(string, int)>();
            var left = new List<(string, int)>();
            foreach (var kv in snap.Items)
            {
                if (!now.TryGetValue(kv.Key, out var n)) took.Add(kv.Value);
                else if (n.Item2 < kv.Value.Count) took.Add((kv.Value.Name, kv.Value.Count - n.Item2));
            }
            foreach (var kv in now)
            {
                if (!snap.Items.TryGetValue(kv.Key, out var o)) left.Add(kv.Value);
                else if (kv.Value.Item2 > o.Count) left.Add((kv.Value.Item1, kv.Value.Item2 - o.Count));
            }
            string stamp = DateTime.UtcNow.ToString("MM/dd HH:mm");
            if (left.Count > 0)
                Store.BankLogAdd(s.Guid.Full, $"{stamp} {snap.OpenerName} put in {Describe(left)}");
            if (took.Count > 0)
            {
                string what = Describe(took);
                Store.BankLogAdd(s.Guid.Full, $"{stamp} {snap.OpenerName} took {what}");
                Mod.Log.Info($"[RevivalGuard] MansionBank: {snap.OpenerName} took {what} from 0x{s.Guid.Full:X8}");
                foreach (var m in Mansions.Defenders(rec.Owner))
                    Mansions.Say(m, $"[Allegiance Bank] {snap.OpenerName} took {what}.", ChatMessageType.Allegiance);
            }
        }

        // ------------------------------------------------------------ /mansion bank

        internal static void Command(Player p, string[] args)
        {
            string sub = args.Length > 0 ? args[0] : "";
            if (sub == "log") { Log(p); return; }
            var house = Mansions.OwnedMansion(p, quiet: sub == "");
            if (house == null)
            {
                if (sub == "") Log(p);
                return;
            }
            if (sub == "on")
            {
                var s = p.CurrentLandblock?.GetObject(p.LastOpenedContainerId) as Storage;
                if (s == null || !s.IsOpen || s.Viewer != p.Guid.Full)
                { Mansions.Say(p, "Open the storage chest that should be the allegiance bank, then type /mansion bank on."); return; }
                if (s.House?.RootHouse?.Guid.Full != house.Guid.Full)
                { Mansions.Say(p, "That chest is not in your mansion."); return; }
                var old = Store.BankOfHouse(house.Guid.Full);
                Store.SetBank(s.Guid.Full, new BankRec { House = house.Guid.Full, Owner = p.Guid.Full, Rank = old?.Rank ?? 1 });
                TakeSnapshot(s, p);
                Mansions.Say(p, $"This chest is now your allegiance bank. Every member may put things in; officers of level {old?.Rank ?? 1} and up and you may take them out. /mansion bank rank 1-3 changes that.");
                return;
            }
            var bankGuid = Store.BankGuidOfHouse(house.Guid.Full);
            var rec = bankGuid != 0 ? Store.Bank(bankGuid) : null;
            if (sub == "off")
            {
                if (rec == null) { Mansions.Say(p, "Your mansion has no allegiance bank."); return; }
                Store.SetBank(bankGuid, null);
                Mansions.Say(p, "That chest is ordinary storage again; only you and your storage guests may open it.");
                return;
            }
            if (sub == "rank")
            {
                if (rec == null) { Mansions.Say(p, "Your mansion has no allegiance bank. Open a storage chest and type /mansion bank on."); return; }
                if (args.Length < 2 || !int.TryParse(args[1], out var r) || r < 1 || r > 3)
                { Mansions.Say(p, "Usage: /mansion bank rank <1-3>, the lowest officer level that may withdraw."); return; }
                rec.Rank = r;
                Store.SetBank(bankGuid, rec);
                Mansions.Say(p, $"Officers of level {r} and up may now withdraw from the allegiance bank.");
                return;
            }
            Mansions.Say(p, rec != null
                ? $"Your allegiance bank is set; officers of level {rec.Rank} and up may withdraw. /mansion bank log shows who took what."
                : "Your mansion has no allegiance bank. Open a storage chest and type /mansion bank on.");
        }

        static void Log(Player p)
        {
            uint monarch = p.Allegiance?.MonarchId ?? p.Guid.Full;
            uint root = PlayerManager.FindByGuid(monarch)?.HouseInstance ?? 0;
            uint bank = root != 0 ? Store.BankGuidOfHouse(root) : 0;
            if (bank == 0) { Mansions.Say(p, "Your allegiance has no bank."); return; }
            var lines = Store.BankLog(bank, 15);
            if (lines.Count == 0) { Mansions.Say(p, "Nothing has gone in or out of the allegiance bank yet."); return; }
            Mansions.Say(p, "The allegiance bank, most recent last (UTC):");
            foreach (var l in lines) Mansions.Say(p, "  " + l);
        }
    }
}
