using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// /deaths, THE PURE HALF (no ACE types; tools/nettest compiles this file by path). Owner 2026-11-16, "Yes, add
    /// /deaths", for Numero-uno's "how do I look up who killed me after log off". The record kept for each death, the
    /// cap, the place words and every line a player reads. DeathLog.cs records on the world loop and answers the command.
    /// </summary>
    public static class DeathLogRule
    {
        public const int KEEP = 10;               // per character, newest first
        public const float NEAR_TOWN = 2.5f;      // map units (240 m each) from a town's centre that read "near <town>"

        public enum Cause { Unknown = 0, Monster = 1, Player = 2, Pet = 3, Fall = 4, OwnMagic = 5, Suicide = 6 }

        public sealed class Entry
        {
            public DateTime Utc { get; set; }
            public Cause Cause { get; set; }
            public string Killer { get; set; }     // the monster or player; for a pet, the pet
            public string Owner { get; set; }      // a pet's owner
            public bool Pk { get; set; }           // a PK or PK Lite death (ACE's IsPKDeath / IsPKLiteDeath)
            public string Where { get; set; }      // "at 42.1N, 33.6E near Holtburg", "in the Holtburg Redoubt"
        }

        /// <summary>Put a death at the front and keep the newest KEEP. ACE can run a death twice for one death
        /// (BossTrophies, ForgeWarden): a second record within 3 s with the same cause and killer is dropped.</summary>
        public static bool Add(List<Entry> list, Entry e)
        {
            if (list == null || e == null) return false;
            if (list.Count > 0)
            {
                var top = list[0];
                if (Math.Abs((e.Utc - top.Utc).TotalSeconds) < 3 && top.Cause == e.Cause && top.Killer == e.Killer) return false;
            }
            list.Insert(0, e);
            if (list.Count > KEEP) list.RemoveRange(KEEP, list.Count - KEEP);
            return true;
        }

        /// <summary>The cause, from what ACE hands Player.Die: the killer's guid and name, whether it is a player, the
        /// victim's own guid, the pet owner's name if any, and whether a fall or /die is under way.</summary>
        public static Cause Classify(bool haveKiller, uint killerGuid, uint selfGuid, bool killerIsPlayer, string petOwner, bool falling, bool suicide)
        {
            if (suicide) return Cause.Suicide;
            if (falling) return Cause.Fall;
            if (!haveKiller) return Cause.Unknown;
            if (killerGuid == selfGuid) return Cause.OwnMagic;
            if (killerIsPlayer) return Cause.Player;
            if (!string.IsNullOrEmpty(petOwner)) return Cause.Pet;
            return Cause.Monster;
        }

        // ---------------------------------------------------------------- where

        /// <summary>Map coordinates the way /corpse prints them (retail's radar formatter, RadarHud.AcCoords:
        /// metres / 240 - 101.95, one decimal, "N, E"). gx/gy are world metres (landblock * 192 + local).</summary>
        public static string Coords(float gx, float gy, out float ns, out float ew)
        {
            ew = gx / 240f - 101.95f; ns = gy / 240f - 101.95f;
            return Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N" : "S") + ", "
                 + Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");
        }

        /// <summary>The nearest town within NEAR_TOWN map units, or null.</summary>
        public static string NearTown(float ns, float ew, IList<(string name, float ns, float ew)> towns)
        {
            if (towns == null) return null;
            string best = null; float bestD = NEAR_TOWN * NEAR_TOWN;
            foreach (var t in towns)
            {
                float dn = t.ns - ns, de = t.ew - ew, d = dn * dn + de * de;
                if (d <= bestD) { bestD = d; best = t.name; }
            }
            return best;
        }

        /// <summary>The place words. cell = ACE's full cell id; x/y = the position inside the landblock (metres).
        /// Outdoors: "at 42.1N, 33.6E" (+ " near Holtburg"). Inside a building on the surface: "indoors at ..." with the
        /// same coordinates. A dungeon landblock: "in &lt;name&gt;" from the portal-destination table, else "in a dungeon".</summary>
        public static string Where(uint cell, float x, float y, bool dungeon, string dungeonName, IList<(string, float, float)> towns)
        {
            if (cell == 0) return "at an unknown place";
            if (dungeon) return string.IsNullOrEmpty(dungeonName) ? "in a dungeon" : "in " + dungeonName;
            bool indoors = (cell & 0xFFFFu) >= 0x100u;
            float gx = ((cell >> 24) & 0xFFu) * 192f + x, gy = ((cell >> 16) & 0xFFu) * 192f + y;
            string c = Coords(gx, gy, out float ns, out float ew);
            string town = NearTown(ns, ew, towns);
            return (indoors ? "indoors at " : "at ") + c + (town != null ? " near " + town : "");
        }

        // ---------------------------------------------------------------- the lines

        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "just now";
            if (t.TotalHours < 1) return N((int)t.TotalMinutes, "minute") + " ago";
            if (t.TotalDays < 1) return N((int)t.TotalHours, "hour") + " ago";
            return N((int)t.TotalDays, "day") + " ago";
        }

        static string N(int n, string unit) => n + " " + unit + (n == 1 ? "" : "s");

        /// <summary>"Nov 16, 3:04 PM PST" from a Pacific wall-clock time and its UTC offset in hours.</summary>
        public static string Clock(DateTime pacificLocal, int offsetHours) =>
            pacificLocal.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture) + (offsetHours == -7 ? " PDT" : " PST");

        /// <summary>Who or what did it, as the line's opening words. self = the reader is the one who died.</summary>
        public static string What(Entry e, bool self)
        {
            string their = self ? "your" : "their";
            switch (e.Cause)
            {
                case Cause.Player: return "Killed by " + Clean(e.Killer) + (e.Pk ? " (a player killer death)" : "");
                case Cause.Monster: return "Killed by " + Clean(e.Killer);   // retail names the creature bare: "Drudge Skulker kills you"
                case Cause.Pet: return "Killed by " + Clean(e.Owner) + "'s " + Clean(e.Killer);
                case Cause.Fall: return "Died from a fall";
                case Cause.OwnMagic: return "Killed by " + their + " own magic";
                case Cause.Suicide: return "Died by " + their + " own hand";
                default: return "Died of unknown causes";
            }
        }

        static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? "someone" : s.TrimStart('+').Replace("—", "-");

        /// <summary>The whole answer. who = null for your own deaths, else the character's name (staff).</summary>
        public static List<string> Lines(IList<Entry> list, string who, DateTime nowUtc, Func<DateTime, string> clock)
        {
            var o = new List<string>();
            bool self = who == null;
            if (list == null || list.Count == 0)
            {
                o.Add(self ? "You have no deaths on record." : Clean(who) + " has no deaths on record.");
                return o;
            }
            o.Add((self ? "Your last " : Clean(who) + "'s last ") + (list.Count == 1 ? "death:" : list.Count + " deaths, newest first:"));
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var sb = new StringBuilder();
                sb.Append(i + 1).Append(". ").Append(What(e, self));
                if (!string.IsNullOrEmpty(e.Where)) sb.Append(' ').Append(e.Where);
                sb.Append(", ").Append(Ago(nowUtc - e.Utc));
                if (clock != null) sb.Append(" (").Append(clock(e.Utc)).Append(')');
                sb.Append('.');
                o.Add(sb.ToString());
            }
            return o;
        }
    }
}
