using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACE.Database;
using ACE.Database.Adapter;
using ACE.Database.Entity;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;
using EntityBiota = ACE.Entity.Models.Biota;

namespace RevivalGuard
{
    /// <summary>
    /// @SNAPSHOT: A COPY OF A CHARACTER YOU CAN PUT BACK.
    ///
    /// From docs/ADMIN-AND-VENUE-IDEAS.md §2 item 5: "Before you let someone test a risky thing, take
    /// a copy; if it goes wrong, put it back." Deletes on this shard are not restorable
    /// (char_delete_time=0), PvP costs real items, and @copychar (ACE's starting point) makes a NEW
    /// character with a new id, which breaks everything keyed on the old one. This keeps the id.
    ///
    /// WHAT A SNAPSHOT IS. Everything the database holds for one character, read back through ACE's
    /// own loaders (the same calls @copychar and the login path make) and written as JSON beside the
    /// mod dll: the player biota (stats, skills, spells, enchantments, position, options), every
    /// possession's biota (worn and in packs, nested packs included) and the character rows (quest
    /// flags, titles, friends, shortcuts, spell bars, contracts, fill-comp book, squelches,
    /// gameplay options). Snapshots/&lt;id&gt;_&lt;Name&gt;/&lt;UTC stamp&gt;.json. An online character
    /// is saved first, so the file is what the database would hold if they logged out now.
    ///
    /// A snapshot is refused if it does not survive its own round trip (serialise, deserialise,
    /// serialise, compare): a file that cannot be read back exactly must not be offered for restore.
    ///
    /// WHAT RESTORE DOES, and what it deliberately does not:
    ///
    ///   * The character must be OFFLINE, with NO SESSION on the account (a client at the character
    ///     list holds stale objects that would be written back on login), NOT deleted or pending
    ///     deletion, and NOT in an allegiance (AllegianceManager keeps its own reference to the
    ///     offline player object and would save the old state over the new).
    ///   * The player's own row keeps its id and is updated in place through SaveBiota, the path
    ///     every save takes.
    ///   * Possessions: an item the character still holds (same id) is updated in place. An item the
    ///     character no longer holds comes back as a NEW COPY under a fresh guid, never under the old
    ///     id: ACE re-issues freed ids from sequence gaps, and an item that was traded or looted is
    ///     now somebody else's row. Their copy is not touched; the restored character gets a
    ///     duplicate. That is a dupe by construction, so the verb is Admin only, needs a confirm, and
    ///     names every re-issued item in the Audit channel. An item the character holds now that the
    ///     snapshot does not know is removed.
    ///   * Character rows are reconciled row by row on the tracked object (update in place, remove,
    ///     add), which is how ACE's own CharacterExtensions erase quests, and saved with
    ///     SaveCharacter. Name, account, deleted flags and login counters are kept as they are now.
    ///   * The in-memory OfflinePlayer is swapped, because the login path takes the player biota
    ///     from PlayerManager's offline copy, not from the database (WorldManager.PlayerEnterWorld).
    ///     Without the swap a restore would be silently undone on login.
    ///
    ///   @snapshot take &lt;name&gt;[, label]      write one (online or offline)
    ///   @snapshot list &lt;name&gt;               what is on disk, oldest first, numbered
    ///   @snapshot restore &lt;name&gt; &lt;n&gt;         stage a restore: shows what would change, changes nothing
    ///   @snapshot confirm                     do the staged restore (within two minutes)
    ///   @snapshot cancel                      drop it
    ///
    /// Take and list are Sentinel; restore is Admin. Everything runs on the database thread, in
    /// order with any save already queued. Admin command feedback is ours, not retail's.
    /// </summary>
    public static class CharacterSnapshot
    {
        const int VERSION = 1;
        const double CONFIRM_SECONDS = 120;
        const int MAX_LIST = 20;
        const int MAX_NAMES = 10;

        // ---- the file --------------------------------------------------------------------------

        public sealed class SnapshotFile
        {
            public int Version { get; set; } = VERSION;
            public string TakenAt { get; set; }
            public string TakenBy { get; set; }
            public string Label { get; set; }
            public uint CharacterId { get; set; }
            public string Name { get; set; }
            public uint AccountId { get; set; }
            public int Level { get; set; }
            public CharacterRows Character { get; set; }
            public EntityBiota Player { get; set; }
            public List<EntityBiota> Wielded { get; set; } = new List<EntityBiota>();
            public List<EntityBiota> Inventory { get; set; } = new List<EntityBiota>();
        }

