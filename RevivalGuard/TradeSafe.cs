using System.Text.Json;
using System.Threading;
using ACE.Database;
using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CRASH DURING A TRADE NO LONGER LOSES THE ITEMS (docs/findings/dupe-recheck-2026-11-02.md). ACE's
    /// Player.FinalizeTrade takes each offered item out of its owner's pack with TryRemoveFromInventoryWithNetworking
    /// (or TryDequipObjectWithNetworking), whose DeepSave writes the row with NO container, holds the items 0.5 s,
    /// then puts each into the other pack and saves it there. Measured 2026-11-03 (box and M5 test shards): kill -9
    /// 0.3 s after both accepted left both items owned by nobody, gone from both players.
    ///
    /// So during FinalizeTrade the ownerless DeepSave is skipped: the database keeps the item with its old owner until
    /// the hand-over saves it with the new one (TryCreateInInventoryWithNetworking saves each item at once). A crash then
    /// leaves each item where it was or where it went, one row either way: never nowhere, never twice.
    ///
    /// Belt and braces: the items of a trade are written to trade-journal.json (guid, from, to) before the hand-over,
    /// and at startup any journalled item whose row has no container, wielder or location goes back to its old owner,
    /// logged. Rows a crash orphaned before this fix carry no trace of their owner: @orphans lists them and
    /// @orphans return &lt;guid&gt; &lt;character&gt; hands one to the character a report names.
    /// </summary>
    [HarmonyPatch(typeof(Player), "FinalizeTrade")]
    static class TradeSafe
    {
        [ThreadStatic] internal static bool t_InTrade;
        const string FILE = "trade-journal.json";
        static readonly object s_Lock = new();

        internal sealed class Entry { public uint Item { get; set; } public uint From { get; set; } public uint To { get; set; } public long At { get; set; } }

        static string PathOf() => System.IO.Path.Combine(ModFolder.Path(), FILE);

        static void Prefix(Player __instance, Player target)
        {
            t_InTrade = true;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                lock (s_Lock)
                {
                    var list = Load().Where(e => now - e.At < 120).ToList();   // a hand-over takes 0.5 s; older entries are done
                    foreach (var g in __instance.ItemsInTradeWindow) list.Add(new Entry { Item = g.Full, From = __instance.Guid.Full, To = target.Guid.Full, At = now });
                    foreach (var g in target.ItemsInTradeWindow) list.Add(new Entry { Item = g.Full, From = target.Guid.Full, To = __instance.Guid.Full, At = now });
                    Write(list);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeSafe: journal not written: {e.Message}"); }
        }

        static Exception Finalizer(Exception __exception) { t_InTrade = false; return __exception; }

        static List<Entry> Load()
        {
            try { var p = PathOf(); return File.Exists(p) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(p)) ?? new() : new(); }
            catch { return new(); }
        }

        static void Write(List<Entry> list)
        {
            var p = PathOf(); var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, p, true);
        }

        /// <summary>Startup: a journalled trade item whose row belongs to nothing goes back to the one who offered it.</summary>
        internal static void Register()
        {
            // Mods load before ACE opens its database (Program.cs: ModManager.Initialize, then DatabaseManager.Initialize),
            // so the replay waits for it on a worker; it runs before the world opens to logins in practice.
            System.Threading.Tasks.Task.Run(async () =>
            {
                for (int i = 0; i < 600 && DatabaseManager.Shard == null; i++) await System.Threading.Tasks.Task.Delay(200);
                Replay();
            });
            CommandManager.TryAddCommand(Orphans, "orphans", AccessLevel.Admin, CommandHandlerFlag.None,
                "Items that belong to nothing (no pack, wielder or place), e.g. left by a crash mid-trade.",
                "@orphans | @orphans return <0xguid> <character name>");
        }

        static void Replay()
        {
            try
            {
                List<Entry> list; lock (s_Lock) list = Load();
                int back = 0;
                foreach (var e in list)
                {
                    var db = DatabaseManager.Shard.BaseDatabase.GetBiota(e.Item, true);
                    if (db == null || !Ownerless(db)) continue;
                    if (Return(db, e.From)) { back++; Mod.Log.Warn($"[RevivalGuard] TradeSafe: 0x{e.Item:X8} was left ownerless by a trade (crash); returned to 0x{e.From:X8}"); }
                }
                lock (s_Lock) Write(new List<Entry>());
                Mod.Log.Info($"[RevivalGuard] TradeSafe: trade hand-over kept owned through a crash; {list.Count} journalled, {back} returned");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeSafe: startup check failed (journal kept for the next start): {e}"); }
        }

        static bool Ownerless(Biota db) =>
            db.WeenieType != (int)WeenieType.Allegiance   // allegiance records have no owner by design
            && !db.BiotaPropertiesIID.Any(p => p.Type == (ushort)PropertyInstanceId.Container || p.Type == (ushort)PropertyInstanceId.Wielder)
            && !db.BiotaPropertiesPosition.Any(p => p.PositionType == (ushort)PositionType.Location);

        /// <summary>Into a character's pack: an online one gets the object on the world thread (Replay runs on a worker,
        /// a console @orphans on the console thread), so true there means queued; an offline one finds it at login.</summary>
        static bool Return(Biota db, uint owner)
        {
            var online = PlayerManager.GetOnlinePlayer(owner);
            if (online != null)
            {
                WorldManager.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
                {
                    try
                    {
                        var wo = WorldObjectFactory.CreateWorldObject(db);
                        if (wo == null || !online.TryCreateInInventoryWithNetworking(wo))
                            Mod.Log.Warn($"[RevivalGuard] TradeSafe: 0x{db.Id:X8} could not go into {online.Name}'s pack; still ownerless (@orphans)");
                    }
                    catch (Exception e) { TickGuard.Report("TradeSafe.Return", e); }
                }));
                return true;
            }
            var biota = BiotaConverter.ConvertToEntityBiota(db, true);
            biota.PropertiesIID[PropertyInstanceId.Container] = owner;
            return DatabaseManager.Shard.BaseDatabase.SaveBiota(biota, new ReaderWriterLockSlim());
        }

        static void Orphans(Session session, params string[] args)
        {
            void Say(string s)
            {
                if (session == null) Console.WriteLine(s);
                else session.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(s, ChatMessageType.Broadcast));
            }
            try
            {
                if (args.Length >= 3 && args[0] == "return")
                {
                    string g = args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[1][2..] : args[1];
                    if (!uint.TryParse(g, System.Globalization.NumberStyles.HexNumber, null, out uint guid)) { Say("@orphans return <0xguid> <character name>"); return; }
                    var who = PlayerManager.FindByName(string.Join(' ', args.Skip(2)));
                    if (who == null) { Say("No such character."); return; }
                    var db = DatabaseManager.Shard.BaseDatabase.GetBiota(guid, true);
                    if (db == null || !Ownerless(db)) { Say($"0x{guid:X8} is not an ownerless item."); return; }
                    bool ok = Return(db, who.Guid.Full);
                    Mod.Log.Warn($"[RevivalGuard] TradeSafe: @orphans by {session?.Player?.Name ?? "console"}: 0x{guid:X8} to {who.Name}: {(ok ? "done" : "FAILED")}");
                    Say(ok ? $"0x{guid:X8} returned to {who.Name}." : $"Could not return 0x{guid:X8}.");
                    return;
                }
                using var ctx = new ShardDbContext();
                var rows = ctx.Biota.Where(b => b.Id >= 0x80000000 && b.WeenieType != (int)WeenieType.Allegiance
                        && !b.BiotaPropertiesIID.Any(p => p.Type == (ushort)PropertyInstanceId.Container || p.Type == (ushort)PropertyInstanceId.Wielder)
                        && !b.BiotaPropertiesPosition.Any(p => p.PositionType == (ushort)PositionType.Location))
                    .Select(b => new { b.Id, b.WeenieClassId, Name = b.BiotaPropertiesString.Where(s => s.Type == (ushort)PropertyString.Name).Select(s => s.Value).FirstOrDefault(),
                                       Stack = b.BiotaPropertiesInt.Where(i => i.Type == (ushort)PropertyInt.StackSize).Select(i => (int?)i.Value).FirstOrDefault() })
                    .Take(40).ToList();
                Say($"{rows.Count} ownerless item(s){(rows.Count == 40 ? " (first 40)" : "")}:");
                foreach (var r in rows) Say($"  0x{r.Id:X8} {r.Name ?? "?"} (wcid {r.WeenieClassId}){(r.Stack > 1 ? $" x{r.Stack}" : "")}");
            }
            catch (Exception e) { Say($"@orphans failed: {e.Message}"); }
        }
    }

    /// <summary>The ownerless DeepSave, skipped while a trade hands items over (TradeSafe).</summary>
    [HarmonyPatch(typeof(Player), "DeepSave")]
    static class TradeSafeDeepSave
    {
        static bool Prefix() => !TradeSafe.t_InTrade;
    }
}
