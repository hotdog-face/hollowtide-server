using ACE.Database;
using ACE.Database.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// NAME COLLISION, 2026-09-22: this was registered as `bestow` and silently never ran. ACE
    /// already ships `bestow <name> <level>` (the advocate command), its handler wins the console
    /// dispatch, and ours logged "registered" perfectly while every call hit ACE's usage text
    /// instead. A load banner proves a command was registered, not that it is reachable, which is
    /// the same lesson keyrings taught the same night. Before adding a verb, check it against the
    /// 317 names in ACE's own CommandHandler attributes.
    /// <summary>
    /// @BESTOW: BONDED ITEMS INTO AN OFFLINE CHARACTER'S PACK.
    ///
    /// The shard is full PvP with retail's PK death loot (PkDeathLoot): wielded gear drops at any
    /// level and every rare drops. The one thing that never drops is an item whose PropertyInt.Bonded
    /// is BondedStatus.Bonded (1): Player.CalculateDeathItems filters "Bonded != 0" out of the value
    /// sort before anything is chosen, HandleDestroyBonded only consumes Destroy (-2), and
    /// GetSlipperyItems only lists Slippery (-1). So an item made here can be lost by trade, sale or
    /// deliberate drop, never by dying.
    ///
    /// The owner asked for his main character's kit back, bonded, while that character is offline and
    /// nobody may log it in. ACE's login path (WorldManager.PlayerEnterWorld) loads possessions from
    /// the database by GetPossessedBiotasInParallel and sorts anything whose Container is the player
    /// into the main pack (Container.SortWorldObjectsIntoInventory renumbers PlacementPosition), so a
    /// new biota written with Container = the character's id is in the pack at the next login. The
    /// player's own biota is not touched, so the OfflinePlayer swap that @snapshot restore needs is
    /// not needed here. Items go in the pack, never straight onto the body: equipping through ACE's
    /// own path keeps its wield checks, and an item written as worn that the character cannot wield
    /// could not be put back on once removed.
    ///
    ///   @grantgear &lt;character name&gt; | &lt;wcid&gt;[xN][:pt=&lt;palette template&gt;,shade=&lt;0..1&gt;,maxlvl=&lt;1..5&gt;,maxed=true] ...
    ///
    /// xN makes N copies (one stack for a stackable weenie, N objects otherwise). pt and shade dye the
    /// item the way the barber and a dye pot would (PaletteTemplate and Shade). maxlvl sets
    /// ItemMaxLevel and its icon overlay, which is how ACE's loot factory makes a level-capped
    /// aetheria. maxed=true fills ItemTotalXp to the item's own cap through
    /// ExperienceSystem.ItemLevelToTotalXP, so a levelable cloak or aetheria arrives at the level
    /// wearing it to the cap would have reached -- the level is DERIVED from that XP by ACE, which
    /// is why the XP is what gets set. Every item is Bonded. Admin only; it works from the console FIFO (no world session
    /// needed) and from a client. An online character is refused: hand them the items in game. The
    /// whole batch is written on the database thread, in order with any queued save, and every item
    /// made is named in the Audit channel. Admin command feedback is ours, not retail's.
    ///
    /// REDUCING COVERAGE (2026-09-22, owner: "the admin character's items need to be reduced to less slot coverage
    /// so the upper arms and bracers are open"). A word of the form `0x<guid>:reduce=main|lower|middle`
    /// names an item the character already holds and applies the Armor Reduction Tool of that name
    /// to it, offline, with ACE's own Tailoring.TailorReduceArmor mapping; see Reduce below for what
    /// is kept and the one rule that is not. Words of both kinds may share a call.
    /// </summary>
    public static class Bestow
    {
        const int MAX_SPECS = 40;
        const int MAX_COPIES = 250;
        const int DEFAULT_CAPACITY = 102;

        /// <summary>One word after the bar. Wcid != 0: make items. Guid != 0: reduce the coverage of an
        /// item the character already holds (Reduce is main, lower or middle).</summary>
        sealed class Spec { public uint Wcid; public int Count = 1; public int? Palette; public double? Shade; public int? MaxLevel; public bool Maxed; public uint Guid; public string Reduce; public string Text; }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "grantgear", AccessLevel.Admin, CommandHandlerFlag.None,
                "Create items, every one Bonded (never drops on death), in an offline character's main pack; or reduce a held armor piece's coverage the way the Armor Reduction Tools do.",
                "<character name> | <wcid>[xN][:pt=<palette>,shade=<0..1>,maxlvl=<1..5>,maxed=true] ... | 0x<held item guid>:reduce=<main|lower|middle>");
            Mod.Log.Info("[RevivalGuard] Bestow: @grantgear registered (name avoids ACE's own advocate `bestow`) (Admin; console or in game; offline characters only)");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Who(Session s) => s?.Player?.Name ?? "console";

        static void Handle(Session session, params string[] parameters)
        {
            if (session != null && session.AccessLevel < AccessLevel.Admin) { Say(session, "@grantgear is Admin only."); return; }
            var line = string.Join(" ", parameters);
            var bar = line.IndexOf('|');
            if (bar < 0)
            {
                Say(session, "Usage: @grantgear <character name> | <wcid>[xN][:pt=<palette>,shade=<0..1>,maxlvl=<1..5>,maxed=true] ... or 0x<held item guid>:reduce=<main|lower|middle>  (the bar separates the name from the items)");
                return;
            }
            var name = line.Substring(0, bar).Trim();
            var specs = new List<Spec>();
            foreach (var word in line.Substring(bar + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var spec = Parse(word, out var why);
                if (spec == null) { Say(session, $"'{word}': {why}. Nothing was made."); return; }
                specs.Add(spec);
            }
            if (specs.Count == 0) { Say(session, "No items given after the bar. Nothing was made."); return; }
            if (specs.Count > MAX_SPECS) { Say(session, $"{specs.Count} items in one call; the limit is {MAX_SPECS}. Nothing was made."); return; }

            var target = PlayerManager.FindByName(name, out bool online);
            if (target == null) { Say(session, $"No character named '{name}'. Nothing was made."); return; }
            if (online) { Say(session, $"{target.Name} is online; @grantgear writes the pack in the database under an offline character. Give the items in game instead, or have them log out. Nothing was made."); return; }
            if (!(target is OfflinePlayer op)) { Say(session, $"{target.Name} is not an offline player object. Nothing was made."); return; }
            try { if (op.IsDeleted || op.IsPendingDeletion) { Say(session, $"{target.Name} is deleted or pending deletion. Nothing was made."); return; } }
            catch (Exception e) { Say(session, $"Could not read {target.Name}'s deletion state ({e.GetType().Name}). Nothing was made."); return; }

            // Every weenie must exist before anything is written, so a typo cannot leave half a kit.
            var missing = specs.Where(s => s.Guid == 0 && DatabaseManager.World.GetCachedWeenie(s.Wcid) == null).Select(s => s.Wcid.ToString()).ToList();
            if (missing.Count > 0) { Say(session, $"No weenie with class id {string.Join(", ", missing)}. Nothing was made."); return; }

            uint guid = target.Guid.Full;
            var by = Who(session);
            int capacity = op.GetProperty(PropertyInt.ItemsCapacity) ?? DEFAULT_CAPACITY;

            DatabaseManager.Shard.GetPossessedBiotasInParallel(guid, possessions =>
            {
                try
                {
                    // Re-checked on the database thread: a login between the command and here would
                    // load the pack before these rows exist and save over them later.
                    var again = PlayerManager.FindByGuid(guid, out bool onlineNow);
                    if (again == null || onlineNow) { Say(session, $"{name} {(again == null ? "vanished" : "logged in")} before the write. Nothing was made."); return; }

                    int inMainPack = 0, lastPos = -1;
                    foreach (var b in possessions.Inventory)
                    {
                        bool inPlayer = b.BiotaPropertiesIID.Any(p => p.Type == (ushort)PropertyInstanceId.Container && p.Value == guid);
                        if (!inPlayer) continue;
                        // Side slots (packs and foci), not main-pack cells: WorldObject.UseBackpackSlot
                        bool sideSlot = (WeenieType)b.WeenieType == WeenieType.Container
                            || b.BiotaPropertiesBool.Any(p => p.Type == (ushort)PropertyBool.RequiresBackpackSlot && p.Value);
                        if (sideSlot) continue;
                        inMainPack++;
                        var pos = b.BiotaPropertiesInt.FirstOrDefault(p => p.Type == (ushort)PropertyInt.PlacementPosition);
                        if (pos != null && pos.Value > lastPos) lastPos = pos.Value;
                    }
                    int cellsNeeded = specs.Where(s => s.Guid == 0).Sum(s => Cells(s));
                    if (inMainPack + cellsNeeded > capacity)
                    {
                        Say(session, $"{target.Name}'s main pack holds {inMainPack} of {capacity}; these would need {cellsNeeded} more cells. Nothing was made.");
                        return;
                    }

                    var db = DatabaseManager.Shard.BaseDatabase;
                    var made = new List<string>();
                    var failed = new List<string>();
                    foreach (var spec in specs)
                    {
                        if (spec.Guid != 0)
                        {
                            var r = Reduce(possessions, spec, db, out var why);
                            if (r != null) made.Add(r); else failed.Add($"0x{spec.Guid:X8} ({why})");
                            continue;
                        }
                        int copies = Cells(spec);
                        for (int i = 0; i < copies; i++)
                        {
                            var wo = WorldObjectFactory.CreateNewWorldObject(spec.Wcid);
                            if (wo == null) { failed.Add($"{spec.Wcid} (no object from the factory)"); break; }
                            if (copies == 1 && spec.Count > 1)
                                wo.SetStackSize(Math.Min(spec.Count, wo.MaxStackSize ?? 1));
                            wo.Bonded = BondedStatus.Bonded;
                            wo.ContainerId = guid;
                            wo.PlacementPosition = ++lastPos;
                            if (spec.Palette != null) wo.PaletteTemplate = spec.Palette;
                            if (spec.Shade != null) wo.Shade = spec.Shade;
                            if (spec.MaxLevel != null)
                            {
                                wo.ItemMaxLevel = spec.MaxLevel;
                                wo.IconOverlayId = LootGenerationFactory.IconOverlay_ItemMaxLevel[spec.MaxLevel.Value - 1];
                            }
                            // FULLY LEVELLED, using ACE's own threshold rather than a number typed
                            // here. A levelable item (a cloak, an aetheria) carries its level in
                            // ItemTotalXp and ACE derives the level back out of it, so setting the
                            // level directly would be a lie the next XP grant corrects.
                            // ExperienceSystem.ItemLevelToTotalXP is the same function the level-up
                            // path measures against, so "maxed" means exactly what wearing it to the
                            // cap would have meant.
                            if (spec.Maxed)
                            {
                                var cap = wo.ItemMaxLevel ?? 0;
                                if (cap <= 0 || wo.ItemXpStyle == null || wo.ItemBaseXp == null)
                                    failed.Add($"{wo.Name} 0x{wo.Guid.Full:X8} (maxed asked for, but it is not a levelable item)");
                                else
                                    wo.ItemTotalXp = (long)ExperienceSystem.ItemLevelToTotalXP(
                                        cap, (ulong)wo.ItemBaseXp.Value, cap, (ItemXpStyle)wo.ItemXpStyle.Value);
                            }
                            if (db.SaveBiota(wo.Biota, wo.BiotaDatabaseLock))
                                made.Add($"{wo.Name}{(wo.StackSize > 1 ? $" ({wo.StackSize})" : "")} 0x{wo.Guid.Full:X8}");
                            else
                                failed.Add($"{wo.Name} 0x{wo.Guid.Full:X8} (SaveBiota false)");
                        }
                    }

                    var summary = $"{by} granted to {target.Name} (0x{guid:X8}, offline) {made.Count} bonded item(s) or reduction(s): {(made.Count == 0 ? "none" : string.Join(", ", made))}";
                    if (failed.Count > 0)
                    {
                        summary += $"; FAILED {failed.Count}: {string.Join(", ", failed)}";
                        Mod.Log.Error($"[RevivalGuard] {summary}");
                    }
                    else
                        Mod.Log.Info($"[RevivalGuard] {summary}");
                    Say(session, $"{(failed.Count > 0 ? "Partly done" : "Done")}: {summary.Substring(by.Length + 1)}. They are in the main pack at the next login.");
                    PlayerManager.BroadcastToAuditChannel(session?.Player, summary);
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] Bestow: {name} threw", e);
                    Say(session, $"@grantgear threw ({e.GetType().Name}: {e.Message}); items written before it stand. See the server log.");
                }
            });
        }

        /// <summary>Main-pack cells a spec takes: one for a stackable weenie however many copies, N otherwise.</summary>
        static int Cells(Spec s)
        {
            if (s.Count <= 1) return 1;
            var weenie = DatabaseManager.World.GetCachedWeenie(s.Wcid);
            int max = weenie?.GetProperty(PropertyInt.MaxStackSize) ?? 1;
            return max > 1 ? 1 : s.Count;
        }

        /// <summary>Reduce a held armor piece's coverage in the database, offline. The mapping is ACE's
        /// own Tailoring.TailorReduceArmor (Entity/Tailoring.cs:385-465), tool by tool: main keeps
        /// the chest (else the upper arms, else the abdomen), lower keeps the lower arms, lower legs
        /// or feet, middle keeps the upper legs. ValidLocations and ClothingPriority are set to the
        /// one slot and its CoverageMask exactly as that method sets them, and a piece that is worn
        /// has its CurrentWieldedLocation brought down to the same slot (ACE writes clothing's
        /// wield location as its ValidLocations, Player_Inventory.cs "if (item is Clothing)").
        ///
        /// The one rule of ACE's not carried over is "loot armor only" (ItemWorkmanship != null),
        /// and retail's tool had the same rule (wiki: "Use this tool on any loot generated
        /// multi-slot armor"). It is skipped on purpose: the owner's retail chest piece was a
        /// loot coat wearing the Luminescent Thaumaturgic look through tailoring, reduced to the
        /// chest, and the quest coat granted here stands in for it because that loot roll cannot
        /// be recovered (an internal note). Reducing the stand-in is the same end state.
        /// Nothing else on the row changes: guid, dye, Bonded, spells, armor level.</summary>
        static string Reduce(ACE.Database.Entity.PossessedBiotas possessions, Spec spec, ShardDatabase db, out string why)
        {
            why = null;
            var row = possessions.Inventory.Concat(possessions.WieldedItems).FirstOrDefault(b => b.Id == spec.Guid);
            if (row == null) { why = "not held by this character"; return null; }
            var ent = BiotaConverter.ConvertToEntityBiota(row);
            var ints = ent.PropertiesInt;
            if (ints == null) { why = "no int properties on the row"; return null; }
            var itemName = ent.PropertiesString != null && ent.PropertiesString.TryGetValue(PropertyString.Name, out var n) ? n : "item";
            var valid = ints.TryGetValue(PropertyInt.ValidLocations, out int vlRaw) ? (EquipMask)vlRaw : EquipMask.None;

            EquipMask to = EquipMask.None;
            CoverageMask cover = CoverageMask.Unknown;
            switch (spec.Reduce)
            {
                case "main":
                    if (valid.HasFlag(EquipMask.ChestArmor)) { to = EquipMask.ChestArmor; cover = CoverageMask.OuterwearChest; }
                    else if (valid.HasFlag(EquipMask.UpperArmArmor)) { to = EquipMask.UpperArmArmor; cover = CoverageMask.OuterwearUpperArms; }
                    else if (valid.HasFlag(EquipMask.AbdomenArmor)) { to = EquipMask.AbdomenArmor; cover = CoverageMask.OuterwearAbdomen; }
                    break;
                case "lower":
                    if (valid.HasFlag(EquipMask.ChestArmor)) { why = "chest armor reduces only to the chest (reduce=main)"; return null; }
                    if (valid.HasFlag(EquipMask.UpperArmArmor)) { to = EquipMask.LowerArmArmor; cover = CoverageMask.OuterwearLowerArms; }
                    else if (valid.HasFlag(EquipMask.UpperLegArmor)) { to = EquipMask.LowerLegArmor; cover = CoverageMask.OuterwearLowerLegs; }
                    else if (valid.HasFlag(EquipMask.LowerLegArmor | EquipMask.FootWear)) { to = EquipMask.FootWear; cover = CoverageMask.Feet; }
                    break;
                case "middle":
                    if (valid.HasFlag(EquipMask.UpperLegArmor)) { to = EquipMask.UpperLegArmor; cover = CoverageMask.OuterwearUpperLegs; }
                    break;
            }
            if (to == EquipMask.None) { why = $"{itemName} covers {valid} ({(uint)valid:X}); the {spec.Reduce} reduction has nothing to keep there"; return null; }
            if (valid == to) { why = $"{itemName} already covers only {to}"; return null; }

            ints[PropertyInt.ValidLocations] = (int)to;
            ints[PropertyInt.ClothingPriority] = (int)cover;
            bool worn = ints.TryGetValue(PropertyInt.CurrentWieldedLocation, out int cur) && cur != 0;
            if (worn) ints[PropertyInt.CurrentWieldedLocation] = (int)to;
            if (!db.SaveBiota(ent, new ReaderWriterLockSlim())) { why = "SaveBiota false"; return null; }
            return $"{itemName} 0x{spec.Guid:X8} reduced from {valid} to {to}{(worn ? ", worn" : "")}";
        }

        /// <summary>wcid[xN][:key=value,...] or 0xguid:reduce=main|lower|middle; null with the reason when it does not parse.</summary>
        static Spec Parse(string word, out string why)
        {
            why = null;
            var spec = new Spec { Text = word };
            var head = word;
            var colon = word.IndexOf(':');
            if (head.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                // An item the character already holds: the only thing to do to one is reduce it.
                var hex = colon >= 0 ? head.Substring(2, colon - 2) : head.Substring(2);
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out spec.Guid) || spec.Guid == 0) { why = $"'{head}' is not an item guid"; return null; }
                if (colon < 0 || !word.Substring(colon + 1).StartsWith("reduce=", StringComparison.OrdinalIgnoreCase)) { why = "a held item takes exactly :reduce=main, :reduce=lower or :reduce=middle"; return null; }
                spec.Reduce = word.Substring(colon + 8).Trim().ToLowerInvariant();
                if (spec.Reduce != "main" && spec.Reduce != "lower" && spec.Reduce != "middle") { why = $"reduce={spec.Reduce} is not main, lower or middle"; return null; }
                return spec;
            }
            if (colon >= 0)
            {
                head = word.Substring(0, colon);
                foreach (var kv in word.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = kv.IndexOf('=');
                    if (eq < 0) { why = $"'{kv}' is not key=value"; return null; }
                    var key = kv.Substring(0, eq).Trim().ToLowerInvariant();
                    var val = kv.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "pt":
                            if (!int.TryParse(val, out int pt) || pt < 0) { why = $"pt={val} is not a palette template"; return null; }
                            spec.Palette = pt; break;
                        case "shade":
                            if (!double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sh) || sh < 0 || sh > 1) { why = $"shade={val} is not 0..1"; return null; }
                            spec.Shade = sh; break;
                        case "maxlvl":
                            if (!int.TryParse(val, out int lvl) || lvl < 1 || lvl > LootGenerationFactory.IconOverlay_ItemMaxLevel.Count) { why = $"maxlvl={val} is not 1..{LootGenerationFactory.IconOverlay_ItemMaxLevel.Count}"; return null; }
                            spec.MaxLevel = lvl; break;
                        case "maxed":
                            if (!bool.TryParse(val, out bool mx)) { why = $"maxed={val} is not true or false"; return null; }
                            spec.Maxed = mx; break;
                        default:
                            why = $"'{key}' is not pt, shade, maxlvl or maxed"; return null;
                    }
                }
            }
            var x = head.IndexOf('x');
            if (x >= 0)
            {
                if (!int.TryParse(head.Substring(x + 1), out int n) || n < 1 || n > MAX_COPIES) { why = $"x{head.Substring(x + 1)} is not 1..{MAX_COPIES}"; return null; }
                spec.Count = n;
                head = head.Substring(0, x);
            }
            if (!uint.TryParse(head, out spec.Wcid) || spec.Wcid == 0) { why = $"'{head}' is not a weenie class id"; return null; }
            return spec;
        }
    }
}