        public sealed class CharacterRows
        {
            public int CharacterOptions1 { get; set; }
            public int CharacterOptions2 { get; set; }
            public byte[] GameplayOptions { get; set; }
            public uint SpellbookFilters { get; set; }
            public uint HairTexture { get; set; }
            public uint DefaultHairTexture { get; set; }
            public List<QuestRow> Quests { get; set; } = new List<QuestRow>();
            public List<uint> Titles { get; set; } = new List<uint>();
            public List<uint> Friends { get; set; } = new List<uint>();
            public List<ShortcutRow> Shortcuts { get; set; } = new List<ShortcutRow>();
            public List<SpellBarRow> SpellBars { get; set; } = new List<SpellBarRow>();
            public List<ContractRow> Contracts { get; set; } = new List<ContractRow>();
            public List<FillCompRow> FillCompBook { get; set; } = new List<FillCompRow>();
            public List<SquelchRow> Squelch { get; set; } = new List<SquelchRow>();
        }
        public sealed class QuestRow { public string Name { get; set; } public uint LastTimeCompleted { get; set; } public int NumTimesCompleted { get; set; } }
        public sealed class ShortcutRow { public uint Index { get; set; } public uint ObjectId { get; set; } }
        public sealed class SpellBarRow { public uint Bar { get; set; } public uint Index { get; set; } public uint SpellId { get; set; } }
        public sealed class ContractRow { public uint ContractId { get; set; } public bool DeleteContract { get; set; } public bool SetAsDisplayContract { get; set; } }
        public sealed class FillCompRow { public int SpellComponentId { get; set; } public int QuantityToRebuy { get; set; } }
        public sealed class SquelchRow { public uint CharacterId { get; set; } public uint AccountId { get; set; } public uint Type { get; set; } }

        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        // ---- staging ---------------------------------------------------------------------------

        sealed class Staged { public uint Guid; public string Name; public string Path; public int Number; public DateTime At; }
        static readonly ConditionalWeakTable<Session, Staged> s_Staged = new ConditionalWeakTable<Session, Staged>();
        static Staged s_Console;
        static readonly object s_Lock = new object();

        // PlayerManager's offline dictionaries, private static, by name. Resolved once at Register:
        // restore refuses when any is missing rather than finding out inside the database thread.
        static Dictionary<uint, OfflinePlayer> s_Offline;
        static Dictionary<string, IPlayer> s_Names;
        static Dictionary<uint, Dictionary<uint, IPlayer>> s_Accounts;
        static ReaderWriterLockSlim s_PlayersLock;
        static bool s_CanSwap;

        static string s_Root;

        internal static void Register()
        {
            s_Root = ModFolder.Sub("Snapshots");
            try
            {
                s_Offline = AccessTools.Field(typeof(PlayerManager), "offlinePlayers")?.GetValue(null) as Dictionary<uint, OfflinePlayer>;
                s_Names = AccessTools.Field(typeof(PlayerManager), "playerNames")?.GetValue(null) as Dictionary<string, IPlayer>;
                s_Accounts = AccessTools.Field(typeof(PlayerManager), "playerAccounts")?.GetValue(null) as Dictionary<uint, Dictionary<uint, IPlayer>>;
                s_PlayersLock = AccessTools.Field(typeof(PlayerManager), "playersLock")?.GetValue(null) as ReaderWriterLockSlim;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] CharacterSnapshot: {e.GetType().Name} reading PlayerManager's fields: {e.Message}"); }
            s_CanSwap = s_Offline != null && s_Names != null && s_Accounts != null && s_PlayersLock != null;
            if (!s_CanSwap)
                Mod.Log.Error("[RevivalGuard] CharacterSnapshot: PlayerManager.offlinePlayers/playerNames/playerAccounts/playersLock not all found; @snapshot restore will refuse, take and list still work");

            // No RequiresWorld: CommandManager refuses a RequiresWorld command when session is null,
            // which is every command from the console FIFO. Take, list and restore run on
            // PlayerManager and the database, never on a landblock, so the console may use them
            // (2026-09-22: a console "snapshot take" of the owner's character was silently refused).
            CommandManager.TryAddCommand(Handle, "snapshot", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "Take a copy of a character to disk, list the copies, or (Admin) stage and confirm putting one back.",
                "take <name>[, label] | list <name> | restore <name> <n> | confirm | cancel");
            Mod.Log.Info($"[RevivalGuard] CharacterSnapshot: files under {s_Root ?? "(no folder)"}; restore {(s_CanSwap ? "available" : "OFF")}");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Who(Session s) => s?.Player?.Name ?? "console";

        static bool IsAdmin(Session s) => s == null || s.AccessLevel >= AccessLevel.Admin;

        // ---- the verb --------------------------------------------------------------------------

        static void Handle(Session session, params string[] parameters)
        {
            if (s_Root == null)
            {
                Say(session, "No Snapshots folder could be created beside the mod dll; see the server log. Nothing was done.");
                return;
            }
            var word = parameters.Length > 0 ? parameters[0].Trim().ToLowerInvariant() : "";
            var rest = string.Join(" ", parameters.Skip(1)).Trim();
            switch (word)
            {
                case "take": Take(session, rest); return;
                case "list": List(session, rest); return;
                case "restore": Stage(session, rest); return;
                case "confirm": case "yes": Confirm(session); return;
                case "cancel": case "no":
                    bool had = GetStaged(session) != null;
                    ClearStaged(session);
                    Say(session, had ? "Dropped. Nothing was restored." : "Nothing was staged.");
                    return;
            }
            Say(session, "Usage: @snapshot take <name>[, label] | list <name> | restore <name> <n> | confirm | cancel. Names with spaces work as typed.");
        }

        /// <summary>A character by name, online or offline; null with a message if there is none.</summary>
        static IPlayer Find(Session session, string name, out bool online)
        {
            online = false;
            if (string.IsNullOrWhiteSpace(name)) { Say(session, "Which character? Give the name."); return null; }
            var p = PlayerManager.FindByName(name.Trim(), out online);
            if (p == null) Say(session, $"No character named '{name.Trim()}'.");
            return p;
        }

        static string DirFor(uint guid, string name)
        {
            // by id first so a rename keeps the history together; the name is for the human
            var existing = Directory.GetDirectories(s_Root, $"{guid:X8}_*").FirstOrDefault();
            if (existing != null) return existing;
            var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '\'' ? c : '_').ToArray());
            return Path.Combine(s_Root, $"{guid:X8}_{safe}");
        }

