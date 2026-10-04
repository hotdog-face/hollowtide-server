using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE ALLEGIANCE HALL (owner, 2026-09-27: mansions are "mostly useless inside"; he approved the
    /// mansion ideas and wanted "the best ones to start"). Ours; docs/MANSIONS-AND-PK.md.
    ///
    /// Everything here is built from what a mansion already has, so nothing is added to the world
    /// database: a storage chest becomes the ALLEGIANCE BANK (MansionBank.cs), and two hook items the
    /// monarch makes with a command and hangs like any other decoration:
    ///
    ///   Allegiance Hall Board  a Plaque (wcid 11970, a wall-hook Book). Reading it shows the
    ///                          allegiance's name, monarch, message of the day, the roster with who is
    ///                          online, and the kills on the mansion's grounds (MansionPk.cs). The
    ///                          pages are written at read time and never stored.
    ///   Allegiance Hall Portal a portal device (wcid 26588, a floor/yard-hook Hooker in HookGroup
    ///                          PortalItems, so ACE's own rule keeps it working only in a mansion).
    ///                          It sends the user to the spot the monarch set with /mansion portal set.
    ///
    ///     /mansion                       what is set up, and the commands
    ///     /mansion board | portal device the two hook items (monarch owning a mansion)
    ///     /mansion portal [set | clear]  where the portal leads; set = where you stand
    ///     /mansion bank [on | off | rank 1-3 | log]
    ///     /mansion kills                 the kills on your allegiance's mansion grounds
    ///
    /// Our client sends an unknown slash command to the server as "@..." (bridge, Program.AcClient.Net),
    /// so /mansion and @mansion are the same command. State lives in Mansions/mansions.json beside the
    /// dll (ModFolder): bank designations and logs, portal destinations, kill records.
    /// </summary>
    public static class Mansions
    {
        internal const uint BOARD_WCID = 11970;      // "Plaque", Book, HookType Wall, 1 page of 1000
        internal const uint PORTAL_WCID = 26588;     // "Portal to Kivik Lir's Temple", Hooker, HookGroup PortalItems
        internal const PropertyBool BOARD = (PropertyBool)29110;
        internal const PropertyBool PORTAL = (PropertyBool)29111;
        const int MAKE_GAP_SECONDS = 60;

        static readonly ConcurrentDictionary<uint, long> s_LastMake = new ConcurrentDictionary<uint, long>();

        internal static void Register()
        {
            Store.Load();
            CommandManager.TryAddCommand(Handle, "mansion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Your mansion's allegiance hall: the bank, the hall board, the hall portal and the kills on its grounds.",
                "[board | portal [device|set|clear] | bank [on|off|rank <1-3>|log] | kills]");
            MansionPk.RegisterProperty();
        }

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            var args = (parameters ?? new string[0]).Select(a => a.ToLowerInvariant()).ToArray();
            string verb = args.Length > 0 ? args[0] : "";
            try
            {
                switch (verb)
                {
                    case "board": MakeItem(p, BOARD_WCID); return;
                    case "portal": PortalCommand(p, args.Skip(1).ToArray()); return;
                    case "bank": MansionBank.Command(p, args.Skip(1).ToArray()); return;
                    case "kills": MansionPk.KillsCommand(p); return;
                    default: Status(p); return;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] Mansions: /mansion {string.Join(" ", args)} by {p.Name}: {e}");
                Say(p, "That did not work. It has been logged for staff.");
            }
        }

        // ---------------------------------------------------------------- shared helpers

        internal static void Say(Player p, string line, ChatMessageType type = ChatMessageType.Broadcast) =>
            p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, type));

        /// <summary>The mansion this player owns, or null (with the reason said to them).</summary>
        internal static House OwnedMansion(Player p, bool quiet = false)
        {
            var h = p.House;
            if (h == null || h.HouseType != HouseType.Mansion || h.HouseOwner != p.Guid.Full)
            {
                if (!quiet) Say(p, "Only the owner of a mansion can do that.");
                return null;
            }
            return h;
        }

        /// <summary>The allegiance of the player who owns a root house, and that owner. Null
        /// allegiance for a monarch whose vassals have all left.</summary>
        internal static Allegiance AllegianceOfOwner(uint ownerGuid, out IPlayer owner)
        {
            owner = ownerGuid != 0 ? PlayerManager.FindByGuid(ownerGuid) : null;
            return owner != null ? AllegianceManager.GetAllegiance(owner) : null;
        }

        internal static string AllegianceLabel(Allegiance a, IPlayer fallbackMonarch = null)
        {
            if (a != null && !string.IsNullOrWhiteSpace(a.AllegianceName)) return a.AllegianceName;
            var m = a?.Monarch?.Player ?? fallbackMonarch;
            return m != null ? $"the allegiance of {m.Name}" : "no allegiance";
        }

        /// <summary>Every online member of the allegiance that holds this root house, and its owner.</summary>
        internal static List<Player> Defenders(uint ownerGuid)
        {
            var list = new List<Player>();
            var a = AllegianceOfOwner(ownerGuid, out _);
            if (a != null) list.AddRange(a.OnlinePlayers);
            var o = PlayerManager.GetOnlinePlayer(ownerGuid);
            if (o != null && !list.Contains(o)) list.Add(o);
            return list;
        }

        internal static string Where(ACE.Entity.Position pos)
        {
            if (pos == null) return "somewhere";
            var map = pos.GetMapCoordStr();
            if (map != null && !pos.Indoors) return map;
            return map != null ? $"indoors near {map}" : $"underground (0x{pos.LandblockId.Landblock:X4})";
        }

        // ---------------------------------------------------------------- /mansion

        static void Status(Player p)
        {
            var h = OwnedMansion(p, quiet: true);
            if (h == null)
            {
                Say(p, "The allegiance hall commands are for the owner of a mansion; members can use /mansion kills and /mansion bank log.");
                Say(p, "Usage: /mansion board | portal [device | set | clear] | bank [on | off | rank <1-3> | log] | kills");
                return;
            }
            var bank = Store.BankOfHouse(h.Guid.Full);
            var dest = Store.PortalOf(p.Guid.Full);
            Say(p, "Your allegiance hall:");
            Say(p, bank != null ? $"  Allegiance bank: set; officers of rank {bank.Rank} and up may withdraw." : "  Allegiance bank: not set. Open a storage chest and type /mansion bank on.");
            Say(p, dest != null ? $"  Hall portal leads to {dest.Where}." : "  Hall portal: not set. Stand where it should lead and type /mansion portal set.");
            Say(p, "  /mansion board and /mansion portal device make the two items to hang on your hooks. Turn hooks off (/house hooks off) so visitors can use them.");
        }

        static void MakeItem(Player p, uint wcid)
        {
            if (OwnedMansion(p) == null) return;
            long now = (long)ACE.Common.Time.GetUnixTime();
            if (s_LastMake.TryGetValue(p.Guid.Full, out var last) && now - last < MAKE_GAP_SECONDS)
            { Say(p, "You have made one very recently. Wait a minute and try again."); return; }

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Say(p, "That item could not be made."); return; }
            if (wcid == BOARD_WCID)
            {
                wo.SetProperty(PropertyString.Name, "Allegiance Hall Board");
                wo.SetProperty(PropertyString.ShortDesc, "This item can be used on a wall hook.");
                wo.SetProperty(PropertyString.LongDesc, "Hung in a mansion, this board shows the allegiance of the mansion's owner: its members and who is online, the message of the day, and the kills on the mansion's grounds.");
                wo.SetProperty(BOARD, true);
            }
            else
            {
                wo.SetProperty(PropertyString.Name, "Allegiance Hall Portal");
                wo.SetProperty(PropertyString.LongDesc, "This device leads to the place the monarch has chosen. It works only when hung on a floor or yard hook in a mansion.");
                wo.SetProperty(PORTAL, true);
            }
            wo.SetProperty(PropertyInt.Value, 0);   // made for nothing, so it is worth nothing to a vendor
            if (!p.TryCreateInInventoryWithNetworking(wo))
            {
                wo.Destroy();
                Say(p, "You have no room for it.");
                return;
            }
            s_LastMake[p.Guid.Full] = now;
            Say(p, wcid == BOARD_WCID
                ? "You receive an Allegiance Hall Board. Hang it on a wall hook in your mansion."
                : "You receive an Allegiance Hall Portal. Hang it on a floor or yard hook in your mansion, then stand where it should lead and type /mansion portal set.");
        }

        static void PortalCommand(Player p, string[] args)
        {
            string sub = args.Length > 0 ? args[0] : "";
            if (sub == "device") { MakeItem(p, PORTAL_WCID); return; }
            if (sub == "set") { SetPortal(p); return; }
            if (sub == "clear")
            {
                if (OwnedMansion(p) == null) return;
                Store.SetPortal(p.Guid.Full, null);
                Say(p, "Your hall portal is dormant again.");
                return;
            }
            uint monarch = p.Allegiance?.MonarchId ?? p.Guid.Full;
            var dest = Store.PortalOf(monarch);
            Say(p, dest != null ? $"Your allegiance's hall portal leads to {dest.Where} (set by {dest.SetBy})." : "Your allegiance's hall portal leads nowhere yet.");
        }

        static void SetPortal(Player p)
        {
            if (OwnedMansion(p) == null) return;
            if (p.PKTimerActive) { p.Session.Network.EnqueueSend(new GameEventWeenieError(p.Session, WeenieError.YouHaveBeenInPKBattleTooRecently)); return; }
            if (p.RecallsDisabled) { p.Session.Network.EnqueueSend(new GameEventWeenieError(p.Session, WeenieError.ExitTrainingAcademyToUseCommand)); return; }
            if (p.Teleporting || p.Location == null) { Say(p, "You cannot do that while travelling."); return; }
            // NOT ON ANYONE'S HOUSING GROUNDS: a portal onto a rival's lawn would drop a whole
            // allegiance on a mansion in one step. Raids walk or recall in, as they always have.
            if (MansionIndex.HasHouses(p.Location.LandblockId.Landblock))
            { Say(p, "You cannot tie the hall portal to a place where dwellings stand."); return; }

            var pos = new ACE.Entity.Position(p.Location);
            Store.SetPortal(p.Guid.Full, new PortalRec
            {
                Cell = pos.Cell, X = pos.PositionX, Y = pos.PositionY, Z = pos.PositionZ,
                QW = pos.RotationW, QX = pos.RotationX, QY = pos.RotationY, QZ = pos.RotationZ,
                Where = Where(pos), SetBy = p.Name, At = (long)ACE.Common.Time.GetUnixTime(),
            });
            Say(p, $"Your hall portal now leads here, {Where(pos)}.");
            Mod.Log.Info($"[RevivalGuard] Mansions: {p.Name} set the hall portal to {pos.ToLOCString()}");
        }

        // ---------------------------------------------------------------- the hall portal, in use

        /// <summary>The portal device is a Hooker whose weenie casts a fixed portal spell from its Use
        /// emote. Ours is handled here instead, before any of that: ACE's own Hooker checks (hooked,
        /// the house's permission, portal items only in a mansion), then the recall rules a portal
        /// device answers to (PK timer, busy, the Academy), then the teleport.</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.OnActivate))]
        static class PortalUse
        {
            static bool Prefix(WorldObject __instance, WorldObject activator)
            {
                if (!(__instance is Hooker hooker) || !(__instance.GetProperty(PORTAL) ?? false)) return true;
                try { Use(hooker, activator as Player); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: hall portal use: {e}"); }
                return false;
            }

            static void Use(Hooker hooker, Player player)
            {
                if (player == null) return;
                var result = hooker.CheckUseRequirements(player);
                if (!result.Success)
                {
                    if (result.Message != null) player.Session.Network.EnqueueSend(result.Message);
                    return;
                }
                hooker.IsHooked(player, out var hook);
                uint owner = hook?.House?.RootHouse?.HouseOwner ?? 0;
                var dest = owner != 0 ? Store.PortalOf(owner) : null;
                if (dest == null)
                {
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "The portal is dormant. The monarch has not chosen where it leads."));
                    return;
                }
                if (player.IsOlthoiPlayer) { player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.OlthoiCanOnlyRecallToLifestone)); return; }
                if (player.PKTimerActive) { player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently)); return; }
                if (player.RecallsDisabled) { player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.ExitTrainingAcademyToUseCommand)); return; }
                if (player.TooBusyToRecall) { player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YoureTooBusy)); return; }

                var pos = new ACE.Entity.Position(dest.Cell, dest.X, dest.Y, dest.Z, dest.QX, dest.QY, dest.QZ, dest.QW);
                Mod.Log.Info($"[RevivalGuard] Mansions: {player.Name} took the hall portal of 0x{owner:X8} to {pos.ToLOCString()}");
                player.Teleport(pos, true);
            }
        }

        // ---------------------------------------------------------------- the hall board

        /// <summary>The pages, written for this reader now. Both ways a book is read end here: a use
        /// (from the pack, or through a hook with hooks off) and the client's own BookData request.</summary>
        internal static List<PropertiesBookPageData> BoardPages(Book board, Player reader, out string title)
        {
            uint ownerGuid = 0, root = 0;
            if (board.OwnerId.HasValue && reader.CurrentLandblock?.GetObject(board.OwnerId.Value) is Hook hook)
            {
                var rh = hook.House?.RootHouse;
                ownerGuid = rh?.HouseOwner ?? 0;
                root = rh?.Guid.Full ?? 0;
            }
            if (ownerGuid == 0)
            {
                // in a pack: the reader's own allegiance, and its monarch's mansion
                ownerGuid = reader.Allegiance?.MonarchId ?? reader.Guid.Full;
                root = PlayerManager.FindByGuid(ownerGuid)?.HouseInstance ?? 0;
            }

            var a = AllegianceOfOwner(ownerGuid, out var owner);
            title = AllegianceLabel(a, owner);
            if (title.StartsWith("the ")) title = char.ToUpper(title[0]) + title.Substring(1);
            var pages = new List<string>();

            var sb = new StringBuilder();
            sb.Append(title).Append("\n\n");
            if (owner != null) sb.Append("Monarch: ").Append(owner.Name).Append('\n');
            if (a != null)
            {
                int online = a.Members.Keys.Count(g => PlayerManager.GetOnlinePlayer(g) != null);
                sb.Append($"Members: {a.TotalMembers}, {online} online now\n\n");
                sb.Append("Message of the day:\n");
                sb.Append(string.IsNullOrWhiteSpace(a.AllegianceMotd) ? "(none)" : a.AllegianceMotd.Trim());
                if (!string.IsNullOrWhiteSpace(a.AllegianceMotd) && !string.IsNullOrWhiteSpace(a.AllegianceMotdSetBy))
                    sb.Append("\n  (").Append(a.AllegianceMotdSetBy).Append(')');
            }
            else sb.Append("\nThis monarch has no followers.");
            pages.Add(sb.ToString());

            if (a != null)
            {
                var rows = a.Members.Values
                    .Where(n => n?.Player != null)
                    .Select(n => new
                    {
                        n.Player.Name, Level = n.Player.Level ?? 1, n.Rank, Monarch = n.IsMonarch,
                        Officer = n.Player.AllegianceOfficerRank ?? 0,
                        Online = PlayerManager.GetOnlinePlayer(n.PlayerGuid) != null,
                    })
                    .OrderByDescending(r => r.Online).ThenByDescending(r => r.Monarch).ThenByDescending(r => r.Officer)
                    .ThenByDescending(r => r.Rank).ThenBy(r => r.Name)
                    .ToList();
                const int PER_PAGE = 9;
                for (int i = 0; i < rows.Count; i += PER_PAGE)
                {
                    var r = new StringBuilder(i == 0 ? "Members (online first)\n\n" : "Members, continued\n\n");
                    foreach (var m in rows.Skip(i).Take(PER_PAGE))
                    {
                        string role = m.Monarch ? "Monarch" : m.Officer > 0 ? a.GetOfficerTitle((AllegianceOfficerLevel)m.Officer) : "";
                        r.Append(m.Online ? "* " : "  ").Append(m.Name)
                         .Append($"\n    level {m.Level}, rank {m.Rank}")
                         .Append(role.Length > 0 ? ", " + role : "").Append('\n');
                    }
                    r.Append("\n* online now");
                    pages.Add(r.ToString());
                }
            }

            pages.AddRange(MansionPk.KillPages(root));

            title = title.Length > 60 ? title.Substring(0, 60) : title;
            return pages.Select(t => new PropertiesBookPageData
            {
                AuthorId = 0, AuthorName = "", AuthorAccount = "", IgnoreAuthor = false,
                PageText = t.Length > 1000 ? t.Substring(0, 1000) : t,
            }).ToList();
        }

        static bool IsBoard(WorldObject wo) => wo is Book && (wo.GetProperty(BOARD) ?? false);

        static void SendBoard(Book board, Player reader)
        {
            var pages = BoardPages(board, reader, out var title);
            // authorId = the board itself, so the client titles the window with the inscription
            // (gmBookUI::OpenBook: title = inscription when scribeID != 0); maxPages = the pages
            // written, so turning past the end asks for no new page. The scribe NAME is sent too:
            // our client's reader keys the same rule on the name (the bridge carries no scribe id),
            // and with it empty a hooked board drew a blank title bar (seen live 2026-09-28).
            reader.Session.Network.EnqueueSend(new GameEventBookDataResponse(reader.Session, board.Guid.Full,
                1000, pages.Count, pages, title, board.Guid.Full, "Allegiance Hall Board", false));
        }

        [HarmonyPatch(typeof(Book), nameof(Book.ActOnUse))]
        static class BoardRead
        {
            static bool Prefix(Book __instance, WorldObject worldObject)
            {
                if (!IsBoard(__instance) || !(worldObject is Player p)) return true;
                try { SendBoard(__instance, p); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: hall board read: {e}"); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ReadBook))]
        static class BoardData
        {
            static bool Prefix(Player __instance, uint bookGuid)
            {
                var b = __instance.FindObject(bookGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook) as Book;
                if (!IsBoard(b)) return true;
                try { SendBoard(b, __instance); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: hall board data: {e}"); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ReadBookPage))]
        static class BoardPage
        {
            static bool Prefix(Player __instance, uint bookGuid, int pageNum)
            {
                var b = __instance.FindObject(bookGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook) as Book;
                if (!IsBoard(b)) return true;
                try
                {
                    var pages = BoardPages(b, __instance, out _);
                    if (pageNum >= 0 && pageNum < pages.Count)
                        __instance.Session.Network.EnqueueSend(new GameEventBookPageDataResponse(__instance.Session, bookGuid, pageNum, pages[pageNum]));
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: hall board page: {e}"); }
                return false;
            }
        }

        /// <summary>Nobody writes on the board: its pages are the allegiance's, made at read time.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBookAddPage))]
        static class BoardNoAdd
        {
            static bool Prefix(Player __instance, uint bookGuid)
            {
                if (!IsBoard(__instance.FindObject(bookGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook))) return true;
                __instance.Session.Network.EnqueueSend(new GameEventBookAddPageResponse(__instance.Session, bookGuid, -1, false));
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBookModifyPage))]
        static class BoardNoModify
        {
            static bool Prefix(Player __instance, uint bookGuid, int pageId)
            {
                if (!IsBoard(__instance.FindObject(bookGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook))) return true;
                __instance.Session.Network.EnqueueSend(new GameEventBookModifyPageResponse(__instance.Session, bookGuid, pageId, false));
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBookDeletePage))]
        static class BoardNoDelete
        {
            static bool Prefix(Player __instance, uint bookGuid, int pageId)
            {
                if (!IsBoard(__instance.FindObject(bookGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook))) return true;
                __instance.Session.Network.EnqueueSend(new GameEventBookDeletePageResponse(__instance.Session, bookGuid, pageId, false));
                return false;
            }
        }
    }

    /// <summary>
    /// WHICH LANDBLOCKS ARE A MANSION'S GROUNDS. HouseCell.RootGuids (ACE's own table) names every
    /// house instance and its root, so its keys give the landblocks that hold any dwelling; the
    /// world's cached instances for a landblock say which of those houses are mansions (weenie
    /// HouseType 3). A mansion's landscape block (the house, lawn and yard) and its basement block
    /// both map to the same root house. Only the world database's own cache is read, so this is
    /// safe from any thread, and a landblock is looked up once.
    /// </summary>
    internal static class MansionIndex
    {
        static HashSet<ushort> s_HouseBlocks;
        static readonly ConcurrentDictionary<ushort, uint> s_Root = new ConcurrentDictionary<ushort, uint>();
        static readonly object s_Lock = new object();
        static Dictionary<uint, uint> s_Owners = new Dictionary<uint, uint>();
        static double s_OwnersAt;

        internal static bool HasHouses(ushort landblock)
        {
            if (s_HouseBlocks == null)
                lock (s_Lock)
                    s_HouseBlocks ??= new HashSet<ushort>(HouseCell.RootGuids.Keys.Select(g => (ushort)((g >> 12) & 0xFFFF)));
            return s_HouseBlocks.Contains(landblock);
        }

        /// <summary>The root (landscape) house guid of the mansion on this landblock, or 0.</summary>
        internal static uint MansionRootAt(ushort landblock)
        {
            if (s_Root.TryGetValue(landblock, out var r)) return r;
            uint root = 0;
            if (HasHouses(landblock))
            {
                foreach (var inst in DatabaseManager.World.GetCachedInstancesByLandblock(landblock))
                {
                    if (!HouseCell.RootGuids.TryGetValue(inst.Guid, out var rg)) continue;
                    var w = DatabaseManager.World.GetCachedWeenie(inst.WeenieClassId);
                    if (w != null && (w.GetProperty(PropertyInt.HouseType) ?? 0) == (int)HouseType.Mansion) { root = rg; break; }
                }
            }
            s_Root[landblock] = root;
            return root;
        }

        /// <summary>The character who owns this root house, from every character's HouseInstance
        /// (refreshed each 30 s; a purchase or abandon shows within that).</summary>
        internal static uint OwnerOf(uint rootGuid)
        {
            if (rootGuid == 0) return 0;
            var now = ACE.Common.Time.GetUnixTime();
            lock (s_Lock)
            {
                if (now - s_OwnersAt > 30)
                {
                    var map = new Dictionary<uint, uint>();
                    foreach (var p in PlayerManager.GetAllPlayers())
                        if (p.HouseInstance is uint h && h != 0) map[h] = p.Guid.Full;
                    s_Owners = map;
                    s_OwnersAt = now;
                }
                return s_Owners.TryGetValue(rootGuid, out var o) ? o : 0;
            }
        }
    }

    // -------------------------------------------------------------------- persisted state

    internal sealed class BankRec { public uint House { get; set; } public uint Owner { get; set; } public int Rank { get; set; } = 1; }
    internal sealed class PortalRec
    {
        public uint Cell { get; set; }
        public float X { get; set; } public float Y { get; set; } public float Z { get; set; }
        public float QW { get; set; } public float QX { get; set; } public float QY { get; set; } public float QZ { get; set; }
        public string Where { get; set; } public string SetBy { get; set; } public long At { get; set; }
    }
    internal sealed class KillRec
    {
        public long At { get; set; } public string Victim { get; set; } public string Killer { get; set; }
        public string KillerAllegiance { get; set; } public string Where { get; set; }
    }
    internal sealed class MansionState
    {
        public Dictionary<uint, BankRec> Banks { get; set; } = new Dictionary<uint, BankRec>();        // storage guid
        public Dictionary<uint, List<string>> BankLog { get; set; } = new Dictionary<uint, List<string>>(); // storage guid
        public Dictionary<uint, PortalRec> Portals { get; set; } = new Dictionary<uint, PortalRec>();   // monarch guid
        public Dictionary<uint, List<KillRec>> Kills { get; set; } = new Dictionary<uint, List<KillRec>>(); // root house guid
    }

    internal static class Store
    {
        const int BANK_LOG_KEEP = 200, KILLS_KEEP = 60;
        static readonly object s_Lock = new object();
        static MansionState s_State = new MansionState();
        static string s_Path;

        internal static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("Mansions");
                if (dir == null) return;
                s_Path = System.IO.Path.Combine(dir, "mansions.json");
                if (File.Exists(s_Path))
                    s_State = JsonSerializer.Deserialize<MansionState>(File.ReadAllText(s_Path)) ?? new MansionState();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: cannot read {s_Path}: {e.Message}; starting empty"); }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State));
                File.Move(tmp, s_Path, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mansions: cannot write {s_Path}: {e.Message}"); }
        }

        internal static BankRec Bank(uint storageGuid) { lock (s_Lock) return s_State.Banks.TryGetValue(storageGuid, out var b) ? b : null; }

        internal static BankRec BankOfHouse(uint rootHouse)
        { lock (s_Lock) return s_State.Banks.Values.FirstOrDefault(b => b.House == rootHouse); }

        internal static uint BankGuidOfHouse(uint rootHouse)
        { lock (s_Lock) return s_State.Banks.FirstOrDefault(kv => kv.Value.House == rootHouse).Key; }

        internal static void SetBank(uint storageGuid, BankRec rec)
        {
            lock (s_Lock)
            {
                if (rec != null)
                    foreach (var k in s_State.Banks.Where(kv => kv.Value.House == rec.House).Select(kv => kv.Key).ToList())
                        s_State.Banks.Remove(k);
                if (rec == null) s_State.Banks.Remove(storageGuid); else s_State.Banks[storageGuid] = rec;
                Save();
            }
        }

        internal static void BankLogAdd(uint storageGuid, string line)
        {
            lock (s_Lock)
            {
                if (!s_State.BankLog.TryGetValue(storageGuid, out var l)) s_State.BankLog[storageGuid] = l = new List<string>();
                l.Add(line);
                if (l.Count > BANK_LOG_KEEP) l.RemoveRange(0, l.Count - BANK_LOG_KEEP);
                Save();
            }
        }

        internal static List<string> BankLog(uint storageGuid, int last)
        { lock (s_Lock) return s_State.BankLog.TryGetValue(storageGuid, out var l) ? l.Skip(Math.Max(0, l.Count - last)).ToList() : new List<string>(); }

        internal static PortalRec PortalOf(uint monarch) { lock (s_Lock) return s_State.Portals.TryGetValue(monarch, out var p) ? p : null; }

        internal static void SetPortal(uint monarch, PortalRec rec)
        {
            lock (s_Lock)
            {
                if (rec == null) s_State.Portals.Remove(monarch); else s_State.Portals[monarch] = rec;
                Save();
            }
        }

        internal static void KillAdd(uint rootHouse, KillRec k)
        {
            lock (s_Lock)
            {
                if (!s_State.Kills.TryGetValue(rootHouse, out var l)) s_State.Kills[rootHouse] = l = new List<KillRec>();
                l.Add(k);
                if (l.Count > KILLS_KEEP) l.RemoveRange(0, l.Count - KILLS_KEEP);
                Save();
            }
        }

        internal static List<KillRec> Kills(uint rootHouse)
        { lock (s_Lock) return s_State.Kills.TryGetValue(rootHouse, out var l) ? l.ToList() : new List<KillRec>(); }
    }
}
