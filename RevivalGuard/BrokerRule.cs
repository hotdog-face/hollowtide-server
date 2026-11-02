using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE MARKETPLACE BROKER, the pure part (no ACE types, so NetTest runs it: tools/nettest/BrokerCase.cs). Ours, not
    /// retail (owner 2026-10-31, player request #19). The rules, the ledger's bookkeeping and the crash reconcile live
    /// here; Broker.cs moves the real items. Design, rules and failure handling: docs/MARKETPLACE-BROKER.md.
    ///
    /// THE LEDGER IS WRITTEN BEFORE AN ITEM MOVES, AND THE ITEM'S PLACE WINS AFTER A CRASH. Every list, sale, cancel and
    /// return marks the listing first (broker.json, through a temp file), then moves the item and saves it. After a
    /// restart Reconcile compares each open listing with where the item really is (in the Strongbox or not) and puts the
    /// ledger right: a sale whose item is still in the Strongbox never happened (the seller's credit comes back off),
    /// a cancel whose item is still there never happened either (listed again), a listing whose item never arrived is
    /// dropped. Nothing is ever created from the ledger, so a crash can lose a sale but never copy an item.
    /// </summary>
    static class BrokerRule
    {
        // ---- the rules (each one is the owner's to change; the doc lists them) ----
        internal const int MAX_LISTINGS = 10;                 // per character, listed + expired-awaiting-pickup
        internal const long LIST_SECONDS = 7 * 86400;         // then expired and held for the seller
        internal const double FEE_PCT = 0.01;                 // listing fee, paid up front, kept on cancel: a pyreal sink
        internal const long FEE_MIN = 10, FEE_MAX = 10_000;
        internal const double CUT_PCT = 0.05;                 // the Broker's cut of a sale: a pyreal sink
        // WHOLE MMDs ONLY (owner 2026-10-31: "the merchant should only have items for sale for mmds ... all items he sells are
        // listed for whole mmd amounts only"): 1 to 200 MMD (MMD = 250,000 pyreals), every listing, players' and the house's.
        internal const long PRICE_MIN = 250_000, PRICE_MAX = 50_000_000;
        internal const double OFFER_SECONDS = 120;            // an offered item waits this long for a price and a Yes
        internal const double SAY_RANGE = 8.0;                // a price said within this many metres reaches him
        internal const uint HOUSE = 0;                        // seller of the seed stock: its sales pay no one

        internal enum State { Listed, Sold, Cancelled, Expired, Returned, Gone }

        internal sealed class Listing
        {
            public uint Item { get; set; }
            public uint Wcid { get; set; }
            public string ItemName { get; set; }
            public uint Seller { get; set; }
            public string SellerName { get; set; }
            public long Price { get; set; }
            public long Fee { get; set; }
            public long Listed { get; set; }      // unix seconds
            public long Expires { get; set; }
            public State State { get; set; }
            public uint Buyer { get; set; }
            public string BuyerName { get; set; }
            public long Done { get; set; }
            public long Credited { get; set; }    // what this sale added to the seller's purse (0 for the house)
            public bool Open => State == State.Listed || State == State.Expired;
        }

        internal sealed class Ledger
        {
            public List<Listing> Listings { get; set; } = new List<Listing>();
            public Dictionary<uint, long> Owed { get; set; } = new Dictionary<uint, long>();
            /// <summary>Sale notices not yet told to an offline seller, by character.</summary>
            public Dictionary<uint, List<string>> Notices { get; set; } = new Dictionary<uint, List<string>>();
            public bool Seeded { get; set; }
            /// <summary>Every seed wcid ever placed (SeedPlan): a seed is placed once, even after it sells.</summary>
            public List<uint> SeedsPlaced { get; set; } = new List<uint>();
            public long SinkTotal { get; set; }   // fees + cuts + house sales: pyreals taken out of the economy
        }

        // ---- money ----
        internal static long Fee(long price) => Math.Clamp((long)Math.Ceiling(price * FEE_PCT), FEE_MIN, FEE_MAX);
        internal static long Cut(long price) => (long)Math.Ceiling(price * CUT_PCT);
        internal static long Proceeds(long price) => Math.Max(0, price - Cut(price));

        /// <summary>A price he takes: whole MMDs, 1 to 200.</summary>
        internal static bool ValidPrice(long price) => price >= PRICE_MIN && price <= PRICE_MAX && price % MMD == 0;

        /// <summary>A price said to him, in MMDs: "3", "3 mmd", "3 mmds", "3 MMD". Pyreal amounts, fractions and anything
        /// else are not prices. Returns the price in pyreals.</summary>
        internal static bool TryParsePrice(string said, out long price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(said)) return false;
            var s = said.Trim().ToLowerInvariant().Replace(" ", "");
            foreach (var tail in new[] { "mmds", "mmd" })
                if (s.EndsWith(tail, StringComparison.Ordinal)) { s = s.Substring(0, s.Length - tail.Length); break; }
            if (s.Length == 0 || s.Length > 3) return false;
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            long n = long.Parse(s, CultureInfo.InvariantCulture);
            price = n * MMD;
            if (ValidPrice(price)) return true;
            price = 0;
            return false;
        }

        /// <summary>The price box's answer: an MMD count (1 to 200), or, from a client that sent pyreals, a whole-MMD
        /// pyreal amount. Anything else is refused.</summary>
        internal static bool TryBoxPrice(string sent, out long price)
        {
            price = 0;
            if (!long.TryParse(sent, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n <= 0) return false;
            price = n < MMD ? n * MMD : n;
            if (ValidPrice(price)) return true;
            price = 0;
            return false;
        }

        /// <summary>His opening price for an offered item: its value rounded UP to whole MMDs, 1 to 200.</summary>
        internal static long Suggest(long value) => Math.Clamp((Math.Max(0, value) + MMD - 1) / MMD, 1, PRICE_MAX / MMD) * MMD;

        /// <summary>Open players' listings priced before the whole-MMD rule (or otherwise not whole MMDs): taken off the
        /// table and held for their sellers, who take them back by using him.</summary>
        internal static List<Listing> OffRule(Ledger l) =>
            l.Listings.Where(x => x.State == State.Listed && x.Seller != HOUSE && !ValidPrice(x.Price)).ToList();

        // ---- MMDs (owner 2026-10-31: "Players will mostly want to list items for sale in whole mmd amounts") ----
        /// <summary>The MMD: Trade Note (250,000), wcid 20630, 250 to a stack. Pyreals stack to 25,000.</summary>
        internal const long MMD = 250_000;
        internal const uint MMD_WCID = 20630, PYREAL_WCID = 273;
        internal const int MMD_STACK = 250, PYREAL_STACK = 25_000;

        internal static (long mmd, long pyreals) Split(long amount) => amount <= 0 ? (0, 0) : (amount / MMD, amount % MMD);

        /// <summary>How a price reads in his window and lines: "3 MMD", "3 MMD + 12,000", or "12,000 pyreals".</summary>
        internal static string PriceText(long price)
        {
            var (m, p) = Split(price);
            if (m == 0) return $"{p:N0} pyreals";
            return p == 0 ? $"{m} MMD" : $"{m} MMD + {p:N0}";
        }

        /// <summary>A payout as notes and coin: whole MMDs as MMD notes, the rest as pyreals. Stacks: how many pack slots
        /// it takes at most (a stack may merge into one already carried, so this is the safe count).</summary>
        internal static (long mmd, long pyreals, int stacks) Payout(long amount)
        {
            var (m, p) = Split(amount);
            int stacks = (int)((m + MMD_STACK - 1) / MMD_STACK + (p + PYREAL_STACK - 1) / PYREAL_STACK);
            return (m, p, stacks);
        }

        // ---- what he takes ----
        internal struct ItemFacts
        {
            public bool Attuned, Bonded, Retained, Stackable, Money, Equipped, Unsellable, Locked, PetOut, Trading;
            public bool ContainerWithItems, Mercenary;
        }

        internal enum Refusal { None, Attuned, Story, Stack, Money, Equipped, Retained, Locked, Container, Pet, Trading, Limit, Mercenary }

        /// <summary>The first reason he will not take it, or None. Quest items are almost all attuned or bonded (and
        /// IsSellable false covers the rest), so "no quest items" is those three tests.</summary>
        internal static Refusal Check(ItemFacts f, int alreadyListed)
        {
            if (f.Mercenary) return Refusal.Mercenary;
            if (f.Money) return Refusal.Money;
            if (f.Attuned || f.Bonded) return Refusal.Attuned;
            if (f.Unsellable) return Refusal.Story;
            if (f.Retained) return Refusal.Retained;
            if (f.Locked) return Refusal.Locked;
            if (f.Equipped) return Refusal.Equipped;
            if (f.Stackable) return Refusal.Stack;
            if (f.ContainerWithItems) return Refusal.Container;
            if (f.PetOut) return Refusal.Pet;
            if (f.Trading) return Refusal.Trading;
            if (alreadyListed >= MAX_LISTINGS) return Refusal.Limit;
            return Refusal.None;
        }

        // ---- the ledger's moves (the caller saves the ledger after each, before moving the item) ----
        internal static int OpenCount(Ledger l, uint seller) => l.Listings.Count(x => x.Seller == seller && x.Open);

        internal static Listing Add(Ledger l, uint item, uint wcid, string itemName, uint seller, string sellerName, long price, long fee, long now)
        {
            var x = new Listing
            {
                Item = item, Wcid = wcid, ItemName = itemName, Seller = seller, SellerName = sellerName, Price = price, Fee = fee,
                Listed = now, Expires = seller == HOUSE ? long.MaxValue : now + LIST_SECONDS, State = State.Listed,
            };
            l.Listings.RemoveAll(o => o.Item == item && !o.Open);   // a re-listed item's old closed row goes
            l.Listings.Add(x);
            l.SinkTotal += fee;
            return x;
        }

        internal static Listing Find(Ledger l, uint item) => l.Listings.FirstOrDefault(x => x.Item == item && x.Open);

        /// <summary>A sale: the seller is owed price less the cut (the house is owed nothing). Returns the credit.</summary>
        internal static long Sell(Ledger l, Listing x, uint buyer, string buyerName, long now)
        {
            x.State = State.Sold; x.Buyer = buyer; x.BuyerName = buyerName; x.Done = now;
            long credit = x.Seller == HOUSE ? 0 : Proceeds(x.Price);
            x.Credited = credit;
            if (credit > 0) l.Owed[x.Seller] = l.Owed.GetValueOrDefault(x.Seller) + credit;
            l.SinkTotal += x.Price - credit;
            return credit;
        }

        internal static void Close(Listing x, State how, long now) { x.State = how; x.Done = now; }

        /// <summary>Listings past their time become Expired (held for the seller). Returns the newly expired.</summary>
        internal static List<Listing> Expire(Ledger l, long now)
        {
            var hit = l.Listings.Where(x => x.State == State.Listed && now >= x.Expires).ToList();
            foreach (var x in hit) x.State = State.Expired;
            return hit;
        }

        /// <summary>Takes up to `cap` of what the seller is owed (cap = what fits in the pack); the ledger first.</summary>
        internal static long Collect(Ledger l, uint seller, long cap = long.MaxValue)
        {
            long owed = l.Owed.GetValueOrDefault(seller);
            long take = Math.Min(owed, Math.Max(0, cap));
            if (take <= 0) return 0;
            if (owed - take > 0) l.Owed[seller] = owed - take; else l.Owed.Remove(seller);
            return take;
        }

        /// <summary>Closed rows older than this are dropped from broker.json (the log file keeps the history).</summary>
        internal static void Prune(Ledger l, long now, long keepSeconds = 30 * 86400) =>
            l.Listings.RemoveAll(x => !x.Open && x.Done > 0 && now - x.Done > keepSeconds);

        // ---- the reconcile after a restart ----
        internal enum Where { InVault, WithSeller, Elsewhere, Missing }

        internal sealed class Fix { public Listing Listing; public State From, To; public string Why; }

        /// <summary>Puts the ledger right from where each item is. `where` answers for one item guid; `vaultItems` is
        /// everything the Strongbox holds (for items no listing names). Returns what changed and the vault items no
        /// listing claims (held back and logged, never shown or handed out).</summary>
        internal static (List<Fix> fixes, List<uint> unclaimed) Reconcile(Ledger l, Func<Listing, Where> where, ICollection<uint> vaultItems)
        {
            var fixes = new List<Fix>();
            void Set(Listing x, State to, string why) { fixes.Add(new Fix { Listing = x, From = x.State, To = to, Why = why }); x.State = to; }
            var claimed = new HashSet<uint>();
            foreach (var x in l.Listings.ToList())
            {
                if (x.State == State.Gone) continue;
                var w = where(x);
                if (w == Where.InVault) claimed.Add(x.Item);
                switch (x.State)
                {
                    case State.Listed:
                    case State.Expired:
                        if (w == Where.WithSeller) Set(x, State.Cancelled, "the item is with its seller (a cancel or return landed, or the listing never did)");
                        else if (w == Where.Elsewhere) Set(x, State.Gone, "the item is not in the Strongbox and not with its seller");
                        else if (w == Where.Missing) Set(x, State.Gone, "the item is in no table at all");
                        break;
                    case State.Sold:
                        if (w == Where.InVault)
                        {
                            if (x.Credited > 0)
                            {
                                long owed = l.Owed.GetValueOrDefault(x.Seller) - x.Credited;
                                if (owed > 0) l.Owed[x.Seller] = owed; else l.Owed.Remove(x.Seller);
                            }
                            l.SinkTotal -= x.Price - x.Credited;
                            x.Credited = 0; x.Buyer = 0; x.BuyerName = null; x.Done = 0;
                            Set(x, State.Listed, "the sale never landed: the item is still in the Strongbox");
                        }
                        break;
                    case State.Cancelled:
                    case State.Returned:
                        if (w == Where.InVault) { x.Done = 0; Set(x, State.Listed, "the hand-back never landed: the item is still in the Strongbox"); }
                        break;
                }
            }
            var unclaimed = vaultItems.Where(g => !claimed.Contains(g)).ToList();
            return (fixes, unclaimed);
        }

        // ---- what he says (no em dashes: player text) ----
        /// <summary>Who runs the stall (owner 2026-10-31: Joe the Sho; the first cast was Vessik the Sclavus, the generator's
        /// --look vessik). His name in the mod's lines and the price box's title.</summary>
        internal const string WHO = "Joe", WHO_FULL = "Joe the Sho";
        static readonly Random s_Rng = new Random();
        static string Pick(string[] lines) { lock (s_Rng) return lines[s_Rng.Next(lines.Length)]; }

        internal static string RefusalLine(Refusal r) => r switch
        {
            Refusal.Attuned => Pick(new[] { "This one is bound to you, friend. I can smell it. Nobody else could even pick it up.",
                                            "That thing would crawl right back to you. Joe does not sell homing pigeons." }),
            Refusal.Story => Pick(new[] { "No, no. That thing has a story attached, and stories bring trouble. Keep it.",
                                          "I know where that came from, and so do the people looking for it. Not on my table." }),
            Refusal.Stack => "Stacks I do not do. One piece at a time, nice and quiet.",
            Refusal.Money => "You want Joe to sell you money? Clever. No.",
            Refusal.Equipped => "Take it off first. I do not sell things people are still wearing. Usually.",
            Refusal.Retained => "You marked that one Retained. Even I respect that.",
            Refusal.Locked => "That one is locked to you. Unlock it first, then we talk.",
            Refusal.Container => "Empty it first. I sell packs, not mystery boxes.",
            Refusal.Pet => "Put your pet away before you sell its house.",
            Refusal.Trading => "You are already trading that to someone. One swindle at a time.",
            Refusal.Limit => $"You already have {MAX_LISTINGS} things on my table or waiting for you. Let some sell, or take some back.",
            Refusal.Mercenary => "Sellswords do not sell, friend. Sellswords get sold.",
            _ => "",
        };

        internal static string OfferLine(string item, long price) =>
            $"Ahh, nice piece. I can move your {item} for {PriceText(price)}. Want more? Say a number of trade notes and I ask again. I will not judge. Much.";

        internal static string ConfirmText(string item, long price, long fee) =>
            $"List your {item} with {WHO} for {PriceText(price)}? The listing fee is {fee:N0} pyreals, paid now and not returned. He keeps {CUT_PCT * 100:0}% when it sells.";

        internal static string ListedLine(string item, long price) =>
            $"Done. Your {item} sits on my table for {LIST_SECONDS / 86400} days at {PriceText(price)}. Come see me to collect when it sells.";

        internal static string NoFeeLine(long fee) => $"The fee is {fee:N0} pyreals and your purse disagrees. Come back richer.";

        internal static string SoldTell(string item, long price, long credit) =>
            $"Psst. Your {item} just sold for {PriceText(price)}. {PriceText(credit)} of it is yours. Come see me in the Marketplace to collect.";

        internal static string LoginNotice(int sold, long owed, int waiting)
        {
            var parts = new List<string>();
            if (sold > 0) parts.Add($"{sold} of your goods sold while you were away");
            if (owed > 0) parts.Add($"{PriceText(owed)} {(owed < MMD ? "are" : "is")} waiting for you");
            if (waiting > 0) parts.Add($"{waiting} unsold {(waiting == 1 ? "item is" : "items are")} ready to take back");
            return parts.Count == 0 ? null : $"{WHO_FULL} sent word: {string.Join(", and ", parts)}. Find him in the Marketplace.";
        }

        internal static string CollectLine(long amount) =>
            Pick(new[] { $"Here. {PriceText(amount)}, counted twice, the second time honestly.",
                         $"{PriceText(amount)}. Do not spend it all in one place. Spend it here." });

        internal static string PackFullPayLine(long held) =>
            $"Your pack has no room for your money, friend. I am holding {PriceText(held)} for you. Make room and come back.";

        internal static string ExpiredBackLine(string item) =>
            $"Your {item} sat on my table a full week and nobody bit. Here it is back. No charge for the storage. This time.";

        /// <summary>Who sells a listing, for its examine text in his window (owner 2026-11-01: "let players see who is selling
        /// the items he sells"). The retail way, like a storage chest's "Owned by X": one line at the end of the item's
        /// description. The name is the seller CHARACTER's current name, looked up now (never the name saved with the
        /// listing, which a rename makes stale, and never an account name); a character that is gone or deleted gets no
        /// line. The house's own stock reads as his; your own listing reads "Sold by you".</summary>
        internal static string SoldByLine(uint seller, uint viewer, Func<uint, string> currentName)
        {
            if (seller == HOUSE) return $"Sold by {WHO_FULL}";
            if (seller == viewer) return "Sold by you";
            var name = currentName?.Invoke(seller)?.Trim();
            return string.IsNullOrEmpty(name) ? null : $"Sold by {name}";
        }

        /// <summary>The item's own description with the seller line after it (a blank line between), or the line alone.</summary>
        internal static string WithSoldBy(string longDesc, string line)
        {
            if (string.IsNullOrEmpty(line)) return longDesc;
            if (string.IsNullOrWhiteSpace(longDesc)) return line;
            return longDesc.TrimEnd() + "\n\n" + line;
        }

        internal const string BoxLine ="Ahh, nice piece. Name your price, friend. I will not judge. Much.";
        internal const string CancelLine = "Taking it back? Fine, fine. The fee stays with me. That is how fees work.";
        internal const string PackFullLine = "Your pack is full, friend. Make room, then we deal.";
        internal const string OwnListingLine = "That is your own piece. Buy it from yourself? I like you. Take it back for free.";
        internal const string OfferTimeoutLine = "Changed your mind? Wise. Or not. Come back when you know.";
        internal const string NoPriceLine = "Hand me the thing first, then say the price.";
        internal const string BadPriceLine = "Joe deals in whole trade notes only, friend. Say it like 3 or 3 mmd, from 1 to 200.";
        internal const string WholeMmdLine = "Whole MMDs only, friend. Joe does not count pyreals. Pick 1 to 200 trade notes.";
        internal const string ClosedLine = "Not today, friend. Come back later.";

        internal static readonly string[] Chatter =
        {
            "I have a cousin in Qalaba'r who sells the same things for double. Do not tell him I said that.",
            "A Tusker came by yesterday wanting a hat. I sold him two.",
            "Every item here comes with a guarantee. The guarantee is that it is an item.",
            "Keep your voice down. The Olthoi helm was a gift. From an Olthoi. It just did not know it yet.",
        };
        internal static string ChatterLine() => Pick(Chatter);

        // ---- the seed stock: a handful of listings that make the table look used on day one ----
        /// <summary>A seed: the item, its price, its weenie value (for the check that a seed is never a bargain) and why.</summary>
        internal sealed class Seed { public uint Wcid; public long Price, Value; public string Note; }

        /// <summary>Plausible mid-value player loot, priced at or above what a player would ask, never a bargain (owner ticket
        /// 261101-0002: "the rare gauntlets for 3mmds is WAY too cheap ... we can't launch with that item"). No rares, no
        /// quest or set pieces players chase. Two priced in whole MMDs on common items worth far less to a player, so the
        /// MMD display shows. The house owns them (HOUSE): a sale pays no one, the pyreals are a sink. Every price is at
        /// least twice the item's value, so nobody profits by buying one and selling it to a vendor.
        /// The table can change: SeedPlan pulls house listings whose item left the table, reprices the rest and adds the
        /// new ones, on a fresh shard and on one already seeded alike.</summary>
        internal static readonly Seed[] SeedStock =
        {
            // Ticket 261101-0018 (owner 2026-11-01: "way too low ... ancient armored boots = 20mmds"): priced by how much a
            // player wants each piece next to those boots, not by vendor value, erring high. No free bees.
            new Seed { Wcid = 27909, Price = 20 * MMD, Value = 6_000, Note = "Ancient Armored Long Boots (AL 260): the owner's anchor" },
            new Seed { Wcid = 22013, Price = 18 * MMD, Value = 4_400, Note = "Olthoi Helm (AL 270): top-tier helm, wanted as much as the boots" },
            new Seed { Wcid = 37216, Price = 15 * MMD, Value = 1_631, Note = "Olthoi Breastplate (AL 200): Olthoi plate, a long-term chest piece" },
            new Seed { Wcid = 12011, Price = 12 * MMD, Value = 2_700, Note = "Basalt Blade, no wield requirement: strong for anyone, twinks want it" },
            new Seed { Wcid = 11985, Price = 10 * MMD, Value = 3_500, Note = "Heavy Ursuin Coat (AL 150): sturdy coat, chest and arms" },
            new Seed { Wcid = 6046, Price = 8 * MMD, Value = 1_738, Note = "Amuli Coat: wide coverage, a good mid-level base" },
            new Seed { Wcid = 8896, Price = 8 * MMD, Value = 4_000, Note = "Furry Mattekar Hide Coat: a decent coat, mostly a look" },
            new Seed { Wcid = 6044, Price = 6 * MMD, Value = 1_785, Note = "Celdon Breastplate: common plate, a starter chest piece" },
            new Seed { Wcid = 6003, Price = 6 * MMD, Value = 1_545, Note = "Koujia Breastplate: common, a starter chest piece" },
        };

        /// <summary>What to do to a ledger's house listings to match SeedStock: pull the open ones whose item left the table,
        /// reprice the open ones whose price changed, and add each seed never placed. A seed placed before (open, sold or
        /// pulled) is not placed again: a sold seed stays sold. The wcids placed are remembered in Ledger.SeedsPlaced; a
        /// ledger from before that list counts its house listings, whatever their state.</summary>
        internal static (List<Listing> pull, List<(Listing x, long price)> reprice, List<Seed> add) SeedPlan(Ledger l)
        {
            var table = SeedStock.ToDictionary(z => z.Wcid);
            var house = l.Listings.Where(x => x.Seller == HOUSE).ToList();
            var placed = new HashSet<uint>(l.SeedsPlaced ?? new List<uint>());
            foreach (var x in house) placed.Add(x.Wcid);
            var pull = house.Where(x => x.Open && !table.ContainsKey(x.Wcid)).ToList();
            var reprice = house.Where(x => x.Open && table.TryGetValue(x.Wcid, out var z) && z.Price != x.Price)
                               .Select(x => (x, table[x.Wcid].Price)).ToList();
            var add = SeedStock.Where(z => !placed.Contains(z.Wcid)).ToList();
            return (pull, reprice, add);
        }
    }
}
