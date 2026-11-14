using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using R = RevivalGuard.WeenieReloadRule;

namespace RevivalGuard
{
    /// <summary>
    /// @RELOADWEENIE: ONE WEENIE BACK FROM THE DATABASE, WITHOUT `clearcache weenie`.
    ///
    /// A content fix (tools/public-shard/apply-live-ovh.sh) used to end with ACE's `clearcache weenie` and
    /// `clearcache landblock`: every cached weenie and every landblock's instance list dropped, so the next minutes
    /// re-read thousands of weenies from MySQL as monsters respawn, and the world hitches. Most fixes change a handful
    /// of wcids. This drops ONE from WorldDatabaseWithEntityCache (ClearCachedWeenie, plus the class-name and
    /// scroll-by-spell entries that point at it), so the next object made from that wcid reads the database.
    ///
    /// WHAT CHANGES AND WHAT DOES NOT. Every live object owns its biota, a full copy of the weenie's properties
    /// taken when it was made; the cache only feeds the NEXT one. So after the eviction: generator spawns, loot, vendor
    /// purchases, crafting results and @create get the new values; anything already made keeps the old ones, and
    /// that includes every copy in a player's pack or worn (online or not) and a vendor's display copy until the
    /// vendor itself is remade. Those are never replaced here: they are the player's items.
    ///
    /// `respawn` also remakes live copies where that is safe (WeenieReloadRule decides): a generator spawn standing
    /// idle in a loaded landblock, not in combat, not hurt, not mid-emote, not a pet, not held by anyone, whose
    /// generator would not destroy itself for losing it. It is destroyed and its own generator profile spawns one
    /// again at once (Enqueue + ProcessQueue, ACE's own spawn path, so position, palette and stack size come from
    /// the profile as on any respawn). Placed objects without a generator are named, with @reloadblock as the way
    /// to remake them.
    ///
    /// Runs on the world thread from the PlayerManager.Tick postfix, as @reloadblock does (LandblockReload has the
    /// why): the console runs commands on its own thread, and Destroy and the generator profiles assume the world
    /// thread. The audit trail records the command itself (AuditTrail's cmd seam) and the result line we post to
    /// ACE's Audit channel.
    ///
    ///   @reloadweenie 12345            drop wcid 12345 from the cache; report its live copies
    ///   @reloadweenie 12345 respawn    ...and remake the idle generator spawns among them
    ///
    /// Admin command feedback is ours, not retail's.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class WeenieReload
    {
        sealed class Request { public uint Wcid; public bool Respawn; public Session Session; public string Who; }

        static readonly ConcurrentQueue<Request> s_Requests = new ConcurrentQueue<Request>();

        // Private caches beside the weenie cache; by name, and a missing one is skipped (logged at Register).
        static readonly FieldInfo s_NameCache = AccessTools.Field(typeof(WorldDatabaseWithEntityCache), "weenieClassNameToClassIdCache");
        static readonly FieldInfo s_ScrollCache = AccessTools.Field(typeof(WorldDatabaseWithEntityCache), "scrollsBySpellID");

        internal static void Register()
        {
            if (s_NameCache == null) Mod.Log.Warn("[RevivalGuard] @reloadweenie: weenieClassNameToClassIdCache not found; class-name lookups may keep a renamed weenie's old name");
            if (s_ScrollCache == null) Mod.Log.Warn("[RevivalGuard] @reloadweenie: scrollsBySpellID not found; a reloaded scroll's spell lookup keeps the old weenie");
            CommandManager.TryAddCommand(Handle, "reloadweenie", AccessLevel.Admin, CommandHandlerFlag.None,
                "Drop one weenie from ACE's cache so the next one made reads the database (no global clearcache). `respawn` also remakes its idle generator spawns. Player items are never touched.",
                "<wcid> [respawn]");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] parameters)
        {
            parameters = parameters.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();   // a trailing space from a script
            if (parameters.Length == 0 || !R.TryParseWcid(parameters[0], out var wcid)
                || (parameters.Length > 1 && !parameters[1].Equals("respawn", StringComparison.OrdinalIgnoreCase)) || parameters.Length > 2)
            {
                Say(session, "Usage: @reloadweenie <wcid in decimal> [respawn]. Without respawn nothing in the world is destroyed.");
                return;
            }
            bool respawn = parameters.Length > 1;
            s_Requests.Enqueue(new Request { Wcid = wcid, Respawn = respawn, Session = session, Who = session?.Player?.Name ?? "console" });
            if (session == null) Console.WriteLine($"@reloadweenie {wcid}{(respawn ? " respawn" : "")}: queued for the next world tick");
        }

