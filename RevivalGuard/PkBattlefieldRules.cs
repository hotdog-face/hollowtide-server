using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// PK BATTLEFIELDS, THE PURE HALF (no ACE types; tools/nettest compiles this file by path, PkBattlefieldCase).
    /// Owner 2026-11-22 (docs/proposals/pk-battlefield-corpses.md): in a designated PK zone any player corpse never
    /// rots on a timer, empty or not, looted or not, across restarts; looting is unchanged; each zone holds at most a
    /// cap (50, or a density for a spread-out zone) and over it the oldest corpse rots normally (15 s). No renaming,
    /// no text. PkBattlefield.cs runs it; the switch is pk_battlefield_corpses (off).
    /// </summary>
    public static partial class PkBattlefield
    {
        public const int DEFAULT_CAP = 50;
        /// <summary>Metres added to a PK Night spot's holding circle (pk_night_radius) to make its battlefield.</summary>
        public const double NIGHT_MARGIN = 40;
        /// <summary>A kept corpse's TimeToRot is held at least here while it is kept (so turning the switch off later
        /// lets it rot within the hour, or in 15 s if empty, the ACE way).</summary>
        public const double KEPT_TTR = 3600;

        /// <summary>A circle zone (map coordinates, metres) or a mansion landblock (Landblock != 0). PerHectare > 0 makes
        /// the cap a density: corpses per 10,000 m2 of the circle, never under the default cap.</summary>
        public sealed class Zone
        {
            public string Key, Name;
            public double Ns, Ew, Radius, PerHectare;
            public int Cap;
            public ushort Landblock;
            public override string ToString() => Landblock != 0 ? $"{Name} (landblock {Landblock:X4})" : $"{Name} ({Ns:0.00}N {Ew:0.00}E, {Radius:0} m)";
        }

        /// <summary>THE ZONE LIST: one line a zone. Mansions and the PK Night spots are added from their own tables.</summary>
        public static readonly Zone[] FIXED =
        {
            // the Dark Isle south beach (owner: "any corpse on the island near 84.7N,58.5E"): 150 m takes in the sand by
            // the lifestone, PK Night's circle (84.72N 58.42E) and its portal landing (84.57N 58.25E, about 75 m off).
            new Zone { Key = "darkisle", Name = "the Dark Isle south beach", Ns = 84.7, Ew = 58.5, Radius = 150 },
            // Arwic's lawn (owner 2026-11-22: "arwic lawn spans all around arwic. 33.3N,56.3E and here are prominent
            // areas 33.4N,57.2E"; where Darktide fought). The town and its lawns, 200 m around his centre, then the
            // prominent east lawn as its own zone (first match wins; they barely overlap). Spread out: 10 a hectare.
            new Zone { Key = "arwiclawn", Name = "Arwic's lawn", Ns = 33.3, Ew = 56.3, Radius = 200, PerHectare = 10 },
            new Zone { Key = "arwiceast", Name = "Arwic's east lawn", Ns = 33.4, Ew = 57.2, Radius = 150, PerHectare = 10 },
        };

        public static int CapOf(Zone z, int defaultCap = DEFAULT_CAP)
        {
            if (defaultCap < 1) defaultCap = 1;
            if (z == null) return defaultCap;
            if (z.PerHectare > 0 && z.Radius > 0)
                return Math.Max(defaultCap, (int)Math.Round(z.PerHectare * Math.PI * z.Radius * z.Radius / 10000.0));
            return z.Cap > 0 ? z.Cap : defaultCap;
        }

        /// <summary>The circle zones in order of precedence: the fixed ones, then every PK Night spot (its circle plus
        /// NIGHT_MARGIN). A point in two takes the first.</summary>
        public static List<Zone> CircleZones(double nightRadius)
        {
            var list = new List<Zone>(FIXED);
            foreach (var s in PkNight.SPOTS)
                list.Add(new Zone { Key = "night-" + s.Key, Name = s.Name, Ns = s.Ns, Ew = s.Ew, Radius = nightRadius + NIGHT_MARGIN });
            return list;
        }

        public static Zone MansionZone(ushort landblock) =>
            new Zone { Key = $"mansion-{landblock:X4}", Name = "a mansion's grounds", Landblock = landblock };

        /// <summary>Global metres of a landblock-local point (ACE: 192 m a landblock); the same frame as
        /// PkNight.GlobalOfMap.</summary>
        public static (double x, double y) Global(ushort landblock, double x, double y) =>
            ((landblock >> 8) * 192.0 + x, (landblock & 0xFF) * 192.0 + y);

        public static bool InCircle(Zone z, double gx, double gy)
        {
            if (z == null || z.Radius <= 0) return false;
            var c = PkNight.GlobalOfMap(z.Ns, z.Ew);
            return (gx - c.x) * (gx - c.x) + (gy - c.y) * (gy - c.y) <= z.Radius * z.Radius;
        }

        /// <summary>The battlefield a point is in: the first circle containing it, else the mansion landblock it is on
        /// (isMansionBlock from MansionIndex), else null.</summary>
        public static Zone ZoneAt(IList<Zone> circles, ushort landblock, double x, double y, bool isMansionBlock)
        {
            var (gx, gy) = Global(landblock, x, y);
            foreach (var z in circles) if (InCircle(z, gx, gy)) return z;
            return isMansionBlock ? MansionZone(landblock) : null;
        }

        // ---------------------------------------------------------------- the cap

        /// <summary>One kept corpse: its guid, when it was kept (unix ms; the order), and whether a player has it open.</summary>
        public struct Kept { public uint Guid; public long Since; public bool Looting; }

        /// <summary>Which corpses of one zone rot: the oldest (count - cap), oldest first by Since (then guid), skipping
        /// one a player has open (the next oldest goes instead), never the newest (if every older one is open, the zone runs
        /// over until a looter closes one). Empty when the zone is at or under its cap.</summary>
        public static HashSet<uint> ToRot(IEnumerable<Kept> zone, int cap)
        {
            var all = zone.OrderBy(k => k.Since).ThenBy(k => k.Guid).ToList();
            var rot = new HashSet<uint>();
            int excess = all.Count - Math.Max(1, cap);
            for (int i = 0; i < all.Count - 1; i++)   // never the newest: the corpse that just landed stays
            {
                var k = all[i];
                if (rot.Count >= excess) break;
                if (!k.Looting) rot.Add(k.Guid);
            }
            return rot;
        }

        // ---------------------------------------------------------------- the decisions

        public enum AtDeath { None, Timer, Battlefield }

        /// <summary>What a fresh player corpse gets. Switch off: only a mansion block, and only MansionPk's wall-clock timer
        /// (mansion_corpse_decay_seconds; 0 = none), exactly as before. Switch on: any battlefield zone keeps it.</summary>
        public static AtDeath OnDeath(bool switchOn, bool inZone, bool mansionBlock, long mansionSeconds)
        {
            if (switchOn && inZone) return AtDeath.Battlefield;
            if (mansionBlock && mansionSeconds > 0) return AtDeath.Timer;
            return AtDeath.None;
        }

        public enum Tick { Ace, Hold, Fade }

        /// <summary>One Decay call on a corpse. `keptSince` is the battlefield mark (null none), `trophyUntil` MansionPk's
        /// expiry (unix s, null none), `ttr` the TimeToRot now. Returns what to do and the TimeToRot to set (NaN: leave).
        /// Hold = skip ACE's Decay this tick. A battlefield corpse is held while the switch is on and the cap keeps it;
        /// over the cap it fades (15 s) the ACE way; switch off, ACE decides. The timer: MansionPk's rule, unchanged.</summary>
        public static (Tick act, double ttr) OnDecay(bool switchOn, long? keptSince, bool keptByCap, long? trophyUntil,
                                                    double ttr, double nowUnix, double emptyDecay = 15.0)
        {
            if (keptSince != null)
            {
                if (!switchOn) return (Tick.Ace, double.NaN);
                if (keptByCap) return (Tick.Hold, Math.Max(ttr, KEPT_TTR));
                return (Tick.Fade, Math.Min(ttr, emptyDecay));
            }
            if (trophyUntil == null) return (Tick.Ace, double.NaN);
            // corpse_spam_limit or an earlier pass cut it to 15 s: let it go
            if (ttr <= emptyDecay) return (Tick.Ace, double.NaN);
            double left = trophyUntil.Value - nowUnix;
            if (left > emptyDecay) return (Tick.Hold, left);
            return (Tick.Ace, Math.Max(0.5, left));
        }

        // ---------------------------------------------------------------- the index

        /// <summary>Kept corpses by zone key: rebuilt at startup from the shard database (every corpse carrying the
        /// mark, and where it lies), then kept current at each death and destroy. Thread-safe.</summary>
        public sealed class CorpseIndex
        {
            readonly object m_Lock = new object();
            readonly Dictionary<string, Dictionary<uint, long>> m_ByZone = new Dictionary<string, Dictionary<uint, long>>();
            readonly Dictionary<uint, string> m_ZoneOf = new Dictionary<uint, string>();

            public void Add(string zone, uint guid, long since)
            {
                lock (m_Lock)
                {
                    if (m_ZoneOf.TryGetValue(guid, out var was) && was != zone) m_ByZone[was].Remove(guid);
                    m_ZoneOf[guid] = zone;
                    if (!m_ByZone.TryGetValue(zone, out var d)) m_ByZone[zone] = d = new Dictionary<uint, long>();
                    d[guid] = since;
                }
            }

            public bool Remove(uint guid)
            {
                lock (m_Lock)
                {
                    if (!m_ZoneOf.TryGetValue(guid, out var z)) return false;
                    m_ZoneOf.Remove(guid);
                    m_ByZone[z].Remove(guid);
                    return true;
                }
            }

            public bool Has(uint guid) { lock (m_Lock) return m_ZoneOf.ContainsKey(guid); }
            public string ZoneKeyOf(uint guid) { lock (m_Lock) return m_ZoneOf.TryGetValue(guid, out var z) ? z : null; }
            public int Count(string zone) { lock (m_Lock) return m_ByZone.TryGetValue(zone, out var d) ? d.Count : 0; }

            public Dictionary<string, int> Counts()
            {
                lock (m_Lock) return m_ByZone.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            }

            /// <summary>Is this corpse inside its zone's cap? `looting(guid)` says whether a player has one open.</summary>
            public bool KeptByCap(uint guid, int cap, Func<uint, bool> looting)
            {
                List<Kept> zone;
                lock (m_Lock)
                {
                    if (!m_ZoneOf.TryGetValue(guid, out var z)) return false;
                    var d = m_ByZone[z];
                    if (d.Count <= cap) return true;
                    zone = d.Select(kv => new Kept { Guid = kv.Key, Since = kv.Value }).ToList();
                }
                for (int i = 0; i < zone.Count; i++) { var k = zone[i]; k.Looting = looting(k.Guid); zone[i] = k; }
                return !ToRot(zone, cap).Contains(guid);
            }
        }
    }
}
