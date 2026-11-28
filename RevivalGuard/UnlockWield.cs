using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ACE.Database;
using ACE.Database.Adapter;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>@unlockwield &lt;character&gt; &lt;item name | 0x&lt;guid&gt;&gt;: undoes a binding recipe's lock for staff.</summary>
    public static class UnlockWield
    {
        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "unlockwield", AccessLevel.Developer, CommandHandlerFlag.None,
                "Remove the wield lock (AllowedWielder) a binding recipe put on a character's item, online or offline.",
                "<character name> <item name | 0x<item guid>>   (a comma between them if the split is unclear)");
            Mod.Log.Info("[RevivalGuard] UnlockWield: @unlockwield registered (Developer/Admin; online or offline characters)");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Who(Session s) => s?.Player?.Name ?? "console";

        static void Handle(Session session, params string[] parameters)
        {
            if (session != null && session.AccessLevel < AccessLevel.Developer) { Say(session, "@unlockwield is Developer/Admin only."); return; }
            IPlayer target = null; bool online = false; string query = null;
            foreach (var (who, item) in TinkerLockRule.Splits(parameters))
            {
                var p = PlayerManager.FindByName(who, out bool on);
                if (p != null) { target = p; online = on; query = item; break; }
            }
            if (target == null) { Say(session, "Usage: @unlockwield <character> <item name | 0x<guid>>. No character found by that name."); return; }

            if (online && target is Player pl) Online(session, pl, query);
            else Offline(session, target, query);
        }

        static void Online(Session session, Player p, string query)
        {
            var items = p.GetAllPossessions().ToList();
            var hits = TinkerLockRule.Pick(items.Select(w => (w.Guid.Full, w.Name)).ToList(), query);
            if (!OneHit(session, p.Name, query, hits, i => (items[i].Guid.Full, items[i].Name, items[i].GetProperty(PropertyInstanceId.AllowedWielder)))) return;
            var wo = items[hits[0]];
            uint old = wo.GetProperty(PropertyInstanceId.AllowedWielder) ?? 0;
            if (old == 0) { Say(session, $"{wo.Name} 0x{wo.Guid.Full:X8} on {p.Name} has no wield lock. Nothing changed."); return; }
            p.UpdateProperty(wo, PropertyInstanceId.AllowedWielder, null);
            bool act = (wo.GetProperty(PropertyInstanceId.AllowedActivator) ?? 0) == old;
            if (act) p.UpdateProperty(wo, PropertyInstanceId.AllowedActivator, null);
            wo.SaveBiotaToDatabase();
            Done(session, old, wo.Name, wo.Guid.Full, p.Name, act, true);
        }

        static void Offline(Session session, IPlayer target, string query)
        {
            uint guid = target.Guid.Full;
            string name = target.Name;
            DatabaseManager.Shard.GetPossessedBiotasInParallel(guid, possessions =>
            {
                try
                {
                    var again = PlayerManager.FindByGuid(new ObjectGuid(guid), out bool onNow);
                    if (again == null || onNow) { Say(session, $"{name} {(again == null ? "vanished" : "logged in")} while I looked. Run it again."); return; }
                    var rows = possessions.Inventory.Concat(possessions.WieldedItems).ToList();
                    string NameOf(ACE.Database.Models.Shard.Biota b) =>
                        b.BiotaPropertiesString.FirstOrDefault(s => s.Type == (ushort)PropertyString.Name)?.Value ?? "item";
                    uint? Iid(ACE.Database.Models.Shard.Biota b, PropertyInstanceId k) =>
                        b.BiotaPropertiesIID.FirstOrDefault(x => x.Type == (ushort)k)?.Value;
                    var hits = TinkerLockRule.Pick(rows.Select(b => (b.Id, NameOf(b))).ToList(), query);
                    if (!OneHit(session, name, query, hits, i => (rows[i].Id, NameOf(rows[i]), Iid(rows[i], PropertyInstanceId.AllowedWielder)))) return;
                    var row = rows[hits[0]];
                    var ent = BiotaConverter.ConvertToEntityBiota(row);
                    string itemName = NameOf(row);
                    uint old = ent.PropertiesIID != null && ent.PropertiesIID.TryGetValue(PropertyInstanceId.AllowedWielder, out var o) ? o : 0;
                    if (old == 0) { Say(session, $"{itemName} 0x{row.Id:X8} on {name} has no wield lock. Nothing changed."); return; }
                    ent.PropertiesIID.Remove(PropertyInstanceId.AllowedWielder);
                    bool act = ent.PropertiesIID.TryGetValue(PropertyInstanceId.AllowedActivator, out var a) && a == old;
                    if (act) ent.PropertiesIID.Remove(PropertyInstanceId.AllowedActivator);
                    if (!DatabaseManager.Shard.BaseDatabase.SaveBiota(ent, new ReaderWriterLockSlim()))
                    { Say(session, $"Saving {itemName} 0x{row.Id:X8} failed. Nothing changed."); return; }
                    Done(session, old, itemName, row.Id, name, act, false);
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] unlockwield: {name} threw", e);
                    Say(session, $"@unlockwield threw ({e.GetType().Name}: {e.Message}). See the server log.");
                }
            });
        }

        /// <summary>True for exactly one match; otherwise says why (none, or the list with guids).</summary>
        static bool OneHit(Session session, string who, string query, List<int> hits, Func<int, (uint guid, string name, uint? wielder)> row)
        {
            if (hits.Count == 1) return true;
            if (hits.Count == 0) { Say(session, $"{who} holds nothing matching '{query}'. Nothing changed."); return false; }
            Say(session, $"{hits.Count} items on {who} match '{query}'. Use @unlockwield {who}, 0x<guid>:");
            foreach (var i in hits.Take(20))
            {
                var r = row(i);
                Say(session, $"  0x{r.guid:X8} {r.name}{(r.wielder is uint w && w != 0 ? $" (locked to {Owner(w)})" : "")}");
            }
            return false;
        }

        static string Owner(uint guid) => PlayerManager.FindByGuid(new ObjectGuid(guid))?.Name ?? $"0x{guid:X8}";

        static void Done(Session session, uint old, string item, uint itemGuid, string who, bool act, bool online)
        {
            string line = $"{Who(session)} removed AllowedWielder 0x{old:X8} ({Owner(old)}) from {item} 0x{itemGuid:X8} on {who}"
                + (act ? " (and the matching AllowedActivator)" : "") + (online ? " (online)" : " (offline)");
            Mod.Log.Info($"[RevivalGuard] unlockwield: {line}");
            Say(session, $"Done: {item} 0x{itemGuid:X8} on {who} is no longer locked to {Owner(old)}; any character can wield it now.");
            try { PlayerManager.BroadcastToAuditChannel(session?.Player, $"unlockwield: {line}"); } catch { }
        }
    }
}
