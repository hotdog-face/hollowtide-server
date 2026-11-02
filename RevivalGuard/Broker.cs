using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using static RevivalGuard.BrokerRule;

namespace RevivalGuard
{
    /// <summary>
    /// THE MARKETPLACE BROKER (owner 2026-10-31, player request #19 "livelier marketplace"). Ours, not retail.
    /// Joe the Sho (first cast: Vessik the Sclavus; weenie 903100, placed by datafix 2026-11-03-marketplace-broker.sql) sells players' goods for
    /// them while they are offline, through retail's own vendor window. docs/MARKETPLACE-BROKER.md has the design,
    /// the rules (BrokerRule) and the failure handling. Off unless `@modifybool rg_broker true`.
    ///
    ///   LIST     give him an item (drag it onto him), use it on him, or drop it on his Sell list. He names a price (the
    ///            item's value) and asks Yes/No; say a number near him and click No to be asked again at that price.
    ///            Yes: the fee is paid, the item goes into his Strongbox (903101, a Container in the block that ACE
    ///            saves like house storage) and the listing is written.
    ///   BUY      his vendor window shows every open listing as stock at its seller's price (Value is set for the
    ///            length of the ApproachVendor message only). Buying moves the real item; coin and notes pay
    ///            (NoteChange). A seller's own listing shows at 1 and is taken back free: that is the cancel.
    ///   COLLECT  using him pays what you are owed (notes and coin) and hands back what expired.
    /// The items in the Strongbox are never left in Vendor.UniqueItemsForSale: Vendor.Destroy (a landblock unload)
    /// destroys that list's objects and their database rows. They are put there for one ApproachVendor and taken out.
    /// </summary>
    public static class Broker
    {
        internal const string P_ON = "rg_broker";
        internal static uint BROKER_WCID = 903100, VAULT_WCID = 903101, VAULT_GUID = 0x7016C001;
        const string FILE = "broker.json", LOG = "broker-log.txt";