        // A content fix can name dozens of wcids (apply-live-ovh.sh --weenies); each scans every loaded landblock, so
        // a few per world tick rather than all in one.
        const int PER_TICK = 8;

        static void Postfix()
        {
            for (int n = 0; n < PER_TICK && s_Requests.TryDequeue(out var r); n++)
            {
                try { Run(r); }
                catch (Exception ex)
                {
                    Mod.Log.Error($"[RevivalGuard] @reloadweenie {r.Wcid} by {r.Who} failed", ex);
                    Say(r.Session, $"wcid {r.Wcid}: the reload threw ({ex.GetType().Name}); see the server log.");
                }
            }
        }

        static int EvictSideCaches(WorldDatabaseWithEntityCache world, uint wcid)
        {
            int dropped = 0;
            if (s_NameCache?.GetValue(world) is ConcurrentDictionary<string, uint> names)
                foreach (var kv in names.ToArray())
                    if (kv.Value == wcid && names.TryRemove(kv.Key, out _)) dropped++;
            if (s_ScrollCache?.GetValue(world) is IDictionary scrolls)
            {
                var stale = new List<object>();
                lock (scrolls.SyncRoot ?? scrolls)
                {
                    foreach (DictionaryEntry kv in scrolls)
                        if (kv.Value is ACE.Entity.Models.Weenie w && w.WeenieClassId == wcid) stale.Add(kv.Key);
                    foreach (var k in stale) scrolls.Remove(k);
                }
                dropped += stale.Count;
            }
            return dropped;
        }

        static bool OwnedByPlayer(WorldObject wo)
        {
            for (int depth = 0; wo != null && depth < 8; depth++)
            {
                if (wo.Wielder is Player || wo.Container is Player) return true;
                wo = (WorldObject)wo.Wielder ?? wo.Container;
            }
            return false;
        }

        static GeneratorProfile ProfileOf(WorldObject wo) =>
            wo.Generator?.GeneratorProfiles?.FirstOrDefault(p => p.Spawned.ContainsKey(wo.Guid.Full));

        static R.Facts FactsOf(WorldObject wo)
        {
            var c = wo as Creature;
            var gen = wo.Generator;
            bool tracked = ProfileOf(wo) != null;
            // WorldObject_Generators.NotifyOfEvent: these generators destroy themselves when their count reaches 0.
            bool empties = tracked && gen.InitCreate > 0 && gen.CurrentCreate <= 1
                && (gen.GeneratorAutomaticDestruction || (gen.GeneratorId > 0 && !(gen is Container)));
            return new R.Facts
            {
                IsPlayer = wo is Player,
                OwnedByPlayer = OwnedByPlayer(wo),
                InContainer = wo.Container != null && !(wo.Container is Player),
                WieldedByCreature = wo.Wielder != null && !(wo.Wielder is Player),
                IsCreature = c != null,
                IsDead = c != null && c.IsDead,
                InCombat = c != null && (c.IsAwake || c.AttackTarget != null || c.Health.Current < c.Health.MaxValue),
                Busy = wo.IsBusy || (wo.EmoteManager?.IsBusy ?? false),
                IsPet = wo is Pet,
                IsGenerator = wo.IsGenerator,
                HasGenerator = tracked,
                GeneratorEmpties = empties,
                StaticGuid = wo.Guid.IsStatic(),
            };
        }