        static List<string> Files(uint guid)
        {
            var dir = Directory.GetDirectories(s_Root, $"{guid:X8}_*").FirstOrDefault();
            if (dir == null) return new List<string>();
            return Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        // ---- take ------------------------------------------------------------------------------

        static void Take(Session session, string rest)
        {
            string label = null;
            var comma = rest.IndexOf(',');
            if (comma >= 0) { label = rest.Substring(comma + 1).Trim(); rest = rest.Substring(0, comma); }
            var target = Find(session, rest, out bool online);
            if (target == null) return;

            uint guid = target.Guid.Full;
            var name = target.Name;
            var by = Who(session);

            // The database is the source, so the live state goes there first. Both saves queue on
            // the serialized shard database, ahead of the reads below.
            if (online && target is Player p)
                p.SavePlayerToDatabase();
            else if (target is OfflinePlayer op && op.ChangesDetected)
            {
                op.SaveBiotaToDatabase(false);
                DatabaseManager.Shard.SaveBiota(op.Biota, op.BiotaDatabaseLock, null);
            }

            DatabaseManager.Shard.GetPossessedBiotasInParallel(guid, possessions =>
            {
                try
                {
                    var db = DatabaseManager.Shard.BaseDatabase;
                    var playerDb = db.GetBiota(guid);
                    var character = db.GetCharacter(guid);
                    if (playerDb == null || character == null)
                    {
                        Say(session, $"{name}: the database has no {(playerDb == null ? "biota" : "character row")} for 0x{guid:X8}; nothing written.");
                        return;
                    }
                    var snap = new SnapshotFile
                    {
                        TakenAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        TakenBy = by,
                        Label = string.IsNullOrEmpty(label) ? null : label,
                        CharacterId = guid,
                        Name = character.Name,
                        AccountId = character.AccountId,
                        Player = BiotaConverter.ConvertToEntityBiota(playerDb),
                        Wielded = possessions.WieldedItems.Select(b => BiotaConverter.ConvertToEntityBiota(b)).ToList(),
                        Inventory = possessions.Inventory.Select(b => BiotaConverter.ConvertToEntityBiota(b)).ToList(),
                        Character = FromCharacter(character),
                    };
                    snap.Level = snap.Player.PropertiesInt != null && snap.Player.PropertiesInt.TryGetValue(PropertyInt.Level, out var lvl) ? lvl : 0;

                    // The round trip is the proof the file can be restored from. Refuse otherwise.
                    var json = JsonSerializer.Serialize(snap, s_Json);
                    var again = JsonSerializer.Serialize(JsonSerializer.Deserialize<SnapshotFile>(json, s_Json), s_Json);
                    if (json != again)
                    {
                        int at = 0; while (at < json.Length && at < again.Length && json[at] == again[at]) at++;
                        Mod.Log.Error($"[RevivalGuard] CharacterSnapshot: {name} does not survive a JSON round trip (first difference at {at}: '{Near(json, at)}' vs '{Near(again, at)}'); not written");
                        Say(session, $"{name}: the snapshot did not read back exactly as written, so it was not saved. The server log has the first difference.");
                        return;
                    }

                    var dir = DirFor(guid, character.Name);
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                    int number = Files(guid).IndexOf(path) + 1;

                    var line = $"snapshot {number} of {character.Name} (0x{guid:X8}) taken by {by}: level {snap.Level}, {snap.Wielded.Count} worn, {snap.Inventory.Count} in packs, {snap.Character.Quests.Count} quest flags, {json.Length / 1024} KB{(label != null ? $", '{label}'" : "")}";
                    Say(session, $"{char.ToUpper(line[0])}{line.Substring(1)}. @snapshot list {character.Name} shows them; @snapshot restore {character.Name} {number} puts it back (Admin, with a confirm).");
                    Mod.Log.Info($"[RevivalGuard] {line} at {path}");
                    PlayerManager.BroadcastToAuditChannel(session?.Player, $"{by} took {line.Substring(0, line.IndexOf(" taken by"))}");
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] CharacterSnapshot: take {name} failed", e);
                    Say(session, $"{name}: the snapshot threw ({e.GetType().Name}: {e.Message}); nothing written. See the server log.");
                }
            });
        }

