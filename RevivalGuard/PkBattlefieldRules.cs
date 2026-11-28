using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>PK battlefields, the pure half (no ACE types; tools/nettest compiles this file by path, PkBattlefieldCase).</summary>
    public static partial class PkBattlefield
    {
        public const int DEFAULT_CAP = 50;
        /// <summary>Metres added to a PK Night spot's holding circle (pk_night_radius) to make its battlefield.</summary>
        public const double NIGHT_MARGIN = 40;
        /// <summary>A kept corpse's TimeToRot is held at least here while it is kept.</summary>
        public const double KEPT_TTR = 3600;

        /// <summary>A circle zone (map coordinates, metres) or a mansion landblock (Landblock != 0).</summary>
        public sealed class Zone
        {
            public string Key, Name;
            public double Ns, Ew, Radius, PerHectare;
            public int Cap;
            public ushort Landblock;
            public override string ToString() => Landblock != 0 ? $"{Name} (landblock {Landblock:X4})" : $"{Name} ({Ns:0.00}N {Ew:0.00}E, {Radius:0} m)";
        }

        /// <summary>The zone list: one line a zone.</summary>
        public static readonly Zone[] FIXED =
        {
            // the Dark Isle south beach: 150 m takes in the sand by the lifestone, PK Night's circle and its portal landing.
            new Zone { Key = "darkisle", Name = "the Dark Isle south beach", Ns = 84.7, Ew = 58.5, Radius = 150 },
            // Arwic's lawn.
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

        /// <summary>The circle zones in order of precedence: the fixed ones, then every PK Night spot (its circle plus NIGHT_MARGIN).</summary>
        public static List<Zone> CircleZones(double nightRadius)
        {
            var list = new List<Zone>(FIXED);
            foreach (var s in PkNight.SPOTS)
                list.Add(new Zone { Key = "night-" + s.Key, Name = s.Name, Ns = s.Ns, Ew = s.Ew, Radius = nightRadius + NIGHT_MARGIN });
            return list;
        }

        public static Zone MansionZone(ushort landblock) =>
            new Zone { Key = $"mansion-{landblock:X4}", Name = "a mansion's grounds", Landblock = landblock };

        /// <summary>Global metres of a landblock-local point (ACE: 192 m a landblock); the same frame as PkNight.GlobalOfMap.</summary>
        public static (double x, double y) Global(ushort landblock, double x, double y) =>
            ((landblock >> 8) * 192.0 + x, (landblock & 0xFF) * 192.0 + y);

        public static bool InCircle(Zone z, double gx, double gy)
        {
            if (z == null || z.Radius <= 0) return false;
            var c = PkNight.GlobalOfMap(z.Ns, z.Ew);
            return (gx - c.x) * (gx - c.x) + (gy - c.y) * (gy - c.y) <= z.Radius * z.Radius;
        }

        /// <summary>The battlefield a point is in: the first circle containing it, else the mansion landblock it is on, else null.</summary>
        public static Zone ZoneAt(IList<Zone> circles, ushort landblock, double x, double y, bool isMansionBlock)
        {
            var (gx, gy) = Global(landblock, x, y);
            foreach (var z in circles) if (InCircle(z, gx, gy)) return z;
            return isMansionBlock ? MansionZone(landblock) : null;
        }

        // ---------------------------------------------------------------- the cap

        /// <summary>One kept corpse: its guid, when it was kept (unix ms; the order), and whether a player has it open.</summary>
        public struct Kept { public uint Guid; public long Since; public bool Looting; }

        public static HashSet<uint> ToRot(IEnumerable<Kept> zone, int cap)
        {
            var all = zone.OrderBy(k => k.Since).ThenBy(k => k.Guid).ToList();
            var rot = new HashSet<uint>();
            int excess = all.Count - Math.Max(1, cap);
            for (int i = 0; i < all.Count - 1; i++)
            {
                var k = all[i];
                if (rot.Count >= excess) break;
                if (!k.Looting) rot.Add(k.Guid);
            }
            return rot;
        }

        // ---------------------------------------------------------------- the decisions

        public enum AtDeath { None, Timer, Battlefield }

        /// <summary>What a fresh player corpse gets.</summary>
        public static AtDeath OnDeath(bool switchOn, bool inZone, bool mansionBlock, long mansionSeconds)
        {
            if (switchOn && inZone) return AtDeath.Battlefield;
            if (mansionBlock && mansionSeconds > 0) return AtDeath.Timer;
            return AtDeath.None;
        }

        public enum Tick { Ace, Hold, Fade }

        /// <summary>One Decay call on a corpse.</summary>
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

        /// <summary>Kept corpses by zone key: rebuilt at startup from the shard database, then kept current at each death and destroy.</summary>
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

            /// <summary>Is this corpse inside its zone's cap?</summary>
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
