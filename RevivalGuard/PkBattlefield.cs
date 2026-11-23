using System.Collections.Concurrent;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Microsoft.EntityFrameworkCore;

namespace RevivalGuard
{
    /// <summary>
    /// CORPSES THAT STAY (owner 2026-11-22, docs/proposals/pk-battlefield-corpses.md; rules in PkBattlefieldRules.cs).
    /// Owns every player corpse that outlasts ACE's timer: MansionPk's mansion-lawn timer (switch off, exactly as before)
    /// and the PK battlefields (pk_battlefield_corpses on): the Dark Isle beach, Arwic's lawn, every mansion landblock
    /// and every PK Night spot. A kept corpse carries KEPT_SINCE (PropertyInt64 29113, unix ms, ours); the Decay prefix
    /// holds it while its zone's cap keeps it, saves it once (ACE saves a player corpse only when something dropped, so
    /// an empty one would not survive a restart), and lets the oldest over the cap fade the ACE way (15 s; ACE itself
    /// never rots one a player has open, and the cap skips it for the next oldest). Name and appraisal stay ACE's.
    /// </summary>
    public static partial class PkBattlefield
    {
        internal const string P_ON = "pk_battlefield_corpses", P_CAP = "pk_battlefield_cap";
        internal const PropertyInt64 KEPT_SINCE = (PropertyInt64)29113;

        static readonly CorpseIndex s_Index = new CorpseIndex();
        static readonly ConcurrentDictionary<uint, Corpse> s_Loaded = new ConcurrentDictionary<uint, Corpse>();
        static readonly ConcurrentDictionary<uint, byte> s_SaidFade = new ConcurrentDictionary<uint, byte>();

        internal static bool On => PkCommon.On(P_ON);
        static int DefaultCap => (int)Math.Clamp(PkCommon.Long(P_CAP, DEFAULT_CAP), 1, 500);
        static List<Zone> Circles() => CircleZones(Math.Clamp(PkCommon.Long(PkNight.P_RADIUS, PkNight.DEFAULT_RADIUS), 5, 200));

        static readonly Dictionary<string, Zone> s_ZoneByKey = new Dictionary<string, Zone>();
        static Zone ZoneOfKey(string key)
        {
            lock (s_ZoneByKey)
            {
                if (s_ZoneByKey.TryGetValue(key, out var z)) return z;
                z = Circles().FirstOrDefault(c => c.Key == key);
                if (z == null && key.StartsWith("mansion-") && ushort.TryParse(key.Substring(8), System.Globalization.NumberStyles.HexNumber, null, out var lb))
                    z = MansionZone(lb);
                if (z != null) s_ZoneByKey[key] = z;
                return z;
            }
        }

        internal static Zone ZoneOf(ushort landblock, double x, double y) =>
            ZoneAt(Circles(), landblock, x, y, MansionIndex.MansionRootAt(landblock) != 0);

        internal static void Register()
        {
            // the switch and the cap are PkCommon properties (pk_battlefield_corpses off, pk_battlefield_cap 50)
            Task.Run(LoadIndex);
        }