        static string Near(string s, int at) => s.Substring(Math.Max(0, at - 20), Math.Min(60, s.Length - Math.Max(0, at - 20))).Replace('\n', ' ');

        static CharacterRows FromCharacter(Character c) => new CharacterRows
        {
            CharacterOptions1 = c.CharacterOptions1,
            CharacterOptions2 = c.CharacterOptions2,
            GameplayOptions = c.GameplayOptions,
            SpellbookFilters = c.SpellbookFilters,
            HairTexture = c.HairTexture,
            DefaultHairTexture = c.DefaultHairTexture,
            Quests = c.CharacterPropertiesQuestRegistry.Select(q => new QuestRow { Name = q.QuestName, LastTimeCompleted = q.LastTimeCompleted, NumTimesCompleted = q.NumTimesCompleted }).ToList(),
            Titles = c.CharacterPropertiesTitleBook.Select(t => t.TitleId).ToList(),
            Friends = c.CharacterPropertiesFriendList.Select(f => f.FriendId).ToList(),
            Shortcuts = c.CharacterPropertiesShortcutBar.Select(s => new ShortcutRow { Index = s.ShortcutBarIndex, ObjectId = s.ShortcutObjectId }).ToList(),
            SpellBars = c.CharacterPropertiesSpellBar.Select(s => new SpellBarRow { Bar = s.SpellBarNumber, Index = s.SpellBarIndex, SpellId = s.SpellId }).ToList(),
            Contracts = c.CharacterPropertiesContractRegistry.Select(r => new ContractRow { ContractId = r.ContractId, DeleteContract = r.DeleteContract, SetAsDisplayContract = r.SetAsDisplayContract }).ToList(),
            FillCompBook = c.CharacterPropertiesFillCompBook.Select(f => new FillCompRow { SpellComponentId = f.SpellComponentId, QuantityToRebuy = f.QuantityToRebuy }).ToList(),
            Squelch = c.CharacterPropertiesSquelch.Select(s => new SquelchRow { CharacterId = s.SquelchCharacterId, AccountId = s.SquelchAccountId, Type = s.Type }).ToList(),
        };

        // ---- list ------------------------------------------------------------------------------

