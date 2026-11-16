using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// THE NUMBERS AND WORDS OF DRINKING (Drinking.cs), pure C# so nettest checks them (case "drinking"). Ours, not retail:
    /// retail's brewed drinks only cast Intoxication I/II/III (-20/-30/-40 Coordination, 5 minutes) and nothing ever
    /// showed or passed out (no such string or effect in acclient_2013-09-06.exe). docs/DIVERGENCES.md "Drinking".
    ///
    /// A character carries a "drinks" count: each alcoholic drink adds its weight, and it wears off at DECAY_PER_S (one
    /// ordinary drink every 2.5 minutes). The client draws the effect from the count; at PASS the character passes out.
    /// </summary>
    public static class DrinkingRule
    {
        public const double PASS = 9.0;            // drinks: out cold (about nine ales in a few minutes)
        public const double CAP = 12.0;            // the count never climbs past this
        public const double SPELL_AT = 3.0;        // a drink with no spell of its own casts Intoxication I from here
        public const double DECAY_PER_S = 1.0 / 150.0;
        public const double OUT_S = 9.0;           // seconds out cold
        public const double HANGOVER_S = 180.0;    // the hangover's length (Feeblemind Self I, held longer than its 60 s)
        public const double HIT_RECENT_S = 30.0;   // a hit this recent is "in combat"
        public const float HOSTILE_M = 25f;        // an awake monster this near blocks passing out
        public const float PK_M = 75f;             // a red player this near blocks it for a player killer

        public const uint INTOX_I = 3527, INTOX_II = 3528, INTOX_III = 3529;
        public const uint HANGOVER_SPELL = 1457;   // Feeblemind Self I: Self -10
        public const uint HANGOVER_CURE = 23924;   // Ulgrim's Hangover Cure (retail): ours, it also clears ours

        /// <summary>Every drinkable alcohol on the world (ace_world, weenie type Food, surveyed 2026-11-16), by class id,
        /// with its weight in drinks. The three with their own Intoxication spell weigh what the spell says. The client's
        /// AcDrunkRule.ALCOHOL lists the same ids (nettest checks they agree): the Assistant never drinks one.</summary>
        public static readonly Dictionary<uint, double> WEIGHT = new Dictionary<uint, double>
        {
            [2469] = 0.5,   // Small Beer
            [2459] = 0.75,  // Kumiss
            [2451] = 1.0,   // Ale
            [81082] = 1.0,  // Danby's Ale
            [8391] = 1.0,   // Filled Beer Stein
            [2471] = 1.0,   // Stout
            [5831] = 1.0,   // Nelvaine's Olde Ispar Stout
            [2453] = 1.0,   // Cider
            [5136] = 1.0,   // Cove Apple Cider
            [2467] = 1.0,   // Red Wine
            [2465] = 1.0,   // Palm Wine
            [2462] = 1.25,  // Mead
            [2468] = 1.25,  // Sake
            // brewed (Brewmaster's Bible): five qualities of amber ale, lager and stout
            [29212] = 1.25, [29111] = 1.25, [29118] = 1.25,   // Distasteful
            [29211] = 1.25, [29110] = 1.25, [29117] = 1.25,   // Aromatic
            [29214] = 1.25, [29113] = 1.25, [29120] = 1.25,   // Homemade
            [29216] = 1.25, [29116] = 1.25, [29122] = 1.25,   // Tasty
            [29213] = 1.25, [29112] = 1.25, [29119] = 1.25,   // Glorious
            [29115] = 1.25, [29215] = 1.25, [29121] = 1.25,   // Spectacular
            [29106] = 1.25, // Bobo's Stout (Bobo's Quickening)
            [29109] = 1.25, // Tusker Spit Ale (Ketnan's Eye)
            [29104] = 1.25, // Amber Ape (Brighteyes' Favor)
            [32270] = 1.25, // Apothecary Zongo's Stout (Zongo's Fist)
            [32272] = 1.25, // Hunter's Stock Amber (Hunter's Hardiness)
            [29114] = 1.5,  // Nasty Lager (Intoxication I)
            [29107] = 2.0,  // Rancid Ale (Intoxication II)
            [29105] = 2.5,  // Angree's Angry Ale (Intoxication III)
            [32271] = 1.5,  // Duke Raoul's Distillation (Duke Raoul's Pride)
            [14912] = 1.5,  // Bottle of Champagne
            [4889] = 1.5, [45877] = 1.5,   // Distillery Ambrosia
            [4890] = 1.5,   // Distillery Dew
            [4891] = 1.5,   // Distillery Nectar
            [5023] = 1.5, [34550] = 1.5,   // Old Nectar (Folthid Cellar)
            [23773] = 2.0,  // Ulgrim's Home Brew
            [23126] = 2.0,  // Wah Chon's Winter Lager
        };

        /// <summary>The drinks that cast an Intoxication spell of their own (the mod adds none to them).</summary>
        public static bool OwnIntoxication(uint wcid) => wcid == 29114 || wcid == 29107 || wcid == 29105;

        public static bool IsAlcohol(uint wcid) => WEIGHT.ContainsKey(wcid);

        public static double Weight(uint wcid) => WEIGHT.TryGetValue(wcid, out var w) ? w : 0.0;

        /// <summary>The count now, from the count at <paramref name="at"/> (seconds).</summary>
        public static double Now(double drinks, double at, double now) =>
            Math.Max(0.0, drinks - Math.Max(0.0, now - at) * DECAY_PER_S);

        public static double Add(double now, double weight) => Math.Min(CAP, Math.Max(0.0, now) + Math.Max(0.0, weight));

        public static bool PassesOut(double after) => after >= PASS;

        /// <summary>0 sober, 1 tipsy (the screen sways), 2 drunk (blur, double vision), 3 staggering. The client draws the
        /// same bands from the count (AcDrunkRule.Effect).</summary>
        public static int Stage(double d) => d >= 6.0 ? 3 : d >= 4.0 ? 2 : d >= 2.0 ? 1 : 0;

        public enum Block { None, Fighting, Hostile, PlayerKiller, Stance }

        /// <summary>WHY A DRINK THAT WOULD PASS YOU OUT IS REFUSED. Never in combat, never with a monster near, never in
        /// reach of a red player (owner 2026-11-16); a combat stance counts as combat (there is no lying down in one).</summary>
        public static Block PassOutBlock(bool combatStance, bool hitRecently, bool hostileNear, bool redPlayerNear)
        {
            if (hitRecently) return Block.Fighting;
            if (hostileNear) return Block.Hostile;
            if (redPlayerNear) return Block.PlayerKiller;
            if (combatStance) return Block.Stance;
            return Block.None;
        }

        public static string RefuseLine(Block b)
        {
            switch (b)
            {
                case Block.Fighting: return "Your blood is still up from the fight. That drink can wait.";
                case Block.Hostile: return "You eye the drink, then the danger nearby, and think better of it.";
                case Block.PlayerKiller: return "Not with a player killer this close. You set the drink aside.";
                case Block.Stance: return "Put your weapon away first. That one would knock you flat.";
                default: return null;
            }
        }

        /// <summary>The line when the count climbs into a new stage (never twice for one stage in a row).</summary>
        public static string StageLine(int from, int to)
        {
            if (to <= from) return null;
            switch (to)
            {
                case 1: return "A pleasant warmth spreads through you.";
                case 2: return "The room has started to tilt. Just a little.";
                case 3: return "Your feet seem to have their own ideas about where to go.";
                default: return null;
            }
        }

        public const string PASS_LINE = "You pass out...";
        public const string WAKE_EARLY_LINE = "You come to with a start! Something is wrong.";

        public static readonly string[] WAKE_LINES =
        {
            "You wake up with your face in a puddle of something. Best not to ask what.",
            "You come to. Someone has drawn a mustache on you. Probably.",
            "You wake up clutching a tankard you do not remember buying.",
            "You come around. Your head is pounding like a Lugian at a forge.",
            "You wake up. A drudge was going through your pockets, but it ran off. Probably.",
            "You come to, sober and sorry. Very, very sorry.",
            "You wake up humming a song you are glad nobody heard. You hope.",
        };

        public const string HANGOVER_LINE = "Your head is splitting. You have a hangover.";
        public const string CURE_LINE = "Ulgrim's cure works. Your head clears and the world holds still.";

        public static string WakeLine(int pick) => WAKE_LINES[(pick % WAKE_LINES.Length + WAKE_LINES.Length) % WAKE_LINES.Length];

        /// <summary>A barkeeper's word to a drinker, by stage (2 drunk, 3 staggering, 4 passing out).</summary>
        public static readonly string[][] BARKEEP_LINES =
        {
            new[] { "Easy there, friend. The ale is not going anywhere.", "You might want some bread with that.", "Another? You are a brave one." },
            new[] { "I think that is enough for you, hero.", "Mind the stairs on your way out.", "You will be singing soon. Please do not sing." },
            new[] { "Somebody fetch a blanket.", "Not again. Leave that one where they fell.", "Out cold. Mind you do not trip over them." },
        };

        /// <summary>Is this NPC name a barkeeper (Holtburg's "Barkeeper Wilomine", "... the Barkeep", "Bartender ...")?</summary>
        public static bool IsBarkeep(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Barkeep", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Bartender", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Barmaid", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Innkeeper", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public const double BARKEEP_GAP_S = 120.0;   // one word per drinker per two minutes at most
    }
}