        /// <summary>Every marked corpse in the shard database, by zone; then one log line a zone.</summary>
        static void LoadIndex()
        {
            try
            {
                List<(uint id, long since)> marks;
                Dictionary<uint, BiotaPropertiesPosition> where;
                using (var ctx = new ShardDbContext())
                {
                    ushort t = (ushort)KEPT_SINCE;
                    marks = ctx.BiotaPropertiesInt64.AsNoTracking().Where(p => p.Type == t)
                        .Select(p => new { p.ObjectId, p.Value }).ToList().Select(p => (p.ObjectId, p.Value)).ToList();
                    var ids = marks.Select(m => m.id).ToList();
                    where = ctx.BiotaPropertiesPosition.AsNoTracking().Where(p => p.PositionType == 1 && ids.Contains(p.ObjectId))
                        .ToList().GroupBy(p => p.ObjectId).ToDictionary(g => g.Key, g => g.First());
                }
                int lost = 0;
                foreach (var (id, since) in marks)
                {
                    if (!where.TryGetValue(id, out var pos)) { lost++; continue; }
                    var z = ZoneOf((ushort)(pos.ObjCellId >> 16), pos.OriginX, pos.OriginY);
                    if (z == null) { lost++; continue; }
                    s_Index.Add(z.Key, id, since);
                }
                var counts = s_Index.Counts();
                string sw = On ? "ON" : "off";
                foreach (var z in Circles())
                    Mod.Log.Info($"[RevivalGuard] PkBattlefield ({sw}): {z}: {counts.GetValueOrDefault(z.Key)} corpse(s), cap {CapOf(z, DefaultCap)}");
                var mansions = counts.Where(kv => kv.Key.StartsWith("mansion-")).ToList();
                Mod.Log.Info($"[RevivalGuard] PkBattlefield ({sw}): every mansion landblock, cap {DefaultCap} each: {mansions.Sum(kv => kv.Value)} corpse(s) on {mansions.Count} block(s)"
                    + (mansions.Count > 0 ? " (" + string.Join(", ", mansions.Select(kv => $"{kv.Key.Substring(8)} {kv.Value}")) + ")" : "")
                    + (lost > 0 ? $"; {lost} marked corpse(s) outside every zone, left to ACE" : ""));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkBattlefield: corpse index not read ({e.Message}); corpses join it as their landblocks tick"); }
        }

        // ------------------------------------------------------------ at death

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.RecalculateDecayTime))]
        static class AtDeathPatch
        {
            static void Postfix(Corpse __instance, Player player)
            {
                try { OnPlayerCorpse(__instance, player); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBattlefield: corpse of {player?.Name}: {e}"); }
            }
        }

        static void OnPlayerCorpse(Corpse corpse, Player victim)
        {
            var loc = corpse.Location;
            if (loc == null || victim == null) return;
            ushort lb = loc.LandblockId.Landblock;
            bool on = On;
            var zone = on ? ZoneOf(lb, loc.PositionX, loc.PositionY) : null;
            bool mansion = MansionIndex.MansionRootAt(lb) != 0;
            long secs = mansion ? MansionPk.DecaySeconds() : 0;
            switch (OnDeath(on, zone != null, mansion, secs))
            {
                case AtDeath.Timer:
                    long until = (long)ACE.Common.Time.GetUnixTime() + secs;
                    corpse.SetProperty(MansionPk.TROPHY_UNTIL, until);
                    corpse.TimeToRot = Math.Max(corpse.TimeToRot ?? 0, secs);
                    Mod.Log.Info($"[RevivalGuard] MansionPk: {corpse.Name} (0x{corpse.Guid.Full:X8}) lies on the grounds of mansion 0x{MansionIndex.MansionRootAt(lb):X8}; it lasts {secs} s");
                    break;
                case AtDeath.Battlefield:
                    long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    corpse.SetProperty(KEPT_SINCE, since);
                    corpse.TimeToRot = Math.Max(corpse.TimeToRot ?? 0, KEPT_TTR);
                    s_Index.Add(zone.Key, corpse.Guid.Full, since);
                    lock (s_ZoneByKey) s_ZoneByKey[zone.Key] = zone;
                    Mod.Log.Info($"[RevivalGuard] PkBattlefield: {corpse.Name} (0x{corpse.Guid.Full:X8}) stays on {zone}: {s_Index.Count(zone.Key)}/{CapOf(zone, DefaultCap)}");
                    break;
            }
        }

        // ------------------------------------------------------------ as it lies there

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Decay))]
        static class Lasts
        {
            static bool Prefix(WorldObject __instance)
            {
                if (!(__instance is Corpse c)) return true;
                try { return Decide(c); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBattlefield: decay of 0x{c.Guid.Full:X8}: {e.Message}"); return true; }
            }
        }

        static bool Decide(Corpse c)
        {
            var since = c.GetProperty(KEPT_SINCE);
            var until = c.GetProperty(MansionPk.TROPHY_UNTIL);
            if (since == null && until == null && c.InventoryLoaded) since = Adopt(c);
            if ((since == null && until == null) || !c.InventoryLoaded) return true;
            uint g = c.Guid.Full;
            bool on = since != null && On;
            bool kept = false;
            if (on)
            {
                s_Loaded[g] = c;
                if (!s_Index.Has(g))
                {
                    var loc = c.Location;
                    var z = loc == null ? null : ZoneOf(loc.LandblockId.Landblock, loc.PositionX, loc.PositionY);
                    if (z == null) return true;   // moved out of every zone (a staff teleport): ACE's
                    s_Index.Add(z.Key, g, since.Value);
                }
                var key = s_Index.ZoneKeyOf(g);
                if (key == null) return true;
                var zone = ZoneOfKey(key);
                kept = s_Index.KeptByCap(g, CapOf(zone, DefaultCap), Looting);
            }
            var (act, ttr) = OnDecay(on, since, kept, until, c.TimeToRot ?? 0, ACE.Common.Time.GetUnixTime(), Corpse.EmptyDecayTime);
            if (!double.IsNaN(ttr)) c.TimeToRot = ttr;
            if (act == Tick.Hold && since != null && !c.BiotaOriginatedFromOrHasBeenSavedToDatabase())
                c.SaveBiotaToDatabase();   // an empty corpse: ACE saved nothing, so it would not outlive a restart
            if (act == Tick.Fade && s_SaidFade.TryAdd(g, 0))
                Mod.Log.Info($"[RevivalGuard] PkBattlefield: {c.Name} (0x{g:X8}) is the oldest over the cap of {s_Index.ZoneKeyOf(g)}; it rots now");
            return act != Tick.Hold;
        }

        /// <summary>A player corpse made before the switch was on (owner 2026-11-22: keep PK Night #1's corpses on the Dark
        /// Isle): on its first decay tick inside a zone it is kept like one marked at death, aged by its creation time so
        /// the cap still lets the oldest go first. Monster corpses and capsules (IsMonster) never.</summary>
        static long? Adopt(Corpse c)
        {
            if (!On || c.IsMonster) return null;
            var loc = c.Location;
            var zone = loc == null ? null : ZoneOf(loc.LandblockId.Landblock, loc.PositionX, loc.PositionY);
            if (zone == null) return null;
            long born = (long)(c.CreationTimestamp ?? ACE.Common.Time.GetUnixTime());
            long since = born * 1000;
            c.SetProperty(KEPT_SINCE, since);
            c.TimeToRot = Math.Max(c.TimeToRot ?? 0, KEPT_TTR);
            lock (s_ZoneByKey) s_ZoneByKey[zone.Key] = zone;
            Mod.Log.Info($"[RevivalGuard] PkBattlefield: {c.Name} (0x{c.Guid.Full:X8}), made before the switch, stays on {zone}");
            return since;
        }

        static bool Looting(uint guid) => s_Loaded.TryGetValue(guid, out var c) && !c.IsDestroyed && c.IsOpen;

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Destroy))]
        static class Gone
        {
            static void Postfix(WorldObject __instance)
            {
                try
                {
                    if (!(__instance is Corpse c)) return;
                    uint g = c.Guid.Full;
                    s_Loaded.TryRemove(g, out _);
                    s_SaidFade.TryRemove(g, out _);
                    s_Index.Remove(g);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBattlefield: destroy: {e.Message}"); }
            }
        }
    }
}