        static readonly object s_Lock = new object();
        static Ledger s_Ledger;
        static bool s_Reconciled;
        static readonly HashSet<uint> s_Unclaimed = new HashSet<uint>();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        internal sealed class Offer { public uint Item, Npc; public long Price, Fee; public double At; public long? Next; public bool Asking, Boxed; }
        static readonly Dictionary<uint, Offer> s_Offers = new Dictionary<uint, Offer>();

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } }
        }

        internal static bool IsBroker(WorldObject wo) => wo is Vendor && wo.WeenieClassId == BROKER_WCID;

        public static void Register()
        {
            BROKER_WCID = (uint)Tunables.L("broker.wcid", BROKER_WCID);
            VAULT_WCID = (uint)Tunables.L("broker.vault_wcid", VAULT_WCID);
            VAULT_GUID = (uint)Tunables.L("broker.vault_guid", VAULT_GUID);
            CommandManager.TryAddCommand(HandlePrice, "brokerprice", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Sent by the client's price box for the Marketplace Broker.", "0x<item> <price, 0 = cancel>");
            CommandManager.TryAddCommand(HandleAdmin, "broker", AccessLevel.Admin, CommandHandlerFlag.None,
                "The Marketplace Broker: status, listings, the sink.", "[list <name> | expireall | log]");
            lock (s_Lock) Load();
            Mod.Log.Info($"[RevivalGuard] Broker: {(On ? "ON" : "off")} ({P_ON}); {s_Ledger.Listings.Count(x => x.Open)} open listings, "
                         + $"{s_Ledger.Owed.Count} sellers owed; @broker");
        }

        // ------------------------------------------------------------------ ledger file and log
        static string PathOf(string f) => Path.Combine(ModFolder.Path(), f);

        static void Load()
        {
            s_Ledger = new Ledger();
            try
            {
                var p = PathOf(FILE);
                if (File.Exists(p)) s_Ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(p), s_Json) ?? new Ledger();
            }
            catch (Exception e)
            {   // never start empty over a ledger we could not read: that would orphan every listing
                Mod.Log.Error($"[RevivalGuard] Broker: could not read {FILE}: {e.Message}; the Broker stays shut until it is fixed");
                s_Ledger = null;
            }
        }

        /// <summary>Through a temp file and a rename: a crash leaves the old ledger or the new, never half of one.</summary>
        static bool Save()
        {
            if (s_Ledger == null) return false;
            try
            {
                Prune(s_Ledger, Now());
                var p = PathOf(FILE); var tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Ledger, s_Json));
                File.Move(tmp, p, overwrite: true);
                return true;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker: could not write {FILE}: {e.Message}"); return false; }
        }

        /// <summary>Every list, buy, cancel, return, collect and repair: broker-log.txt (append, one line) and the audit trail.</summary>
        static void Log(string kind, string who, string text)
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\t{kind}\t{who}\t{text}";
            try { File.AppendAllText(PathOf(LOG), line + Environment.NewLine); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker log: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] Broker {kind}: {who} {text}");
            try { AuditTrail.Record("broker", who, "Marketplace", $"{kind} {text}"); } catch { }
        }

        static long Now() => (long)ACE.Common.Time.GetUnixTime();
        static string Nm(WorldObject wo) => wo?.NameWithMaterial ?? wo?.Name ?? "item";
        static string G(uint g) => $"0x{g:X8}";

        // ------------------------------------------------------------------ talking
        static void Tell(Player p, WorldObject npc, string text)
        {
            if (p?.Session == null || string.IsNullOrEmpty(text)) return;
            if (npc != null) p.Session.Network.EnqueueSend(new GameEventTell(npc, text, p, ChatMessageType.Tell));
            else p.Session.Network.EnqueueSend(new GameMessageSystemChat($"{WHO_FULL} tells you, \"{text}\"", ChatMessageType.Tell));
        }

        static void Fail(Player p, uint item = 0)
        {
            if (p?.Session == null) return;
            p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item != 0 ? item : p.Guid.Full));
        }

        // ------------------------------------------------------------------ the Strongbox
        /// <summary>The Strongbox beside this Broker, saved to the shard (so it and its contents persist), its contents
        /// loaded and the ledger reconciled against them once per run. Null when it is not ready (say so and stop).</summary>
        static Container Vault(WorldObject npc)
        {
            var v = npc?.CurrentLandblock?.GetObject(new ObjectGuid(VAULT_GUID)) as Container;
            if (v == null || v.WeenieClassId != VAULT_WCID) { Mod.Log.Error($"[RevivalGuard] Broker: no Strongbox {G(VAULT_GUID)} (wcid {VAULT_WCID}) beside {npc?.Name}"); return null; }
            if (!v.BiotaOriginatedFromOrHasBeenSavedToDatabase()) { v.SaveBiotaToDatabase(); Log("vault", "-", $"Strongbox {G(VAULT_GUID)} saved to the shard for the first time"); }
            if (!v.InventoryLoaded) return null;
            if (s_Ledger == null) return null;
            if (!s_Reconciled) { s_Reconciled = true; Repair(v); }
            if (On) { var plan = SeedPlan(s_Ledger); if (plan.pull.Count + plan.reprice.Count + plan.add.Count > 0) SeedNow(v); }
            foreach (var x in OffRule(s_Ledger)) { x.State = State.Expired; Log("off-rule", x.SellerName, $"{x.ItemName} {G(x.Item)} at {x.Price:N0} is not whole MMDs; held for the seller"); }
            foreach (var x in Expire(s_Ledger, Now())) Log("expire", x.SellerName, $"{x.ItemName} {G(x.Item)} unsold after {LIST_SECONDS / 86400} days; held for the seller");
            Save();
            return v;
        }

        /// <summary>After a restart: the ledger from where the items are (BrokerRule.Reconcile).</summary>
        static void Repair(Container v)
        {
            Where WhereIs(Listing x)
            {
                if (v.Inventory.ContainsKey(new ObjectGuid(x.Item))) return Where.InVault;
                var seller = x.Seller == HOUSE ? null : PlayerManager.GetOnlinePlayer(x.Seller);
                if (seller?.FindObject(x.Item, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems) != null) return Where.WithSeller;
                try
                {
                    var b = DatabaseManager.Shard.BaseDatabase.GetBiota(x.Item, true);
                    if (b == null) return Where.Missing;
                    uint Owner(ACE.Database.Models.Shard.Biota bb) =>
                        bb.BiotaPropertiesIID.FirstOrDefault(i => i.Type == (ushort)PropertyInstanceId.Container)?.Value
                        ?? bb.BiotaPropertiesIID.FirstOrDefault(i => i.Type == (ushort)PropertyInstanceId.Wielder)?.Value ?? 0;
                    uint c = Owner(b);
                    if (c == x.Seller && c != 0) return Where.WithSeller;
                    if (c != 0 && c != VAULT_GUID)
                    {   // in a side pack: whose pack?
                        var pack = DatabaseManager.Shard.BaseDatabase.GetBiota(c, true);
                        if (pack != null && Owner(pack) == x.Seller && x.Seller != 0) return Where.WithSeller;
                    }
                    return c == VAULT_GUID ? Where.InVault : Where.Elsewhere;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker repair {G(x.Item)}: {e.Message}"); return Where.Elsewhere; }
            }
            var (fixes, unclaimed) = Reconcile(s_Ledger, WhereIs, v.Inventory.Keys.Select(k => k.Full).ToList());
            foreach (var f in fixes) Log("repair", f.Listing.SellerName, $"{f.Listing.ItemName} {G(f.Listing.Item)} {f.From} -> {f.To}: {f.Why}");
            foreach (var g in unclaimed) { s_Unclaimed.Add(g); Log("unclaimed", "-", $"{G(g)} is in the Strongbox but no open listing names it; held back for staff"); }
            Save();
        }

        /// <summary>Brings the house listings in line with BrokerRule.SeedStock (SeedPlan): pull, reprice, add. The ledger is
        /// written before any item moves, as everywhere else.</summary>
        static void SeedNow(Container v)
        {
            long now = Now();
            var (pull, reprice, add) = SeedPlan(s_Ledger);
            // 1. pulled: the listing closes first, then the item leaves the Strongbox and is destroyed
            foreach (var x in pull) Close(x, State.Gone, now);
            foreach (var (x, price) in reprice) { Log("seed-price", "-", $"{x.ItemName} {G(x.Item)} {x.Price:N0} -> {price:N0}"); x.Price = price; }
            if (pull.Count + reprice.Count > 0 && !Save()) return;
            foreach (var x in pull)
            {
                if (v.TryRemoveFromInventory(new ObjectGuid(x.Item), out var gone) && gone != null) gone.Destroy();
                Log("seed-pull", "-", $"{x.ItemName} {G(x.Item)} at {x.Price:N0}: no longer in the seed list");
            }
            // 2. added: made, listed (ledger written), then put in the Strongbox and saved
            var made = new List<WorldObject>();
            foreach (var z in add)
            {
                var wo = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(z.Wcid);
                if (wo == null) { Mod.Log.Warn($"[RevivalGuard] Broker seed: no weenie {z.Wcid}"); continue; }
                if ((wo.MaxStackSize ?? 1) > 1 || (wo.Attuned ?? 0) != 0 || (wo.Bonded ?? 0) != 0) { wo.Destroy(); continue; }
                Add(s_Ledger, wo.Guid.Full, wo.WeenieClassId, Nm(wo), HOUSE, "", z.Price, 0, now - Random.Shared.Next(3600, 5 * 86400));
                made.Add(wo);
            }
            if (made.Count == 0) { s_Ledger.Seeded = true; Save(); return; }
            var before = new List<uint>(s_Ledger.SeedsPlaced);
            s_Ledger.Seeded = true;
            foreach (var wo in made) if (!s_Ledger.SeedsPlaced.Contains(wo.WeenieClassId)) s_Ledger.SeedsPlaced.Add(wo.WeenieClassId);
            if (!Save()) { foreach (var wo in made) { s_Ledger.Listings.RemoveAll(x => x.Item == wo.Guid.Full); wo.Destroy(); } s_Ledger.SeedsPlaced = before; return; }
            foreach (var wo in made)
            {
                if (v.TryAddToInventory(wo)) { wo.SaveBiotaToDatabase(); Log("seed", "-", $"{Nm(wo)} {G(wo.Guid.Full)} at {Find(s_Ledger, wo.Guid.Full)?.Price:N0}"); }
                else { Find(s_Ledger, wo.Guid.Full)?.IfAny(x => Close(x, State.Gone, now)); wo.Destroy(); }
            }
            Save();
        }

        static void IfAny<T>(this T x, Action<T> a) where T : class { if (x != null) a(x); }

        // ------------------------------------------------------------------ listing
        static ItemFacts Facts(Player p, WorldObject item, bool equipped) => new ItemFacts
        {
            Mercenary = Mercenaries.IsMerc(p),
            Money = item.WeenieType == WeenieType.Coin || NoteChange.IsNote(item.WeenieClassId),
            Attuned = (item.Attuned ?? AttunedStatus.Normal) != AttunedStatus.Normal || item.IsStickyAttunedOrContainsStickyAttuned,
            Bonded = (item.Bonded ?? BondedStatus.Normal) != BondedStatus.Normal,
            Unsellable = !item.IsSellable,
            Retained = item.Retained,
            Locked = ItemLock.IsLocked(item),
            Equipped = equipped,
            Stackable = (item.MaxStackSize ?? 1) > 1,
            ContainerWithItems = item is Container c && c.Inventory.Count > 0,
            PetOut = item is PetDevice pd && pd.Pet != null,
            Trading = p.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(p.ItemsInTradeWindow),
        };

        /// <summary>An item handed to him (give, use-on, or his Sell list): check it, name a price, ask.</summary>
        internal static void OfferItem(Player p, Vendor npc, uint itemGuid)
        {
            if (p?.Session == null || npc == null) return;
            if (!On || s_Ledger == null) { Tell(p, npc, ClosedLine); Fail(p, itemGuid); return; }
            var item = p.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems, out _, out _, out bool equipped);
            if (item == null) { Fail(p, itemGuid); return; }
            Fail(p, itemGuid);   // the client's drag snaps back: the item stays in the pack until Yes
            int open; lock (s_Lock) open = OpenCount(s_Ledger, p.Guid.Full);
            var why = Check(Facts(p, item, equipped), open);
            if (why != Refusal.None) { Tell(p, npc, RefusalLine(why)); return; }
            long price = Suggest(item.Value ?? 0);
            lock (s_Lock) s_Offers[p.Guid.Full] = new Offer { Item = itemGuid, Npc = npc.Guid.Full, Price = price, Fee = Fee(price), At = ACE.Common.Time.GetUnixTime() };
            // OUR CLIENT ASKS FOR THE PRICE IN ITS OWN BOX (owner 2026-10-31: "the player should set their own price"):
            // AcBrokerPriceHud, answered with @brokerprice. Another client gets the Yes/No and the price said out loud.
            if (ModChannel.Capable(p.Session))
            {
                ModChannel.Send(p.Session, "brokerprice", JsonSerializer.Serialize(new
                {
                    g = itemGuid, n = Nm(item), v = price, max = PRICE_MAX, fee = FEE_PCT, feeMin = FEE_MIN, feeMax = FEE_MAX,
                    cut = CUT_PCT, days = LIST_SECONDS / 86400, mmd = MMD, who = WHO, mmdOnly = true,
                }));
                lock (s_Lock) s_Offers[p.Guid.Full].Boxed = true;
                Tell(p, npc, BoxLine);
                return;
            }
            Tell(p, npc, OfferLine(Nm(item), price));
            Ask(p, npc);
        }

        static void Ask(Player p, WorldObject npc)
        {
            Offer o; lock (s_Lock) if (!s_Offers.TryGetValue(p.Guid.Full, out o)) return;
            var item = p.FindObject(o.Item, Player.SearchLocations.MyInventory);
            if (item == null) { lock (s_Lock) s_Offers.Remove(p.Guid.Full); return; }
            o.Fee = Fee(o.Price);
            if (!p.ConfirmationManager.EnqueueSend(new BrokerConfirmation(p.Guid), ConfirmText(Nm(item), o.Price, o.Fee)))
            { Tell(p, npc, "Answer your other question first, then hand it to me again."); lock (s_Lock) s_Offers.Remove(p.Guid.Full); return; }
            o.Asking = true;
        }

        /// <summary>The seller's answer. Yes lists it; No (or no answer) ends the offer unless a new price was said.</summary>
        internal static void Answered(Player p, bool yes)
        {
            if (p == null) return;
            Offer o; lock (s_Lock) { if (!s_Offers.TryGetValue(p.Guid.Full, out o)) return; o.Asking = false; }
            var npc = p.CurrentLandblock?.GetObject(o.Npc) as Vendor;
            if (!yes)
            {
                if (o.Next.HasValue && npc != null) { o.Price = o.Next.Value; o.Next = null; Ask(p, npc); return; }
                lock (s_Lock) s_Offers.Remove(p.Guid.Full);
                return;
            }
            lock (s_Lock) s_Offers.Remove(p.Guid.Full);
            if (npc == null || p.Location == null || npc.Location == null || p.Location.DistanceTo(npc.Location) > SAY_RANGE * 2)
            { Tell(p, null, "Come back to my table to finish that deal."); return; }
            ListItem(p, npc, o);
        }

        static void ListItem(Player p, Vendor npc, Offer o)
        {
            var v = Vault(npc);
            if (v == null) { Tell(p, npc, "One moment, friend. My strongbox is stuck. Try again shortly."); return; }
            var item = p.FindObject(o.Item, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems, out _, out _, out bool equipped);
            if (item == null) return;
            long now = Now();
            lock (s_Lock)
            {
                var why = Check(Facts(p, item, equipped), OpenCount(s_Ledger, p.Guid.Full));   // again: things change in 30 s
                if (why != Refusal.None) { Tell(p, npc, RefusalLine(why)); return; }
                if (!ValidPrice(o.Price)) { Tell(p, npc, WholeMmdLine); return; }   // whole MMDs only, whatever the client sent
                if ((p.CoinValue ?? 0) + NoteChange.NoteValue(p, 0) < o.Fee) { Tell(p, npc, NoFeeLine(o.Fee)); return; }
                // 1. the ledger first
                Add(s_Ledger, item.Guid.Full, item.WeenieClassId, Nm(item), p.Guid.Full, p.Name, o.Price, o.Fee, now);
                if (!Save()) { s_Ledger.Listings.RemoveAll(x => x.Item == item.Guid.Full && x.Listed == now); Tell(p, npc, ClosedLine); return; }
            }
            // 2. the fee, then the item into the Strongbox
            NoteChange.t_Change = 0;
            bool paid = o.Fee <= 0 || NoteChange.Pay(p, o.Fee);
            long change = NoteChange.t_Change; NoteChange.t_Change = 0;
            NoteChange.Recount(p);
            // Container.TryRemoveFromInventory, not ...WithNetworking: that one DeepSaves the item with no container, a
            // second database write a crash could stop between (the item would belong to nothing). One save, below.
            WorldObject wo = null;
            bool removed = paid && p.TryRemoveFromInventory(item.Guid, out wo) && wo != null;
            if (removed)
            {
                p.Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo),
                    new GameMessagePrivateUpdatePropertyInt(p, PropertyInt.EncumbranceVal, p.EncumbranceVal ?? 0));
                if (wo.WeenieType == WeenieType.Container) NoteChange.Recount(p);
            }
            if (!removed)
            {
                lock (s_Lock) { var x = Find(s_Ledger, item.Guid.Full); if (x != null) { s_Ledger.SinkTotal -= x.Fee; s_Ledger.Listings.Remove(x); } Save(); }
                if (paid && o.Fee > 0) NoteChange.GiveChange(p, o.Fee);   // the fee back: nothing was listed
                if (change > 0) NoteChange.GiveChange(p, change);
                Tell(p, npc, "Something slipped. You still have it. Try again.");
                return;
            }
            if (change > 0) NoteChange.GiveChange(p, change);
            if (!v.TryAddToInventory(wo))
            {   // the Strongbox refused it (full): straight back to the seller, listing closed, fee refunded
                p.TryCreateInInventoryWithNetworking(wo);
                lock (s_Lock) { var x = Find(s_Ledger, wo.Guid.Full); if (x != null) { Close(x, State.Cancelled, now); s_Ledger.SinkTotal -= x.Fee; } Save(); }
                if (o.Fee > 0) NoteChange.GiveChange(p, o.Fee);
                Log("refused", p.Name, $"{Nm(wo)} {G(wo.Guid.Full)}: the Strongbox is full");
                Tell(p, npc, "My strongbox is full to the lid. Come back tomorrow.");
                return;
            }
            // 3. saved at once: the item's new container, then the seller (the fee)
            wo.SaveBiotaToDatabase();
            p.SavePlayerToDatabase();
            Log("list", p.Name, $"{Nm(wo)} {G(wo.Guid.Full)} wcid {wo.WeenieClassId} at {o.Price:N0}, fee {o.Fee:N0}");
            Tell(p, npc, ListedLine(Nm(wo), o.Price));
        }

        // ------------------------------------------------------------------ the window
        sealed class Shown { public WorldObject Item; public int? Value; public string Name; }

        internal static List<object> Fill(Vendor npc, Player viewer)
        {
            var shown = new List<object>();
            if (!On) return shown;
            // our client prices this vendor's window in MMD (owner ticket 261031-0012); sent before the window's own message
            if (viewer?.Session != null && ModChannel.Capable(viewer.Session))
                ModChannel.Send(viewer.Session, "brokershop", $"{{\"g\":{npc.Guid.Full}}}");
            var v = Vault(npc);
            if (v == null) return shown;
            List<Listing> open; lock (s_Lock) open = s_Ledger.Listings.Where(x => x.State == State.Listed).ToList();
            foreach (var x in open)
            {
                if (!v.Inventory.TryGetValue(new ObjectGuid(x.Item), out var wo) || s_Unclaimed.Contains(x.Item)) continue;
                bool mine = viewer != null && x.Seller == viewer.Guid.Full && x.Seller != HOUSE;
                shown.Add(new Shown { Item = wo, Value = wo.Value, Name = wo.Name });
                wo.Value = mine ? 0 : (int)Math.Min(int.MaxValue, x.Price);
                // the window prices in pyreals only; an MMD price also reads in the row's name ("Olthoi Helm (3 MMD)")
                if (mine) wo.Name = wo.Name + " (yours)";
                else if (x.Price >= MMD) wo.Name = wo.Name + " (" + PriceText(x.Price) + ")";
                npc.UniqueItemsForSale[wo.Guid] = wo;
            }
            return shown;
        }

        internal static void Unfill(Vendor npc, List<object> shown)
        {
            if (shown == null) return;
            foreach (Shown s in shown)
            {
                s.Item.Value = s.Value;
                s.Item.Name = s.Name;
                npc.UniqueItemsForSale.Remove(s.Item.Guid);
            }
        }

        // ------------------------------------------------------------------ buying (and taking your own back)
        internal static void Buy(Player p, Vendor npc, List<ItemProfile> items)
        {
            void Done(bool ok) { if (!ok) Fail(p); p.SendUseDoneEvent(); }
            if (!On || s_Ledger == null) { Tell(p, npc, ClosedLine); Done(false); return; }
            var v = Vault(npc);
            if (v == null || items == null || items.Count == 0) { Done(false); return; }
            var picks = new List<(Listing x, WorldObject wo)>();
            lock (s_Lock)
                foreach (var ip in items)
                {
                    var x = Find(s_Ledger, ip.ObjectGuid);
                    if (x == null || x.State != State.Listed || !v.Inventory.TryGetValue(new ObjectGuid(ip.ObjectGuid), out var wo) || s_Unclaimed.Contains(ip.ObjectGuid))
                    { Tell(p, npc, "Too slow, friend. That piece is gone."); npc.ApproachVendor(p); Done(false); return; }
                    if (picks.Any(q => q.x == x)) continue;
                    picks.Add((x, wo));
                }
            var receive = new ItemsToReceive(p);
            foreach (var (_, wo) in picks) receive.Add(wo.WeenieClassId, 1);
            if (receive.PlayerExceedsLimits) { Tell(p, npc, PackFullLine); Done(false); return; }
            var paidFor = picks.Where(q => q.x.Seller != p.Guid.Full || q.x.Seller == HOUSE).ToList();
            if (!p.CheckUniques(paidFor.Select(q => q.wo).ToList(), npc)) { Done(false); return; }
            long total = paidFor.Sum(q => q.x.Price);
            if ((p.CoinValue ?? 0) + NoteChange.NoteValue(p, 0) < total) { Tell(p, npc, "Your purse is lighter than your taste, friend."); Done(false); return; }

            long now = Now();
            // 1. the ledger first: sold (credit to the seller) or taken back
            lock (s_Lock)
            {
                foreach (var (x, _) in picks)
                    if (paidFor.Any(q => q.x == x)) BrokerRule.Sell(s_Ledger, x, p.Guid.Full, p.Name, now);
                    else Close(x, State.Cancelled, now);
                if (!Save()) { Mod.Log.Error("[RevivalGuard] Broker: ledger not written; buy refused"); Load(); Done(false); return; }
            }
            // 2. the items
            var moved = new List<(Listing x, WorldObject wo)>();
            foreach (var (x, wo) in picks)
            {
                if (v.TryRemoveFromInventory(wo.Guid, out var taken) && p.TryCreateInInventoryWithNetworking(taken)) { moved.Add((x, taken)); continue; }
                if (taken != null && taken.ContainerId != p.Guid.Full) v.TryAddToInventory(taken);   // back where it was
                lock (s_Lock) Undo(x);
                Log("buy-failed", p.Name, $"{x.ItemName} {G(x.Item)} could not move; listing restored");
            }
            // 3. the money for what moved
            long owe = moved.Where(m => paidFor.Any(q => q.x == m.x)).Sum(m => m.x.Price);
            NoteChange.t_Change = 0;
            if (owe > 0 && !NoteChange.Pay(p, owe)) Mod.Log.Error($"[RevivalGuard] Broker: {p.Name} could not pay {owe:N0} after the check passed");
            long change = NoteChange.t_Change; NoteChange.t_Change = 0;
            if (change > 0) NoteChange.GiveChange(p, change);
            NoteChange.Recount(p);
            // 4. saved at once, in this order on the database queue: the items, then the buyer (coin and items)
            foreach (var (_, wo) in moved) wo.SaveBiotaToDatabase();
            p.SavePlayerToDatabase();
            lock (s_Lock) Save();
            foreach (var (x, wo) in moved)
            {
                if (x.State == State.Cancelled) { Log("cancel", p.Name, $"took back {x.ItemName} {G(x.Item)}"); Tell(p, npc, CancelLine); continue; }
                Log("buy", p.Name, $"{x.ItemName} {G(x.Item)} for {x.Price:N0} from {(x.Seller == HOUSE ? "the house" : x.SellerName)}; seller credited {x.Credited:N0}");
                if (x.Seller == HOUSE) continue;
                var seller = PlayerManager.GetOnlinePlayer(x.Seller);
                if (seller?.Session != null) Tell(seller, null, SoldTell(x.ItemName, x.Price, x.Credited));
                else lock (s_Lock) { if (!s_Ledger.Notices.TryGetValue(x.Seller, out var n)) s_Ledger.Notices[x.Seller] = n = new List<string>(); n.Add(x.ItemName); Save(); }
            }
            p.Session.Network.EnqueueSend(new GameMessageSound(p.Guid, Sound.PickUpItem));
            npc.ApproachVendor(p, VendorType.Buy);
            Done(moved.Count > 0);
        }

        static void Undo(Listing x)
        {
            if (x.State == State.Sold && x.Credited > 0)
            {
                long owed = s_Ledger.Owed.GetValueOrDefault(x.Seller) - x.Credited;
                if (owed > 0) s_Ledger.Owed[x.Seller] = owed; else s_Ledger.Owed.Remove(x.Seller);
            }
            if (x.State == State.Sold) s_Ledger.SinkTotal -= x.Price - x.Credited;
            x.State = State.Listed; x.Credited = 0; x.Buyer = 0; x.BuyerName = null; x.Done = 0;
            Save();
        }

        // ------------------------------------------------------------------ collecting
        /// <summary>On use, before the window opens: what he owes you, and what expired.</summary>
        internal static void Collect(Player p, Vendor npc)
        {
            if (!On || p?.Session == null || s_Ledger == null) return;
            var v = Vault(npc);
            if (v == null) return;
            // Paid as MMD notes for whole MMDs and pyreals for the rest (owner 2026-10-31), only what fits in the pack:
            // all of it, else the MMDs alone, else nothing and the balance stays held.
            long owed; lock (s_Lock) owed = s_Ledger.Owed.GetValueOrDefault(p.Guid.Full);
            if (owed > 0)
            {
                var (m, rest, _) = Payout(owed);
                long cap = Fits(p, m, rest) ? owed : m > 0 && Fits(p, m, 0) ? m * MMD : 0;
                long take = 0;
                if (cap > 0) lock (s_Lock) { take = BrokerRule.Collect(s_Ledger, p.Guid.Full, cap); if (take > 0 && !Save()) { s_Ledger.Owed[p.Guid.Full] = owed; take = 0; } }
                if (take > 0)
                {
                    long unpaid = PayOut(p, take);
                    if (unpaid > 0) lock (s_Lock) { s_Ledger.Owed[p.Guid.Full] = s_Ledger.Owed.GetValueOrDefault(p.Guid.Full) + unpaid; Save(); }
                    NoteChange.Recount(p);
                    p.SavePlayerToDatabase();
                    Log("collect", p.Name, $"{take - unpaid:N0} pyreals ({PriceText(take - unpaid)}){(unpaid > 0 ? $", {unpaid:N0} could not be placed and stays held" : "")}");
                    if (take - unpaid > 0) Tell(p, npc, CollectLine(take - unpaid));
                }
                long left; lock (s_Lock) left = s_Ledger.Owed.GetValueOrDefault(p.Guid.Full);
                if (left > 0) Tell(p, npc, PackFullPayLine(left));
            }
            List<Listing> back; lock (s_Lock) back = s_Ledger.Listings.Where(x => x.Seller == p.Guid.Full && x.State == State.Expired).ToList();
            foreach (var x in back)
            {
                if (!v.Inventory.TryGetValue(new ObjectGuid(x.Item), out var wo)) continue;
                var r = new ItemsToReceive(p); r.Add(wo.WeenieClassId, 1);
                if (r.PlayerExceedsLimits) { Tell(p, npc, PackFullLine); break; }
                lock (s_Lock) { Close(x, State.Returned, Now()); Save(); }
                if (v.TryRemoveFromInventory(wo.Guid, out var taken) && p.TryCreateInInventoryWithNetworking(taken))
                {
                    taken.SaveBiotaToDatabase();
                    p.SavePlayerToDatabase();
                    Log("return", p.Name, $"{x.ItemName} {G(x.Item)} expired, handed back");
                    Tell(p, npc, ExpiredBackLine(x.ItemName));
                }
                else
                {
                    if (taken != null && taken.ContainerId != p.Guid.Full) v.TryAddToInventory(taken);
                    lock (s_Lock) { x.State = State.Expired; x.Done = 0; Save(); }
                }
            }
        }

        /// <summary>Whether m MMD notes and `pyreals` coin fit in the pack (slots and burden), as ACE checks a purchase.</summary>
        static bool Fits(Player p, long m, long pyreals)
        {
            var r = new ItemsToReceive(p);
            if (m > 0) r.Add(MMD_WCID, (int)Math.Min(int.MaxValue, m));
            if (pyreals > 0) r.Add(PYREAL_WCID, (int)pyreals);
            return !r.PlayerExceedsLimits;
        }

        /// <summary>Hands `amount` over as MMD notes and pyreals into the pack. Returns what could not be placed (never
        /// dropped on the ground: it goes back to what he holds for the seller).</summary>
        static long PayOut(Player p, long amount)
        {
            var (m, rest, _) = Payout(amount);
            long unpaid = 0;
            bool Give(uint wcid, int n)
            {
                var wo = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) return false;
                wo.SetStackSize(n);
                if (p.TryCreateInInventoryWithNetworking(wo)) return true;
                wo.Destroy();
                return false;
            }
            while (m > 0) { int n = (int)Math.Min(m, MMD_STACK); if (!Give(MMD_WCID, n)) { unpaid += m * MMD; m = 0; break; } m -= n; }
            while (rest > 0) { int n = (int)Math.Min(rest, PYREAL_STACK); if (!Give(PYREAL_WCID, n)) { unpaid += rest; break; } rest -= n; }
            return unpaid;
        }

        // ------------------------------------------------------------------ login notice
        static void Notify(Player p)
        {
            if (p?.Session == null || s_Ledger == null) return;
            int sold; long owed; int waiting;
            lock (s_Lock)
            {
                sold = s_Ledger.Notices.TryGetValue(p.Guid.Full, out var n) ? n.Count : 0;
                owed = s_Ledger.Owed.GetValueOrDefault(p.Guid.Full);
                waiting = s_Ledger.Listings.Count(x => x.Seller == p.Guid.Full && (x.State == State.Expired || (x.State == State.Listed && Now() >= x.Expires)));
                if (sold > 0) { s_Ledger.Notices.Remove(p.Guid.Full); Save(); }
            }
            var line = LoginNotice(sold, owed, waiting);
            if (line != null) p.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        // ------------------------------------------------------------------ admin
        static void HandleAdmin(Session session, params string[] args)
        {
            void Say(string t) { if (session?.Player != null) session.Network.EnqueueSend(new GameMessageSystemChat(t, ChatMessageType.Broadcast)); else Console.WriteLine(t); }
            if (s_Ledger == null) { Say($"Broker: {FILE} could not be read; shut. Fix or move it and restart."); return; }
            var a = args ?? Array.Empty<string>();
            lock (s_Lock)
            {
                if (a.Length > 0 && a[0] == "expireall")
                {
                    int n = 0; foreach (var x in s_Ledger.Listings.Where(x => x.State == State.Listed && x.Seller != HOUSE)) { x.State = State.Expired; n++; }
                    Save(); Log("admin", session?.Player?.Name ?? "console", $"expireall: {n} listings held for their sellers");
                    Say($"Broker: {n} listings expired; sellers take them back by using him."); return;
                }
                if (a.Length > 1 && a[0] == "list")
                {
                    var who = string.Join(" ", a.Skip(1));
                    foreach (var x in s_Ledger.Listings.Where(x => string.Equals(x.SellerName, who, StringComparison.OrdinalIgnoreCase)).Take(30))
                        Say($"{x.State} {x.ItemName} {G(x.Item)} {x.Price:N0} listed {DateTimeOffset.FromUnixTimeSeconds(x.Listed):MMM d}");
                    return;
                }
                if (a.Length > 0 && a[0] == "log") { Say($"Broker log: {PathOf(LOG)}"); return; }
                Say($"Broker: {(On ? "ON" : "off")} ({P_ON}). Open {s_Ledger.Listings.Count(x => x.State == State.Listed)}, expired waiting "
                    + $"{s_Ledger.Listings.Count(x => x.State == State.Expired)}, owed {s_Ledger.Owed.Values.Sum():N0} to {s_Ledger.Owed.Count}, "
                    + $"sink {s_Ledger.SinkTotal:N0}, unclaimed in the Strongbox {s_Unclaimed.Count}. @broker list <name> | expireall | log");
            }
        }

        // ------------------------------------------------------------------ the price said out loud
        internal static void Heard(Player p, string message)
        {
            if (p == null || !On) return;
            Offer o; lock (s_Lock) if (!s_Offers.TryGetValue(p.Guid.Full, out o)) return;
            if (o.Boxed) return;   // the price box has the price
            if (ACE.Common.Time.GetUnixTime() - o.At > OFFER_SECONDS) { lock (s_Lock) s_Offers.Remove(p.Guid.Full); return; }
            var npc = p.CurrentLandblock?.GetObject(o.Npc);
            if (npc == null || p.Location == null || npc.Location == null || p.Location.DistanceTo(npc.Location) > SAY_RANGE) return;
            if (!TryParsePrice(message, out long price))
            {
                if (message.Any(char.IsDigit)) Tell(p, npc, BadPriceLine);
                return;
            }
            o.At = ACE.Common.Time.GetUnixTime();
            if (o.Asking) { o.Next = price; Tell(p, npc, $"{price:N0}? Click No on my question and I ask again at that."); return; }
            o.Price = price;
            Ask(p, npc);
        }

        /// <summary>The price box's answer: "@brokerprice 0x&lt;item&gt; &lt;price&gt;", 0 = cancelled. OK in the box is the
        /// seller's yes, so the item is listed at once (the fee and every rule are checked again in ListItem).</summary>
        static void HandlePrice(Session session, params string[] args)
        {
            var p = session?.Player;
            if (p == null || args == null || args.Length < 2 || !On) return;
            string g = args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[0].Substring(2) : args[0];
            if (!uint.TryParse(g, System.Globalization.NumberStyles.HexNumber, null, out uint item)) return;
            Offer o; lock (s_Lock) if (!s_Offers.TryGetValue(p.Guid.Full, out o) || o.Item != item) return;
            var npc = p.CurrentLandblock?.GetObject(o.Npc) as Vendor;
            if (args[1] == "0") { lock (s_Lock) s_Offers.Remove(p.Guid.Full); if (npc != null) Tell(p, npc, OfferTimeoutLine); return; }
            if (!TryBoxPrice(args[1], out long price)) { if (npc != null) Tell(p, npc, WholeMmdLine); lock (s_Lock) s_Offers.Remove(p.Guid.Full); return; }
            lock (s_Lock) s_Offers.Remove(p.Guid.Full);
            if (ACE.Common.Time.GetUnixTime() - o.At > OFFER_SECONDS * 5) { Tell(p, npc, OfferTimeoutLine); return; }
            if (npc == null || p.Location == null || npc.Location == null || p.Location.DistanceTo(npc.Location) > SAY_RANGE * 2)
            { Tell(p, null, "Come back to my table to finish that deal."); return; }
            o.Price = price; o.Fee = Fee(price);
            ListItem(p, npc, o);
        }

        internal sealed class BrokerConfirmation : Confirmation
        {
            public BrokerConfirmation(ObjectGuid player) : base(player, ConfirmationType.Yes_No) { }
            public override void ProcessConfirmation(bool response, bool timeout = false)
            {
                try { Answered(Player, response && !timeout); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker answer: {e}"); }
            }
        }

        // ================================================================== patches
        [HarmonyPatch(typeof(Vendor), nameof(Vendor.ActOnUse), new Type[] { typeof(WorldObject) })]
        static class UseBroker
        {
            static void Prefix(Vendor __instance, WorldObject wo)
            {
                try { if (IsBroker(__instance) && wo is Player p && !p.IsBusy) Collect(p, __instance); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker use: {e}"); }
            }
        }

        [HarmonyPatch(typeof(Vendor), nameof(Vendor.ApproachVendor))]
        static class ShowListings
        {
            static void Prefix(Vendor __instance, Player player, out List<object> __state)
            {
                __state = null;
                try { if (IsBroker(__instance)) __state = Fill(__instance, player); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker fill: {e}"); }
            }
            /// <summary>Always, whatever ApproachVendor did: the values and names back, the vendor list empty again.</summary>
            static Exception Finalizer(Exception __exception, Vendor __instance, List<object> __state)
            {
                try { Unfill(__instance, __state); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker unfill: {e}"); }
                return __exception;
            }
        }

        /// <summary>Retail's five-minute rot of resold items never touches a listing.</summary>
        [HarmonyPatch]
        static class NoRot
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Vendor), "RotUniques");
            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() != null) return true;
                Mod.Log.Error("[RevivalGuard] Broker: Vendor.RotUniques not found; listings would still be safe (they are only in the list during one message)");
                return false;
            }
            static bool Prefix(Vendor __instance) => !IsBroker(__instance);
        }

        /// <summary>Examining a listing in his window: the item is in the Strongbox, not in the vendor's list.</summary>
        [HarmonyPatch(typeof(Vendor), nameof(Vendor.TryGetItemForSale))]
        static class AppraiseListing
        {
            static void Postfix(Vendor __instance, ObjectGuid itemGuid, ref WorldObject itemForSale, ref bool __result)
            {
                try
                {
                    if (__result || !IsBroker(__instance) || !On || s_Ledger == null) return;
                    Listing x; lock (s_Lock) x = Find(s_Ledger, itemGuid.Full);
                    if (x == null || x.State != State.Listed) return;
                    var v = __instance.CurrentLandblock?.GetObject(new ObjectGuid(VAULT_GUID)) as Container;
                    if (v != null && v.Inventory.TryGetValue(itemGuid, out var wo)) { itemForSale = wo; __result = true; }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker appraise: {e.Message}"); }
            }
        }

        /// <summary>Who sells it (owner 2026-11-01): examining a listing in his window adds "Sold by <name>" at the end of the
        /// item's description (BrokerRule.SoldByLine). Server side, so every client shows it. Only items in the Strongbox
        /// with an open listing; the name is the seller character's current one (PlayerManager), none if it was deleted.</summary>
        [HarmonyPatch(typeof(ACE.Server.Network.Structure.AppraiseInfo), nameof(ACE.Server.Network.Structure.AppraiseInfo.BuildProfile))]
        static class SoldByOnExamine
        {
            static void Postfix(ACE.Server.Network.Structure.AppraiseInfo __instance, WorldObject wo, Player examiner)
            {
                try
                {
                    if (wo == null || examiner == null || !On || s_Ledger == null || wo.ContainerId != VAULT_GUID) return;
                    Listing x; lock (s_Lock) x = Find(s_Ledger, wo.Guid.Full);
                    if (x == null || x.State != State.Listed) return;
                    var line = SoldByLine(x.Seller, examiner.Guid.Full, g =>
                    {
                        var who = PlayerManager.FindByGuid(g);
                        return who == null || who.IsDeleted ? null : who.Name;
                    });
                    if (line == null) return;
                    var props = __instance.PropertiesString ??= new Dictionary<PropertyString, string>();
                    props.TryGetValue(PropertyString.LongDesc, out var desc);
                    props[PropertyString.LongDesc] = WithSoldBy(desc, line);
                    __instance.Flags |= IdentifyResponseFlags.StringStatsTable;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker sold-by: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBuyItem))]
        static class BuyFromBroker
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player __instance, uint vendorGuid, List<ItemProfile> items)
            {
                try
                {
                    var npc = __instance.CurrentLandblock?.GetObject(vendorGuid) as Vendor;
                    if (!IsBroker(npc)) return true;
                    if (__instance.IsBusy || __instance.IsTrading) { Fail(__instance); __instance.SendUseDoneEvent(WeenieError.YoureTooBusy); return false; }
                    Buy(__instance, npc, items);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker buy: {e}"); Fail(__instance); __instance.SendUseDoneEvent(); }
                return false;
            }
        }

        /// <summary>His Sell list is a way to hand him something: it lists, it never pays.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionSellItem))]
        static class SellToBroker
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player __instance, uint vendorGuid, List<ItemProfile> itemProfiles)
            {
                try
                {
                    var npc = __instance.CurrentLandblock?.GetObject(vendorGuid) as Vendor;
                    if (!IsBroker(npc)) return true;
                    var first = itemProfiles?.FirstOrDefault();
                    if (first != null) OfferItem(__instance, npc, first.ObjectGuid); else Fail(__instance);
                    if ((itemProfiles?.Count ?? 0) > 1) Tell(__instance, npc, "One at a time, friend. Let us start with that one.");
                    __instance.SendUseDoneEvent();
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker sell list: {e}"); Fail(__instance); __instance.SendUseDoneEvent(); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), "GiveObjectToNPC")]
        static class GiveToBroker
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player __instance, WorldObject target, WorldObject item)
            {
                try
                {
                    if (!IsBroker(target) || item == null) return true;
                    OfferItem(__instance, (Vendor)target, item.Guid.Full);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker give: {e}"); Fail(__instance, item?.Guid.Full ?? 0); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseWithTarget))]
        static class UseOnBroker
        {
            static bool Prefix(Player __instance, uint sourceObjectGuid, uint targetObjectGuid)
            {
                try
                {
                    var npc = __instance.CurrentLandblock?.GetObject(targetObjectGuid) as Vendor;
                    if (!IsBroker(npc)) return true;
                    OfferItem(__instance, npc, sourceObjectGuid);
                    __instance.SendUseDoneEvent();
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Broker use-with: {e}"); __instance.SendUseDoneEvent(); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionTalk))]
        static class SayPrice
        {
            static void Postfix(Player __instance, string message)
            {
                try { Heard(__instance, message); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker heard: {e.Message}"); }
            }
        }

        /// <summary>His Strongbox does not open for anyone.</summary>
        [HarmonyPatch(typeof(Container), nameof(Container.ActOnUse), new Type[] { typeof(WorldObject) })]
        static class StrongboxShut
        {
            static bool Prefix(Container __instance, WorldObject wo)
            {
                if (__instance?.WeenieClassId != VAULT_WCID || __instance.Guid.Full != VAULT_GUID) return true;
                if (wo is Player p) { p.Session?.Network.EnqueueSend(new GameMessageSystemChat("The strongbox is locked tight. Joe keeps the only key, somewhere unpleasant.", ChatMessageType.Broadcast)); p.SendUseDoneEvent(); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class LoginNoticePatch
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (!On || __instance == null) return;
                    var p = __instance;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(8.0);
                    chain.AddAction(p, () => Notify(p));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Broker login: {e.Message}"); }
            }
        }
    }
}