        /// <summary>Destroy it and have its own profile spawn one at once. True when a new object was made.</summary>
        static bool RespawnOne(WorldObject wo, uint wcid)
        {
            var profile = ProfileOf(wo);
            if (profile == null) return false;
            var old = $"0x{wo.Guid.Full:X8} '{wo.Name}'";
            var before = new HashSet<uint>(profile.Spawned.Keys);
            wo.Destroy();                               // NotifyOfEvent: the profile forgets it, NextAvailable = now + Delay
            profile.NextAvailable = DateTime.UtcNow;    // the delay is for a kill, not for this
            if (profile.Biota.WeenieClassId != wcid)    // a treasure roll or a linked profile: its own regen picks
                return false;
            profile.Enqueue(1);
            profile.ProcessQueue();
            var made = profile.Spawned.Values.Where(v => !before.Contains(v.Guid.Full)).Select(v => v.TryGetWorldObject()).Where(o => o != null).ToList();
            // the new object's own values, read back off it: the proof that the respawn read the database
            foreach (var o in made)
                Mod.Log.Info($"[RevivalGuard] @reloadweenie {wcid}: {old} -> 0x{o.Guid.Full:X8} '{o.Name}' at {o.Location?.ToLOCString()}");
            return made.Count > 0;
        }

        static void Run(Request r)
        {
            var world = DatabaseManager.World;
            int cachedBefore = world.GetWeenieCacheCount();
            bool evicted = world.ClearCachedWeenie(r.Wcid);
            int side = EvictSideCaches(world, r.Wcid);
            var fresh = world.GetCachedWeenie(r.Wcid);  // read it now: a bad row shows here, not at the next spawn
            if (fresh == null)
            {
                Say(r.Session, $"wcid {r.Wcid} is not in the world database. Its cache entry is dropped; nothing in the world was touched.");
                return;
            }
            var name = fresh.GetProperty(PropertyString.Name);

            var actions = new List<R.Action>();
            var toRespawn = new List<WorldObject>();

            // The world: every loaded landblock's top-level objects, and what creatures, chests and corpses hold.
            foreach (var lb in LandblockManager.GetLoadedLandblocks())
            {
                foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    if (wo is Player) continue;   // their possessions are counted below, from the player
                    if (wo.WeenieClassId == r.Wcid)
                    {
                        var a = R.Decide(FactsOf(wo), r.Respawn);
                        actions.Add(a);
                        if (a == R.Action.Respawn) toRespawn.Add(wo);
                    }
                    if (wo is Container box)
                        foreach (var item in box.Inventory.Values)
                            if (item?.WeenieClassId == r.Wcid) actions.Add(R.Decide(FactsOf(item), r.Respawn));
                    if (wo is Creature cr)
                        foreach (var item in cr.EquippedObjects.Values)
                            if (item?.WeenieClassId == r.Wcid) actions.Add(R.Decide(FactsOf(item), r.Respawn));
                }
            }
            // Online players' own copies, for the count only.
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p == null) continue;
                if (p.WeenieClassId == r.Wcid) actions.Add(R.Action.KeepPlayer);
                foreach (var item in p.GetAllPossessions())
                    if (item?.WeenieClassId == r.Wcid) actions.Add(R.Action.KeepPlayerOwned);
            }

            int made = 0, failed = 0;
            foreach (var wo in toRespawn)
            {
                try { if (RespawnOne(wo, r.Wcid)) made++; else failed++; }
                catch (Exception ex) { failed++; Mod.Log.Warn($"[RevivalGuard] @reloadweenie {r.Wcid}: respawn of 0x{wo.Guid.Full:X8} threw {ex.GetType().Name}: {ex.Message}"); }
            }

            var summary = R.Summary(r.Wcid, name, evicted, actions, r.Respawn);
            if (failed > 0) summary += $" {failed} of the {toRespawn.Count} destroyed were left to their generator's own timer.";
            if (side > 0) summary += $" ({side} name/scroll cache entries dropped too.)";
            Say(r.Session, summary);
            Mod.Log.Info($"[RevivalGuard] @reloadweenie {r.Wcid} by {r.Who}: evicted={evicted} side={side} weenieCache={cachedBefore}->{world.GetWeenieCacheCount()} live={actions.Count} respawned={made} failed={failed} :: {summary}");
            PlayerManager.BroadcastToAuditChannel(r.Session?.Player,
                $"{r.Who} reloaded weenie {r.Wcid} ({name}){(r.Respawn ? $", respawned {made}" : "")}, {actions.Count} live copies seen");
        }
    }
}
