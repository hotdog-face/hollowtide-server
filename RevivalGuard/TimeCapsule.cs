using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// HOUSE TIME CAPSULES (ours, not retail; owner 2026-10-29). Themes, odds and rules: TimeCapsuleRule,
    /// TimeCapsuleThemes (generated) and docs/HOUSE-TIME-CAPSULES.md. Off until the owner approves:
    /// rg_house_capsules (bool, default false).
    ///
    /// Retail behaviour kept: ACE never empties storage chests or hooks when a house is abandoned, evicted or
    /// released (only the slumlord's rent box), so a real owner's leftovers already reach the next buyer.
    ///
    /// SEEDED AT PURCHASE, not on a timer: nobody can open an unowned house's chest or hooks (ACE's checks;
    /// UnownedHouseGuard covers staff), so a stash placed the moment the house is bought looks the same to the
    /// buyer as one that sat there for weeks. A timer would load every empty house's landblock and write
    /// thousands of item rows for houses nobody buys.
    ///
    /// Vacancy: House.ClearPermissions runs on every release path (abandon, eviction, HouseRelease) with the owner
    /// already cleared, so it stamps VACANT_SINCE there. Purchase reads it, decides, then removes it: at most one
    /// stash per vacancy, and a house bought and lost again starts a fresh one. No stamp = never owned since
    /// tracking began = eligible.
    ///
    /// The note's signature is a real retail player's name from our January 2017 captures (owner's call for this
    /// feature), read at run time from the chat replay corpus on the host (it never ships in git or the public
    /// source); crude names and names in use on this shard are skipped.
    /// </summary>
    static class TimeCapsule
    {
        internal const string P_ON = "rg_house_capsules";
        internal const PropertyInt64 VACANT_SINCE = (PropertyInt64)29150;   // on the house: unix time it was released
        internal const PropertyInt64 SEEDED_AT = (PropertyInt64)29151;      // on the house: last stash, for @capsule
        internal const PropertyBool CAPSULE_ITEM = (PropertyBool)29152;     // on each stash item, so @capsule clear finds it
        internal const PropertyInt64 FOUND_AT = (PropertyInt64)29153;       // on the buyer: the account limit reads it
        internal const PropertyInt64 HUNG_FOR = (PropertyInt64)29154;       // on the house: Marker(vacancy key) once its pieces are up
        /// <summary>The hook layout's version. A house hung by an older layout (2026-10-29: too sparse) has its own pieces
        /// taken down and is hung again; a real owner's things are never touched.</summary>
        const long LAYOUT = 2, LAYOUT_UNIT = 1_000_000_000_000L;
        static long Marker(long vacancyKey) => vacancyKey + 1 + LAYOUT * LAYOUT_UNIT;
        internal const PropertyInt64 CHEST_DUE = (PropertyInt64)29155;      // on a villa/mansion: vacancy key + 1 whose chest waits for the basement
        const float REACH = 40f;
        static bool s_Registered;

        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard TimeCapsule: a house bought after standing empty a while holds a themed stash a 'previous owner' left");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule: property not registered ({e.Message}); read with default"); }
            CommandManager.TryAddCommand(Command, "capsule", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "House time capsules (ours, not retail): inspect, force-seed, clear or age the nearest house.",
                "[info|seed|hang|clear|age <days>|fresh]");
            Mod.Log.Info($"[RevivalGuard] TimeCapsule: {(On ? "ON" : "off")} ({P_ON}), @capsule");
        }

        /// <summary>Called after ACE made the buyer the owner. Same landblock as the house, its storage and hooks.</summary>
        internal static void OnPurchase(Player buyer, House house)
        {
            if (buyer == null || house == null) return;
            long now = (long)Time.GetUnixTime();
            long vacant = house.GetProperty(VACANT_SINCE) ?? 0;
            bool hung = Hung(house, vacant);
            int existing = Existing(house);          // counted before the stamps go: this vacancy's hung pieces don't count
            house.RemoveProperty(VACANT_SINCE);
            house.RemoveProperty(HUNG_FOR);
            if (!On) { house.SaveBiotaToDatabase(); return; }

            var chest = Chest(house);
            bool exempt = buyer.Session != null && buyer.Session.AccessLevel >= AccessLevel.Developer;
            bool ok = TimeCapsuleRule.Eligible(now, vacant, existing, LastAccountFind(buyer), exempt, out var why);
            if (!ok)
            {
                house.SaveBiotaToDatabase();
                Mod.Log.Info($"[RevivalGuard] TimeCapsule: {buyer.Name} bought 0x{house.Guid.Full:X8} ({house.HouseType}), no stash: {why}");
                return;
            }
            var stash = TimeCapsuleRule.Roll(house.Guid.Full, vacant, (int)house.HouseType);
            if (!stash.Hit)
            {
                // The house simply stood empty: no stash, and the account keeps its monthly find.
                house.SaveBiotaToDatabase();
                Mod.Log.Info($"[RevivalGuard] TimeCapsule: {buyer.Name} bought 0x{house.Guid.Full:X8} ({house.HouseType}, {why}), no stash: this vacancy's roll missed ({TimeCapsuleRule.HitPct((int)house.HouseType)}%)");
                return;
            }
            // Villas and mansions keep their storage in the basement (another landblock): it fills when the basement loads.
            int inChest = 0; string signer = null;
            if (chest != null) (inChest, signer) = PlaceChest(chest, stash);
            else house.SetProperty(CHEST_DUE, vacant + 1);
            // Hook pieces normally went up while the house stood empty (window-shoppers saw them); if not, now.
            int onHooks = hung ? 0 : PlaceHooks(house, chest, stash, ref inChest);
            house.SetProperty(SEEDED_AT, now);
            house.SaveBiotaToDatabase();
            if (inChest + onHooks > 0)
            {
                buyer.SetProperty(FOUND_AT, now);
                buyer.SaveBiotaToDatabase();
            }
            Mod.Log.Info($"[RevivalGuard] TimeCapsule: {buyer.Name} bought 0x{house.Guid.Full:X8} ({house.HouseType}, {why}): {stash.Theme.Name}, {(chest == null ? "chest due in the basement" : inChest + " in the chest")}, {(hung ? "hook pieces already up" : onHooks + " on hooks")}{(signer != null ? ", note from " + signer : "")}: {Describe(stash)}");
            Telemetry.Emit("house_capsule", buyer, $"0x{house.Guid.Full:X8} {stash.Theme.Key} {inChest}+{onHooks}");
        }

        /// <summary>What storage and hooks hold, not counting this vacancy's own hung pieces. After a release HUNG_FOR is
        /// gone, so everything left behind (stash pieces included) counts as the last owner's.</summary>
        static int Existing(House house)
        {
            bool hung = OwnVacancy(house, house.GetProperty(VACANT_SINCE) ?? 0);
            bool Counts(WorldObject i) => !(hung && (i.GetProperty(CAPSULE_ITEM) ?? false));
            return house.Storage.Sum(s => s.Inventory.Values.Count(Counts)) + house.Hooks.Count(h => h.HasItem && Counts(h.Item));
        }

        static bool Hung(House house, long vacancyKey) => house.GetProperty(HUNG_FOR) == Marker(vacancyKey);

        /// <summary>Hung (or cleared) this vacancy by any layout version: its capsule pieces are ours, not an owner's.</summary>
        static bool OwnVacancy(House house, long vacancyKey) => house.GetProperty(HUNG_FOR) is long m && m % LAYOUT_UNIT == vacancyKey + 1;

        /// <summary>Takes down this house's own capsule pieces (hooks and chest). Only items carrying the marker.</summary>
        static int TakeDown(House house)
        {
            int n = 0;
            foreach (var st in house.Storage.Where(x => x.CurrentLandblock != null))
                foreach (var item in st.Inventory.Values.Where(i => i.GetProperty(CAPSULE_ITEM) ?? false).ToList())
                    if (st.TryRemoveFromInventory(item.Guid, out var gone)) { gone.Destroy(); n++; }
            foreach (var hook in house.Hooks.Where(h => h.CurrentLandblock != null && h.HasItem))
            {
                var item = hook.Item;
                if (!(item.GetProperty(CAPSULE_ITEM) ?? false)) continue;
                if (hook.TryRemoveFromInventory(item.Guid, out var gone)) { gone.Destroy(); hook.SaveBiotaToDatabase(); n++; }
            }
            return n;
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> s_Filled = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();

        /// <summary>A basement storage chest loaded: if its villa or mansion was bought with the chest still due, fill it.
        /// RootHouse is the live main house when its landblock is loaded, else a database copy; the marker is cleared on
        /// the live object (its own landblock) or, with none loaded, on the copy, never a copy over a live house.</summary>
        internal static void FillBasement(Storage storage)
        {
            if (!On || storage?.House == null) return;
            {
                try
                {
                    var basement = storage.House;
                    if (basement == null || basement.Guid == basement.RootGuid) return;
                    var root = basement.RootHouse;
                    long due = root?.GetProperty(CHEST_DUE) ?? 0;
                    if (root == null || due == 0 || (root.HouseOwner ?? 0) == 0 || !s_Filled.TryAdd(root.Guid.Full, 0)) return;
                    var stash = TimeCapsuleRule.Roll(root.Guid.Full, due - 1, (int)root.HouseType);
                    var (n, signer) = PlaceChest(storage, stash);
                    void Clear(House h) { h.RemoveProperty(CHEST_DUE); h.SaveBiotaToDatabase(); s_Filled.TryRemove(h.Guid.Full, out _); }
                    if (root.CurrentLandblock != null) root.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() => Clear(root)));
                    else Clear(root);
                    Mod.Log.Info($"[RevivalGuard] TimeCapsule: basement chest of 0x{root.Guid.Full:X8} ({root.HouseType}, {stash.Theme.Name}) filled: {n} item(s){(signer != null ? ", note from " + signer : "")}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule basement chest: {e.Message}"); }
            }
        }

        // ---- hook pieces on an empty house (owner 2026-10-29: "there are items on hooks in the houses right?") ----

        /// <summary>A landblock finished creating its objects (queued behind ACE's own creation action, so every house,
        /// hook and storage chest exists, is linked and has its contents). Hook and storage contents load in their
        /// constructors, before any landblock or house link exists, so this is the first moment to look.</summary>
        internal static void OnLandblockReady(ACE.Server.Entity.Landblock lb)
        {
            if (!On || lb == null) return;
            foreach (var house in lb.Houses.ToList())
            {
                try
                {
                    if (house.Guid == house.RootGuid) HangIfDue(house, 0);
                    else foreach (var st in house.Storage) FillBasement(st);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule landblock 0x{house.Guid.Full:X8}: {e.Message}"); }
            }
        }

        static void HangIfDue(House house, int tries)
        {
            if ((house.HouseOwner ?? 0) != 0) return;
            // Hooks that hold something load it from the database in the background: look again shortly.
            if (house.Hooks.Any(h => !h.InventoryLoaded))
            {
                if (tries < 15)
                {
                    var chain = new ACE.Server.Entity.Actions.ActionChain();
                    chain.AddDelaySeconds(2);
                    chain.AddAction(house, () => { try { HangIfDue(house, tries + 1); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule hang 0x{house.Guid.Full:X8}: {e.Message}"); } });
                    chain.EnqueueChain();
                }
                return;
            }
            long vacant = house.GetProperty(VACANT_SINCE) ?? 0;
            if (OwnVacancy(house, vacant) && !Hung(house, vacant))
            {
                int down = TakeDown(house);
                house.RemoveProperty(HUNG_FOR);
                Mod.Log.Info($"[RevivalGuard] TimeCapsule: 0x{house.Guid.Full:X8} was hung by an older layout: took down {down} piece(s)");
            }
            if (!TimeCapsuleRule.HangNow(owned: false, alreadyHung: Hung(house, vacant),
                    eligible: TimeCapsuleRule.Eligible((long)Time.GetUnixTime(), vacant, Existing(house), 0, true, out _),
                    hit: TimeCapsuleRule.Roll(house.Guid.Full, vacant, (int)house.HouseType).Hit)) return;
            var stash = TimeCapsuleRule.Roll(house.Guid.Full, vacant, (int)house.HouseType);
            int inChest = 0;
            int n = PlaceHooks(house, Chest(house), stash, ref inChest);
            house.SetProperty(HUNG_FOR, Marker(vacant));
            house.SaveBiotaToDatabase();
            Mod.Log.Info($"[RevivalGuard] TimeCapsule: hung {n} piece(s) on empty 0x{house.Guid.Full:X8} ({house.HouseType}, {stash.Theme.Name}){(inChest > 0 ? $", {inChest} in the chest" : "")}");
        }

        /// <summary>Latest stash any character on the buyer's account found.</summary>
        static long LastAccountFind(Player buyer)
        {
            long last = buyer.GetProperty(FOUND_AT) ?? 0;
            try
            {
                var acct = buyer.Account?.AccountId;
                if (acct != null)
                    foreach (var p in PlayerManager.GetAccountPlayers(acct.Value).Values)
                        last = Math.Max(last, p.GetProperty(FOUND_AT) ?? 0);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule: account lookup: {e.Message}"); }
            return last;
        }

        /// <summary>The root house's emptiest storage chest (villa and mansion basements are another landblock).</summary>
        static Storage Chest(House house) =>
            house.Storage.Where(s => s.CurrentLandblock != null).OrderBy(s => s.Inventory.Count).FirstOrDefault();

        static WorldObject Make(TimeCapsuleRule.Pick p, int ageDays)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(p.Wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule: no weenie {p.Wcid}"); return null; }
            if (p.Salvage)
            {
                // As Player_Crafting builds a bag: units in Structure, workmanship summed over the items salvaged.
                wo.Structure = (ushort)p.Units;
                wo.NumItemsInMaterial = p.NumItems;
                wo.ItemWorkmanship = (int)Math.Round(p.Workmanship10 / 10.0 * p.NumItems);
                wo.Value = p.Units * 8;
            }
            else if (p.Count > 1 && (wo.MaxStackSize ?? 1) > 1)
                wo.SetStackSize(Math.Min(p.Count, wo.MaxStackSize ?? 1));
            Age(wo, ageDays);
            return wo;
        }

        /// <summary>Owner 2026-10-29: the stash must pass for a real player's leftovers. Created and last touched before
        /// the "owner" left (ageDays, plus a few days per item), and a hidden staff-only marker (custom properties are
        /// never in an appraisal: ACE sends only [AssessmentProperty] values) so @capsule clear can find it.</summary>
        static void Age(WorldObject wo, int ageDays)
        {
            long then = (long)Time.GetUnixTime() - (ageDays + (int)(wo.Guid.Full % 60)) * TimeCapsuleRule.DAY;
            wo.CreationTimestamp = (int)then;
            wo.SetProperty(CAPSULE_ITEM, true);
        }

        static (int chest, string signer) PlaceChest(Storage chest, TimeCapsuleRule.Stash stash)
        {
            int inChest = 0;
            string signer = null;
            var all = new List<WorldObject>();
            foreach (var p in stash.Chest) { var wo = Make(p, stash.AgeDays); if (wo != null) all.Add(wo); }
            if (stash.Note >= 0 && WorldObjectFactory.CreateNewWorldObject(TimeCapsuleRule.NOTE_WCID) is Book note)
            {
                signer = stash.Signer ?? TimeCapsuleRule.PickName(Names(), stash.NameRoll, n => PlayerManager.FindByName(n) != null);
                // Written the way a player's page is: an author name and a player-range author id (high in the range,
                // clear of real characters), never "ignore author".
                uint authorId = 0x5F000000u | (uint)(stash.NameRoll & 0xFFFFF);
                note.AddPage(authorId, signer, "", false, TimeCapsuleRule.NoteTexts(stash.Theme)[stash.Note], out _);
                Age(note, stash.AgeDays);
                all.Add(note);
            }
            foreach (var wo in all)
            {
                if (chest.TryAddToInventory(wo)) { wo.SaveBiotaToDatabase(); inChest++; }
                else { Mod.Log.Warn($"[RevivalGuard] TimeCapsule: {wo.Name} did not fit chest 0x{chest.Guid.Full:X8}"); wo.Destroy(); }
            }
            chest.SaveBiotaToDatabase();
            return (inChest, signer);
        }

        /// <summary>Each hook candidate on the first free hook it fits, within ACE's hook and hook-group limits, through
        /// ACE's own hook path (Hook.OnAddItem: the same state a player's hanging leaves).</summary>
        static int PlaceHooks(House house, Storage chest, TimeCapsuleRule.Stash stash, ref int inChest)
        {
            int onHooks = 0;
            var free = house.Hooks.Where(h => h.CurrentLandblock != null && !h.HasItem).OrderBy(h => h.Guid.Full).ToList();
            bool limited = PropertyManager.GetBool("house_hook_limit").Item && house.HouseMaxHooksUsable != -1;
            int usable = limited ? house.HouseCurrentHooksUsable : free.Count;
            var plan = TimeCapsuleRule.PlanHooks(stash, free.Select(h => (int)(h.HookType ?? 0)).ToList(), usable);
            var placed = new HashSet<uint>();
            foreach (var (idx, p) in plan)
            {
                if (limited && house.HouseCurrentHooksUsable <= 0) break;
                var hook = free[idx];
                var wo = Make(p, stash.AgeDays);
                if (wo == null) continue;
                var group = wo.HookGroup ?? HookGroupType.Undef;
                int gmax = house.GetHookGroupMaxCount(group);
                if ((gmax != -1 && house.GetHookGroupCurrentCount(group) >= gmax) || !Fits(wo, hook) || !hook.TryAddToInventory(wo)) { wo.Destroy(); continue; }
                wo.SaveBiotaToDatabase();
                placed.Add(p.Wcid);
                onHooks++;
            }
            // A hand-authored house keeps every piece: what found no hook waits in the chest.
            if (stash.Fixed && chest != null)
                foreach (var p in stash.Hooks.Where(p => !placed.Contains(p.Wcid)))
                {
                    var wo = Make(p, stash.AgeDays);
                    if (wo != null && chest.TryAddToInventory(wo)) { wo.SaveBiotaToDatabase(); inChest++; }
                    else wo?.Destroy();
                }
            return onHooks;
        }

        /// <summary>HookTypeGuard's rule without its unowned-house pass (a force-seed may run on an unowned house).</summary>
        static bool Fits(WorldObject item, Hook hook)
        {
            int hookType = hook.HookType ?? 0, itemTypes = hook.HookItemType ?? 0, itemHook = item.HookType ?? 0;
            if (itemHook == 0 || hookType == 0 || (itemHook & hookType) == 0) return false;
            return itemTypes == 0 || ((int)item.ItemType & itemTypes) != 0;
        }

        static string Describe(TimeCapsuleRule.Stash stash)
        {
            string N(TimeCapsuleRule.Pick p)
            {
                var name = DatabaseManager.World.GetCachedWeenie(p.Wcid)?.GetName() ?? "?";
                var extra = p.Salvage ? $" {p.Units}u" : p.Count > 1 ? $" x{p.Count}" : "";
                return $"{name}{extra}{(p.Tier != TimeCapsuleRule.Tier.Common ? " (" + p.Tier + ")" : "")}";
            }
            return $"chest ({stash.Chest.Count}): {string.Join(", ", stash.Chest.Select(N))}; theme hook pieces: {string.Join(", ", stash.Hooks.Select(N))} (plus furniture and clutter to 70-100% of free hooks)";
        }

        // ---- the retail names list (never in git: read from the chat replay corpus on the host) ----

        static List<string> s_Names;
        static readonly object s_NamesLock = new object();

        static string CorpusPath => Environment.GetEnvironmentVariable("RG_CHAT_REPLAY_CORPUS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "chatreplay", "corpus.jsonl");

        internal static List<string> Names()
        {
            lock (s_NamesLock)
            {
                if (s_Names != null) return s_Names;
                var set = new SortedSet<string>(StringComparer.Ordinal);
                try
                {
                    if (File.Exists(CorpusPath))
                        foreach (var line in File.ReadLines(CorpusPath))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                if (doc.RootElement.TryGetProperty("who", out var who) && who.GetString() is string n && TimeCapsuleRule.NameOk(n)) set.Add(n);
                            }
                            catch { }
                        }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule: names: {e.Message}"); }
                s_Names = set.ToList();
                Mod.Log.Info($"[RevivalGuard] TimeCapsule: {s_Names.Count} retail names for notes{(s_Names.Count == 0 ? $" (none at {CorpusPath}: notes are signed '{TimeCapsuleRule.FallbackNames[0]}')" : "")}");
                return s_Names;
            }
        }

        // ---- @capsule (admin) ----

        static void Command(Session session, params string[] args)
        {
            var p = session?.Player;
            var house = Nearest(p);
            if (house == null) { Say(session, $"[Capsule] No house within {REACH:F0} m: stand by its sale sign, covenant crystal, storage chest or a hook."); return; }
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
            long now = (long)Time.GetUnixTime();
            switch (verb)
            {
                case "seed":
                {
                    var chest = Chest(house);
                    long sk = house.GetProperty(VACANT_SINCE) ?? 0;
                    var stash = TimeCapsuleRule.Roll(house.Guid.Full, sk, (int)house.HouseType);
                    int c = 0; string signer = null;
                    if (chest != null) (c, signer) = PlaceChest(chest, stash);
                    else if ((house.HouseOwner ?? 0) != 0) { house.SetProperty(CHEST_DUE, sk + 1); Say(session, "[Capsule] No chest up here: the basement chest fills when the basement loads (go down)."); }
                    int h = PlaceHooks(house, chest, stash, ref c);
                    house.SetProperty(SEEDED_AT, now);
                    house.SaveBiotaToDatabase();
                    Mod.Log.Info($"[RevivalGuard] TimeCapsule: {p.Name} force-seeded 0x{house.Guid.Full:X8}: {stash.Theme.Name}, {c}+{h}: {Describe(stash)}");
                    Say(session, $"[Capsule] Force-seeded '{stash.Theme.Name}': {c} in the chest, {h} on hooks{(signer != null ? ", note from " + signer : "")}.");
                    return;
                }
                case "hang":
                {
                    long vk = house.GetProperty(VACANT_SINCE) ?? 0;
                    var stash = TimeCapsuleRule.Roll(house.Guid.Full, vk, (int)house.HouseType);
                    int c = 0, h = PlaceHooks(house, Chest(house), stash, ref c);
                    if ((house.HouseOwner ?? 0) == 0) house.SetProperty(HUNG_FOR, Marker(vk));
                    house.SaveBiotaToDatabase();
                    Say(session, $"[Capsule] Hung {h} '{stash.Theme.Name}' piece(s) now, hit or not.");
                    return;
                }
                case "clear":
                {
                    int n = 0;
                    foreach (var s in house.Storage.Where(s => s.CurrentLandblock != null))
                        foreach (var item in s.Inventory.Values.Where(i => i.GetProperty(CAPSULE_ITEM) ?? false).ToList())
                            if (s.TryRemoveFromInventory(item.Guid, out var gone)) { gone.Destroy(); n++; }
                    foreach (var hook in house.Hooks.Where(h => h.CurrentLandblock != null && h.HasItem))
                    {
                        var item = hook.Item;
                        if (!(item.GetProperty(CAPSULE_ITEM) ?? false)) continue;
                        if (hook.TryRemoveFromInventory(item.Guid, out var gone)) { gone.Destroy(); hook.SaveBiotaToDatabase(); n++; }
                    }
                    house.RemoveProperty(SEEDED_AT);
                    // Cleared stays cleared for this vacancy: an empty house's pieces don't come back at the next load.
                    if ((house.HouseOwner ?? 0) == 0) house.SetProperty(HUNG_FOR, Marker(house.GetProperty(VACANT_SINCE) ?? 0));
                    house.SaveBiotaToDatabase();
                    Say(session, $"[Capsule] Removed {n} stash item(s) from the storage and hooks.");
                    return;
                }
                case "age":
                {
                    if ((house.HouseOwner ?? 0) != 0) { Say(session, "[Capsule] Owned: a vacancy starts when it is released."); return; }
                    double days = args.Length > 1 && double.TryParse(args[1], out var d) ? d : TimeCapsuleRule.MIN_VACANT / (double)TimeCapsuleRule.DAY;
                    house.SetProperty(VACANT_SINCE, now - (long)(days * TimeCapsuleRule.DAY));
                    house.RemoveProperty(HUNG_FOR);
                    house.SaveBiotaToDatabase();
                    Say(session, $"[Capsule] Vacancy now started {days:F1} days ago.");
                    break;
                }
                case "fresh":
                {
                    if ((house.HouseOwner ?? 0) != 0) { Say(session, "[Capsule] Owned: a vacancy starts when it is released."); return; }
                    house.SetProperty(VACANT_SINCE, now);
                    house.RemoveProperty(HUNG_FOR);
                    house.SaveBiotaToDatabase();
                    Say(session, "[Capsule] Vacancy restarted now (as if just released).");
                    break;
                }
            }
            Info(session, house, now);
        }

        static void Info(Session session, House house, long now)
        {
            long vacant = house.GetProperty(VACANT_SINCE) ?? 0;
            long seeded = house.GetProperty(SEEDED_AT) ?? 0;
            int items = house.Storage.Sum(s => s.Inventory.Count), hooked = house.Hooks.Count(h => h.HasItem);
            int mine = house.Storage.Sum(s => s.Inventory.Values.Count(i => i.GetProperty(CAPSULE_ITEM) ?? false))
                     + house.Hooks.Count(h => h.HasItem && (h.Item.GetProperty(CAPSULE_ITEM) ?? false));
            bool owned = (house.HouseOwner ?? 0) != 0;
            bool ok = TimeCapsuleRule.Eligible(now, vacant, Existing(house), 0, true, out var why);
            var stash = TimeCapsuleRule.Roll(house.Guid.Full, vacant, (int)house.HouseType);
            Say(session, $"[Capsule] 0x{house.Guid.Full:X8} {house.HouseType}, {(owned ? "owned by " + house.HouseOwnerName : "for sale")}, feature {(On ? "ON" : "off")}.");
            Say(session, $"[Capsule] Vacant since: {(vacant > 0 ? $"{(now - vacant) / (double)TimeCapsuleRule.DAY:F2} days ago" : "never owned (no stamp)")}. Last seeded: {(seeded > 0 ? $"{(now - seeded) / 3600.0:F1} h ago" : "never")}.");
            Say(session, $"[Capsule] Storage: {house.Storage.Count} chest(s), {items} item(s); hooks: {house.Hooks.Count}, {hooked} in use; {mine} from a stash.");
            Say(session, $"[Capsule] {(owned ? "Not for sale" : ok ? "A purchase now WOULD find a stash" : "A purchase now would find nothing")} ({why}; account limit not checked here).");
            if (house.GetProperty(CHEST_DUE) != null) Say(session, "[Capsule] Basement chest: due, fills when the basement loads.");
            Say(session, $"[Capsule] Hook pieces for this vacancy: {(Hung(house, vacant) ? "up (or cleared)" : "not up")}. They go up when an empty, eligible HIT house's hooks load; @capsule hang forces them.");
            Say(session, $"[Capsule] This vacancy's roll: {(stash.Hit ? "HIT, a purchase finds a stash" : $"MISS, a purchase finds the house empty")} ({(stash.Fixed ? "a hand-authored house always hits" : TimeCapsuleRule.HitPct((int)house.HouseType) + "% for a " + house.HouseType)}). @capsule seed forces one either way.");
            Say(session, $"[Capsule] Roll: '{stash.Theme.Name}', neighbourhood {(stash.LivedIn ? "lived-in" : "quiet")}{(stash.Shared ? ", one owner's group of houses" : "")}, note {(stash.Note >= 0 ? "yes" : "no")}. {Describe(stash)}");
        }

        static House Nearest(Player p)
        {
            if (p?.CurrentLandblock == null || p.Location == null) return null;
            House best = null; float bestD = REACH;
            foreach (var o in p.CurrentLandblock.GetAllWorldObjectsForDiagnostics())
            {
                // The house the object belongs to on THIS landblock (a basement's chest -> the basement house), never
                // RootHouse: that is another landblock's object, not this thread's to change.
                House h = o is SlumLord sl ? sl.House : o is Storage st ? st.House : o is Hook hk ? hk.House : o as House;
                if (h == null || o.Location == null) continue;
                float d = p.Location.DistanceTo(o.Location);
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetHouseOwner))]
    static class TimeCapsuleOnPurchase
    {
        static bool Prepare() { TimeCapsule.Register(); return true; }
        static void Postfix(Player __instance, SlumLord slumlord)
        {
            try { TimeCapsule.OnPurchase(__instance, slumlord?.House); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule purchase: {e.Message}"); }
        }
    }

    /// <summary>Every release path clears the owner, then calls ClearPermissions, then saves the house.</summary>
    [HarmonyPatch(typeof(House), nameof(House.ClearPermissions))]
    static class TimeCapsuleOnRelease
    {
        static void Postfix(House __instance)
        {
            try
            {
                if (__instance == null || (__instance.HouseOwner ?? 0) != 0 || __instance.GetProperty(TimeCapsule.VACANT_SINCE) != null) return;
                __instance.SetProperty(TimeCapsule.VACANT_SINCE, (long)Time.GetUnixTime());
                __instance.RemoveProperty(TimeCapsule.HUNG_FOR);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule release: {e.Message}"); }
        }
    }

    /// <summary>Landblock.CreateWorldObjects (private) queues the creation itself; this queues the capsule look right
    /// behind it on the same landblock queue.</summary>
    [HarmonyPatch(typeof(ACE.Server.Entity.Landblock), "CreateWorldObjects")]
    static class TimeCapsuleOnLandblock
    {
        static void Postfix(ACE.Server.Entity.Landblock __instance)
        {
            try
            {
                if (!TimeCapsule.On) return;
                var lb = __instance;
                lb.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() => TimeCapsule.OnLandblockReady(lb)));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TimeCapsule landblock: {e.Message}"); }
        }
    }
}
