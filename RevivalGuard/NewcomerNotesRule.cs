using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>The pure part of NewcomerNotes: who gets the first-gank note and the swap-coin notice, and every line a player reads.</summary>
    public static class NewcomerNotesRule
    {
        /// <summary>The note goes to victims below this level (the plan's "under 30").</summary>
        public const int GANK_UNDER = 30;
        /// <summary>The swap-coin notice comes at this level</summary>
        public const int SWAP_LEVEL = 30;
        /// <summary>Quest stamps (ACE character quest registry): once per character, kept across logins and restarts.</summary>
        public const string GANK_FLAG = "RgFirstGankNote", SWAP_FLAG = "RgSwapCoinNotice";
        /// <summary>The Emissary's own stamp (gen_imbue_swap_gift.py): a character with it has its coins already.</summary>
        public const string SWAP_GIFT_QUEST = "ImbueSwapCoinGift";
        public const string ARWIC_EMISSARY = "33.6N 56.9E";

        public static bool GankNoteDue(int victimLevel, bool alreadyNoted, bool botOrStaff, bool killerIsSelf) =>
            !alreadyNoted && !botOrStaff && !killerIsSelf && victimLevel < GANK_UNDER;

        public static bool SwapNoticeDue(int before, int now, bool alreadyNoticed, bool hasCoins, bool botOrStaff) =>
            before < SWAP_LEVEL && now >= SWAP_LEVEL && !alreadyNoticed && !hasCoins && !botOrStaff;

        /// <summary>Luminance at 200: one notice at level LUM_LEVEL, or at the first login at or past it</summary>
        public const int LUM_LEVEL = 200;
        public const string LUM_FLAG = "RgLuminanceNotice";
        /// <summary>Nalicana stamps it when she takes Battle Lord Gregor's Mnemosyne (quest-guide oracleportalentry).</summary>
        public const string LUM_OPEN_QUEST = "OracleLuminanceRewardsAccess_1110";

        public static bool LuminanceNoticeDue(int level, bool alreadyNoticed, bool luminanceOpen, bool botOrStaff) =>
            level >= LUM_LEVEL && !alreadyNoticed && !luminanceOpen && !botOrStaff;

        /// <summary>The luminance notice: retail content, where to go, what is lost until then.</summary>
        public static string LuminanceLine() =>
            "You can earn luminance now, but it starts at Nalicana's Test: kill Battle Lord Gregor in the Gurog Ice Cave (84.9N 7.1W) "
          + "and give his Mnemosyne to Nalicana in Asheron's Castle. Until then every luminance award is lost. The Journal's Where next has it.";

        /// <summary>What the server knew at the moment the note is written.</summary>
        public sealed class Facts
        {
            public string Killer;
            public string Where;              // DeathLogRule.Where words ("at 42.1N, 33.6E near Holtburg"), or null
            public double CorpseSeconds = -1; // the corpse's own timer (Corpse.TimeToRot); -1 = not seen
            public int CorpseItems = -1;      // items on it; 0 = nothing dropped (ACE then rots it in 15 s)
            public bool Outdoors;             // /corpse remembers only an outdoor corpse (retail and ACE)
            public double VitaePct = -1;      // e.g. 5 for 95% vitae
            public double VitaeStepPct = 5, VitaeMaxPct = 40;   // the cap at the victim's level (ACE GetMinVitae: 3% a level, up to vitae_penalty_max)
            public int RecruitingListings = -1; // -1 = /recruit off, or the victim is not free to join one
            public bool InAllegiance;
            public DateTime? PkNightStartUtc;   // null = PK Night off
            public DateTime PkNightLocal;       // the same moment in Pacific time
            public bool PkNightRunningNow;
            public bool KillerWanted;
            public bool FestivalSeason;
        }

        /// <summary>The small notice at the top of the screen.</summary>
        public const string NOTICE = "You were killed by another player. Your chat window says what to do next.";

        /// <summary>The chat lines, in order.</summary>
        public static List<string> GankLines(Facts f)
        {
            var l = new List<string>();
            l.Add($"{f.Killer ?? "Another player"} killed you. On Hollowtide every character from level 10 can be attacked by other players. It happens to everyone, so here is what to do next.");

            if (f.CorpseItems == 0)
                l.Add("You dropped nothing this time, so there is no corpse to fetch.");
            else if (f.CorpseSeconds > 0)
                l.Add($"Your corpse lies {f.Where ?? "where you fell"} and stays for {Duration(f.CorpseSeconds)}, or until it is emptied. "
                    + "Your killer may loot it too, so go back for it soon."
                    + (f.Outdoors ? " Type /corpse to see where it is." : ""));

            if (f.VitaePct > 0)
                l.Add($"You now have {Pct(f.VitaePct)} Vitae: your skills and attributes are {Pct(f.VitaePct)} lower until you earn it back as experience. "
                    + $"Each death adds {Pct(f.VitaeStepPct)}, up to {Pct(f.VitaeMaxPct)} at your level.");

            l.Add(f.FestivalSeason
                ? "Get buffed again for free: the Pumpkin Buffer stands in Holtburg (42.1N 33.6E), Glenden Wood (29.8N 27.3E), Yaraq (21.5S 1.8W) and Shoushi (33.5S 72.8E) until November 1."
                : "Get buffed again for free: Azimar the Gleaming blesses anyone in Holtburg (42.1N 33.6E), Glenden Wood (29.8N 27.3E), Yaraq (21.5S 1.8W) and Shoushi (33.5S 72.8E).");

            if (!f.InAllegiance)
                l.Add(f.RecruitingListings > 0
                    ? $"Allegiances look after their own. {(f.RecruitingListings == 1 ? "One allegiance is" : f.RecruitingListings + " allegiances are")} looking for new players right now: type /recruit to see {(f.RecruitingListings == 1 ? "it" : "them")}."
                    : "Allegiances look after their own. Ask in General chat (/g) or on Discord who is recruiting.");

            if (f.PkNightStartUtc.HasValue)
                l.Add(f.PkNightRunningNow
                    ? "Want a fair fight? PK Night is on right now, and runs every other Sunday at 2 PM Pacific."
                    : $"Want a fair fight? PK Night runs every other Sunday at 2 PM Pacific. The next one is {When(f.PkNightLocal)} Pacific.");

            if (f.KillerWanted && f.Killer != null)
                l.Add($"{f.Killer} is Wanted on the PK board: five fair kills in a row without falling. Whoever ends the streak earns extra renown.");

            l.Add("Type /deaths any time to see who killed you and where.");
            return l;
        }

        /// <summary>The swap-coin notice at SWAP_LEVEL.</summary>
        public static string SwapLine() =>
            $"A gift is waiting for you in Arwic: the Emissary of Asheron, upstairs in the northeast building ({ARWIC_EMISSARY}), gives every character "
          + "5 Imbue Swap Coins, once. Spend them at the Weapon Imbue Vendor beside him on Foolproof imbue gems and slayer gems for a weapon you mean to keep.";

        public static string Duration(double seconds)
        {
            int min = (int)Math.Round(seconds / 60.0);
            if (min < 60) return min <= 1 ? "a minute" : min + " minutes";
            int h = min / 60, m = min % 60;
            string hs = h == 1 ? "1 hour" : h + " hours";
            return m == 0 ? hs : $"{hs} {m} minutes";
        }

        public static string Pct(double p) =>
            (Math.Abs(p - Math.Round(p)) < 0.05 ? Math.Round(p).ToString(CultureInfo.InvariantCulture) : p.ToString("0.#", CultureInfo.InvariantCulture)) + "%";

        public static string When(DateTime local)
        {
            int h = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
            string ampm = local.Hour < 12 ? "AM" : "PM";
            string t = local.Minute == 0 ? $"{h} {ampm}" : $"{h}:{local.Minute:00} {ampm}";
            return $"{local.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)} at {t}";
        }

        /// <summary>Festival Season, when the Pumpkin Buffer takes Azimar's spots (SeasonalCalendar; wiki Azimar page).</summary>
        public static bool FestivalSeason(DateTime local) =>
            local.Month == 10 || (local.Month == 11 && local.Day == 1);
    }
}