        static SnapshotFile Load(string path, out string error)
        {
            error = null;
            try
            {
                var snap = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path), s_Json);
                if (snap == null) error = "empty file";
                else if (snap.Version != VERSION) error = $"version {snap.Version}, this build reads {VERSION}";
                else if (snap.Player == null || snap.Character == null) error = "no player biota or character rows in it";
                return error == null ? snap : null;
            }
            catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; return null; }
        }

        static void List(Session session, string rest)
        {
            var target = Find(session, rest, out bool online);
            if (target == null) return;
            var files = Files(target.Guid.Full);
            if (files.Count == 0)
            {
                Say(session, $"No snapshots of {target.Name} (0x{target.Guid.Full:X8}). @snapshot take {target.Name} makes one.");
                return;
            }
            Say(session, $"Snapshots of {target.Name} (0x{target.Guid.Full:X8}, {(online ? "online" : "offline")} now), oldest first{(files.Count > MAX_LIST ? $", last {MAX_LIST} of {files.Count}" : "")}:");
            for (int i = Math.Max(0, files.Count - MAX_LIST); i < files.Count; i++)
            {
                var snap = Load(files[i], out var err);
                if (snap == null) { Say(session, $"  {i + 1}. {Path.GetFileName(files[i])}: unreadable ({err})"); continue; }
                Say(session, $"  {i + 1}. {snap.TakenAt} by {snap.TakenBy}: level {snap.Level}, {snap.Wielded.Count} worn, {snap.Inventory.Count} in packs, {snap.Character.Quests.Count} quests{(snap.Label != null ? $", '{snap.Label}'" : "")}{(snap.Name != target.Name ? $" (as {snap.Name})" : "")}");
            }
            Say(session, $"@snapshot restore {target.Name} <n> stages one (Admin); it changes nothing until @snapshot confirm.");
        }

        // ---- restore: stage, confirm, run ------------------------------------------------------

        static Staged GetStaged(Session s)
        {
            lock (s_Lock)
            {
                var st = s == null ? s_Console : (s_Staged.TryGetValue(s, out var v) ? v : null);
                if (st == null) return null;
                if ((DateTime.UtcNow - st.At).TotalSeconds > CONFIRM_SECONDS) { ClearStaged(s); return null; }
                return st;
            }
        }
        static void SetStaged(Session s, Staged st)
        {
            lock (s_Lock)
            {
                if (s == null) { s_Console = st; return; }
                s_Staged.Remove(s);
                s_Staged.Add(s, st);
            }
        }
        static void ClearStaged(Session s)
        {
            lock (s_Lock) { if (s == null) s_Console = null; else s_Staged.Remove(s); }
        }

        /// <summary>The conditions a restore needs, checked at stage and again at confirm. Null when
        /// all hold, otherwise the reason in plain words.</summary>
        static string Refusal(IPlayer target, bool online, SnapshotFile snap)
        {
            if (!s_CanSwap) return "this ACE build's PlayerManager does not expose its offline player list, so the restore could be undone on login; refused";
            if (online) return $"{target.Name} is online; a restore writes the database under a live player. Log them out first";
            if (!(target is OfflinePlayer op)) return $"{target.Name} is not an offline player object";
            if (snap.CharacterId != target.Guid.Full) return $"the snapshot is of 0x{snap.CharacterId:X8}, not 0x{target.Guid.Full:X8}";
            if (snap.Player.Id != snap.CharacterId) return "the snapshot's player biota id does not match its character id; the file is not trusted";
            try { if (op.IsDeleted || op.IsPendingDeletion) return $"{target.Name} is deleted or pending deletion"; }
            catch (Exception e) { return $"could not read {target.Name}'s deletion state ({e.GetType().Name})"; }
            uint accountId = target.Account?.AccountId ?? 0;
            if (accountId == 0) return $"{target.Name} has no account";
            Session s = null;
            try { s = NetworkManager.Find(accountId); } catch (Exception) { return "that account has more than one session; refused"; }
            if (s != null) return $"account {s.Account} has a session open ({s.Player?.Name ?? "at the character list"}); a client at the character list holds the old rows and would write them back on login. Have them close the client first";
            if (target.MonarchId != null || target.PatronId != null) return $"{target.Name} is in an allegiance; the allegiance tree holds its own copy of the character and would save the old state over the restore. Leave the allegiance first";
            uint? snapMonarch = snap.Player.PropertiesIID != null && snap.Player.PropertiesIID.TryGetValue(PropertyInstanceId.Monarch, out var m) ? m : (uint?)null;
            if (snapMonarch != null) return "the snapshot was taken inside an allegiance; restoring it would put the character into a tree that no longer has it. Not supported";
            return null;
        }

        static void Stage(Session session, string rest)
        {
            if (!IsAdmin(session)) { Say(session, "Restore is Admin only. Take and list are open to you."); return; }
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[parts.Length - 1], out int number) || number < 1)
            {
                Say(session, "Usage: @snapshot restore <name> <n>, where n is the number from @snapshot list.");
                return;
            }
            var name = string.Join(" ", parts.Take(parts.Length - 1));
            var target = Find(session, name, out bool online);
            if (target == null) return;
            var files = Files(target.Guid.Full);
            if (number > files.Count) { Say(session, $"{target.Name} has {files.Count} snapshot(s); there is no {number}."); return; }
            var path = files[number - 1];
            var snap = Load(path, out var err);
            if (snap == null) { Say(session, $"Snapshot {number} of {target.Name} cannot be used: {err}."); return; }
            var why = Refusal(target, online, snap);
            if (why != null) { Say(session, $"Not staged: {why}. Nothing was changed."); return; }

            uint guid = target.Guid.Full;
            DatabaseManager.Shard.GetPossessedBiotasInParallel(guid, current =>
            {
                try
                {
                    var plan = Plan(snap, current);
                    int nowLevel = target.Level ?? 0;
                    SetStaged(session, new Staged { Guid = guid, Name = target.Name, Path = path, Number = number, At = DateTime.UtcNow });
                    Say(session, $"Staged, nothing changed yet: snapshot {number} of {target.Name} ({snap.TakenAt} by {snap.TakenBy}, level {snap.Level}{(snap.Label != null ? $", '{snap.Label}'" : "")}) onto {target.Name} as they are now (level {nowLevel}, offline).");
                    Say(session, $"It would: put back stats, skills, spells, enchantments, position, {snap.Character.Quests.Count} quest flags, titles, shortcuts and spell bars from the file; keep {plan.Keep.Count} item(s) they still hold as the file has them; bring back {plan.Reissue.Count} item(s) they no longer hold as fresh copies{Names(plan.Reissue)}; remove {plan.Remove.Count} item(s) they hold now that the file does not know{Names(plan.Remove)}.");
                    Say(session, "@snapshot confirm within two minutes does it. @snapshot cancel drops it.");
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] CharacterSnapshot: staging {target.Name} failed", e);
                    Say(session, $"Could not stage: {e.GetType().Name}: {e.Message}. Nothing was changed.");
                }
            });
        }

        static string Names(List<EntityBiota> items)
        {
            if (items.Count == 0) return "";
            var names = items.Take(MAX_NAMES).Select(b => b.PropertiesString != null && b.PropertiesString.TryGetValue(PropertyString.Name, out var n) ? n : $"wcid {b.WeenieClassId}");
            return $" ({string.Join(", ", names)}{(items.Count > MAX_NAMES ? $", +{items.Count - MAX_NAMES} more" : "")})";
        }

        sealed class RestorePlan
        {
            public readonly List<EntityBiota> Keep = new List<EntityBiota>();      // in the file and still held: update in place
            public readonly List<EntityBiota> Reissue = new List<EntityBiota>();   // in the file, not held now: new guid
            public readonly List<EntityBiota> Remove = new List<EntityBiota>();    // held now, not in the file: delete row
        }

        static RestorePlan Plan(SnapshotFile snap, PossessedBiotas current)
        {
            var plan = new RestorePlan();
            var held = new Dictionary<uint, ACE.Database.Models.Shard.Biota>();
            foreach (var b in current.WieldedItems) held[b.Id] = b;
            foreach (var b in current.Inventory) held[b.Id] = b;
            var inFile = new HashSet<uint>();
            foreach (var b in snap.Wielded.Concat(snap.Inventory))
            {
                if (b == null || !inFile.Add(b.Id)) continue;
                (held.ContainsKey(b.Id) ? plan.Keep : plan.Reissue).Add(b);
            }
            foreach (var kv in held)
                if (!inFile.Contains(kv.Key)) plan.Remove.Add(BiotaConverter.ConvertToEntityBiota(kv.Value));
            return plan;
        }

        static void Confirm(Session session)
        {
            if (!IsAdmin(session)) { Say(session, "Restore is Admin only."); return; }
            var st = GetStaged(session);
            if (st == null) { Say(session, "Nothing is staged (a staged restore is dropped after two minutes). @snapshot restore <name> <n> first."); return; }
            ClearStaged(session);

            var target = PlayerManager.FindByGuid(st.Guid, out bool online);
            if (target == null) { Say(session, $"0x{st.Guid:X8} is no longer known to PlayerManager. Nothing was changed."); return; }
            var snap = Load(st.Path, out var err);
            if (snap == null) { Say(session, $"Snapshot {st.Number} of {st.Name} cannot be used: {err}. Nothing was changed."); return; }
            var why = Refusal(target, online, snap);
            if (why != null) { Say(session, $"Not restored: {why}. Nothing was changed."); return; }

            var by = Who(session);
            var op = (OfflinePlayer)target;
            DatabaseManager.Shard.GetPossessedBiotasInParallel(st.Guid, current =>
            {
                string step = "checking";
                try
                {
                    // Re-checked on the database thread: the window between confirm and here is small but real.
                    var again = PlayerManager.FindByGuid(st.Guid, out bool onlineNow);
                    var whyNow = again == null ? "the character vanished" : Refusal(again, onlineNow, snap);
                    if (whyNow != null) { Say(session, $"Not restored: {whyNow}. Nothing was changed."); return; }
                    op = (OfflinePlayer)again;

                    var db = DatabaseManager.Shard.BaseDatabase;
                    var plan = Plan(snap, current);

                    // Any pending hourly save of the old offline state is cancelled before a row is touched.
                    op.ChangesDetected = false;

                    step = "removing items";
                    var failed = new List<string>();
                    foreach (var b in plan.Remove)
                        if (!db.RemoveBiota(b.Id)) failed.Add($"remove 0x{b.Id:X8}");

                    step = "re-issuing guids";
                    var swaps = new Dictionary<uint, uint>();
                    foreach (var b in plan.Reissue)
                    {
                        var fresh = GuidManager.NewDynamicGuid().Full;
                        swaps[b.Id] = fresh;
                        b.Id = fresh;
                    }
                    foreach (var b in plan.Keep.Concat(plan.Reissue)) Remap(b, swaps);
                    Remap(snap.Player, swaps);

                    step = "saving the player";
                    if (!db.SaveBiota(snap.Player, new ReaderWriterLockSlim())) failed.Add("player biota");

                    step = "saving items";
                    foreach (var b in plan.Keep.Concat(plan.Reissue))
                        if (!db.SaveBiota(b, new ReaderWriterLockSlim())) failed.Add($"item 0x{b.Id:X8}");

                    step = "saving character rows";
                    var character = db.GetCharacter(st.Guid);
                    if (character == null) failed.Add("character row (not found)");
                    else
                    {
                        Apply(snap.Character, character, swaps);
                        if (!db.SaveCharacter(character, new ReaderWriterLockSlim())) failed.Add("character rows");
                    }

                    step = "swapping the offline player";
                    var replacement = new OfflinePlayer(snap.Player);
                    s_PlayersLock.EnterWriteLock();
                    try
                    {
                        s_Offline[st.Guid] = replacement;
                        s_Names[replacement.Name] = replacement;
                        uint acct = replacement.Account?.AccountId ?? 0;
                        if (acct != 0)
                        {
                            if (!s_Accounts.TryGetValue(acct, out var byAccount)) { byAccount = new Dictionary<uint, IPlayer>(); s_Accounts[acct] = byAccount; }
                            byAccount[st.Guid] = replacement;
                        }
                    }
                    finally { s_PlayersLock.ExitWriteLock(); }

                    var reissued = plan.Reissue.Count == 0 ? "" : $"; fresh copies of{Names(plan.Reissue)}";
                    var summary = $"{by} restored snapshot {st.Number} of {st.Name} (0x{st.Guid:X8}, {snap.TakenAt}, level {snap.Level}): {plan.Keep.Count} kept, {plan.Reissue.Count} re-issued, {plan.Remove.Count} removed{reissued}";
                    if (failed.Count > 0)
                    {
                        summary += $"; FAILED: {string.Join(", ", failed)} (the character may be a mix of old and new; the server log has the database errors)";
                        Mod.Log.Error($"[RevivalGuard] {summary}");
                    }
                    else
                        Mod.Log.Info($"[RevivalGuard] {summary}");
                    Say(session, $"{(failed.Count > 0 ? "Partly restored" : "Restored")}: {summary.Substring(by.Length + 1)}.");
                    PlayerManager.BroadcastToAuditChannel(session?.Player, summary);
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] CharacterSnapshot: restore of {st.Name} threw while {step}", e);
                    Say(session, $"The restore threw while {step} ({e.GetType().Name}: {e.Message}). Steps before that one are done; see the server log and the Audit channel.");
                    PlayerManager.BroadcastToAuditChannel(session?.Player, $"{by}'s restore of snapshot {st.Number} of {st.Name} threw while {step}: {e.GetType().Name}");
                }
            });
        }

        /// <summary>The same id fix-ups @copychar makes when an item gets a new guid.</summary>
        static void Remap(EntityBiota b, Dictionary<uint, uint> swaps)
        {
            if (swaps.Count == 0 || b == null) return;
            if (b.PropertiesIID != null)
            {
                foreach (var key in new[] { PropertyInstanceId.Owner, PropertyInstanceId.Container, PropertyInstanceId.Wielder, PropertyInstanceId.AllowedActivator, PropertyInstanceId.AllowedWielder })
                    if (b.PropertiesIID.TryGetValue(key, out var v) && swaps.TryGetValue(v, out var nv))
                        b.PropertiesIID[key] = nv;
            }
            if (b.PropertiesEnchantmentRegistry != null)
                foreach (var e in b.PropertiesEnchantmentRegistry)
                    if (swaps.TryGetValue(e.CasterObjectId, out var nv)) e.CasterObjectId = nv;
        }

        /// <summary>Row-by-row onto the tracked Character: update what is there, remove what the file
        /// lacks, add what it has. Never remove-then-add the same key in one save; EF still tracks the
        /// removed row and refuses a second instance with its key.</summary>
        static void Apply(CharacterRows s, Character c, Dictionary<uint, uint> swaps)
        {
            c.CharacterOptions1 = s.CharacterOptions1;
            c.CharacterOptions2 = s.CharacterOptions2;
            c.GameplayOptions = s.GameplayOptions;
            c.SpellbookFilters = s.SpellbookFilters;
            c.HairTexture = s.HairTexture;
            c.DefaultHairTexture = s.DefaultHairTexture;

            Reconcile(c.CharacterPropertiesQuestRegistry, s.Quests, r => r.QuestName.ToLowerInvariant(), q => q.Name.ToLowerInvariant(),
                (r, q) => { r.LastTimeCompleted = q.LastTimeCompleted; r.NumTimesCompleted = q.NumTimesCompleted; },
                q => new CharacterPropertiesQuestRegistry { CharacterId = c.Id, QuestName = q.Name, LastTimeCompleted = q.LastTimeCompleted, NumTimesCompleted = q.NumTimesCompleted });
            Reconcile(c.CharacterPropertiesTitleBook, s.Titles, r => r.TitleId, t => t, (r, t) => { },
                t => new CharacterPropertiesTitleBook { CharacterId = c.Id, TitleId = t });
            Reconcile(c.CharacterPropertiesFriendList, s.Friends, r => r.FriendId, f => f, (r, f) => { },
                f => new CharacterPropertiesFriendList { CharacterId = c.Id, FriendId = f });
            Reconcile(c.CharacterPropertiesShortcutBar, s.Shortcuts, r => r.ShortcutBarIndex, x => x.Index,
                (r, x) => r.ShortcutObjectId = Swap(x.ObjectId, swaps),
                x => new CharacterPropertiesShortcutBar { CharacterId = c.Id, ShortcutBarIndex = x.Index, ShortcutObjectId = Swap(x.ObjectId, swaps) });
            Reconcile(c.CharacterPropertiesSpellBar, s.SpellBars, r => (r.SpellBarNumber, r.SpellBarIndex), x => (x.Bar, x.Index),
                (r, x) => r.SpellId = x.SpellId,
                x => new CharacterPropertiesSpellBar { CharacterId = c.Id, SpellBarNumber = x.Bar, SpellBarIndex = x.Index, SpellId = x.SpellId });
            Reconcile(c.CharacterPropertiesContractRegistry, s.Contracts, r => r.ContractId, x => x.ContractId,
                (r, x) => { r.DeleteContract = x.DeleteContract; r.SetAsDisplayContract = x.SetAsDisplayContract; },
                x => new CharacterPropertiesContractRegistry { CharacterId = c.Id, ContractId = x.ContractId, DeleteContract = x.DeleteContract, SetAsDisplayContract = x.SetAsDisplayContract });
            Reconcile(c.CharacterPropertiesFillCompBook, s.FillCompBook, r => r.SpellComponentId, x => x.SpellComponentId,
                (r, x) => r.QuantityToRebuy = x.QuantityToRebuy,
                x => new CharacterPropertiesFillCompBook { CharacterId = c.Id, SpellComponentId = x.SpellComponentId, QuantityToRebuy = x.QuantityToRebuy });
            Reconcile(c.CharacterPropertiesSquelch, s.Squelch, r => r.SquelchCharacterId, x => x.CharacterId,
                (r, x) => { r.SquelchAccountId = x.AccountId; r.Type = x.Type; },
                x => new CharacterPropertiesSquelch { CharacterId = c.Id, SquelchCharacterId = x.CharacterId, SquelchAccountId = x.AccountId, Type = x.Type });
        }

        static uint Swap(uint id, Dictionary<uint, uint> swaps) => swaps.TryGetValue(id, out var nv) ? nv : id;

        static void Reconcile<TRow, TSnap, TKey>(ICollection<TRow> rows, List<TSnap> wanted, Func<TRow, TKey> rowKey, Func<TSnap, TKey> snapKey,
            Action<TRow, TSnap> update, Func<TSnap, TRow> create)
        {
            var want = new Dictionary<TKey, TSnap>();
            foreach (var s in wanted ?? new List<TSnap>()) want[snapKey(s)] = s;   // a duplicate key in the file: last one wins
            foreach (var r in rows.Where(r => !want.ContainsKey(rowKey(r))).ToList()) rows.Remove(r);
            var have = new Dictionary<TKey, TRow>();
            foreach (var r in rows) have[rowKey(r)] = r;
            foreach (var kv in want)
            {
                if (have.TryGetValue(kv.Key, out var r)) update(r, kv.Value);
                else rows.Add(create(kv.Value));
            }
        }
    }
}
