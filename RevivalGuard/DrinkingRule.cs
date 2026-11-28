using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The numbers and words of drinking (Drinking.cs), pure C# so nettest checks them (case "drinking").</summary>
    public static partial class DrinkingRule
    {
        public const double PASS = 9.0;            // drinks: out cold (about nine ales in a few minutes)
        public const double CAP = 12.0;
        public const double SPELL_AT = 3.0;        // a drink with no spell of its own casts Intoxication I from here
        public const double DECAY_PER_S = 1.0 / 150.0;
        public const double OUT_S = 9.0;           // seconds out cold
        public const double HANGOVER_S = 180.0;    // the hangover's length (Feeblemind Self I, held longer than its 60 s)
        public const double HIT_RECENT_S = 30.0;   // a hit this recent is "in combat"
        public const float HOSTILE_M = 25f;        // an awake monster this near blocks passing out
        public const float PK_M = 75f;             // a red player this near blocks it for a player killer

        public const uint INTOX_I = 3527, INTOX_II = 3528, INTOX_III = 3529;
        public const uint HANGOVER_SPELL = 1457;   // Feeblemind Self I: Self -10
        public const uint HANGOVER_COORD = 1385;   // Clumsiness Self I: Coordination -10
        public const uint HANGOVER_QUICK = 1409;   // Slowness Self I: Quickness -10

        // ---- the picture: brief by design ----------------------------------------------------------

        public const double PICTURE_S = 90.0;        // from your last drink to a clear screen, at most
        /// <summary>A pass-out drink refused for danger</summary>
        public const double BLOCK_COUNT = 3.0, BLOCK_PICTURE = 2.0, BLOCK_PICTURE_S = 15.0;

        /// <summary>The picture's count since seconds after it was set to d0, falling at rate per second.</summary>
        public static double Picture(double d0, double rate, double since) => Math.Max(0.0, d0 - Math.Max(0.0, since) * Math.Max(0.0, rate));

        /// <summary>The rate that takes d0 to nothing in secs seconds.</summary>
        public static double PictureRate(double d0, double secs) => d0 <= 0.0 || secs <= 0.0 ? 0.0 : d0 / secs;

        // ---- carried off: waking in a town tavern ---------------------------------------------------

        public const double CARRY_EXTRA_S = 30.0;
        public const double CARRY_TIMEOUT_S = 45.0;    // no arrival by then: wake wherever the body is
        public const double CARRY_WAKE_AFTER_S = 2.5;
        public const double OLD_CLIENT_HANGOVER_VIEW_S = 4.0;  // lying in the tavern a moment before waking
        public const int CARRY_LOCAL_BELOW = 20;       // under this level: only one of the three taverns nearest the fall
        public const int CARRY_PK_BLOCKS = 1;

        /// <summary>Choose a tavern: the allowed ones, for a low level only among the three nearest fromLb; null when none.</summary>
        public static Tavern ChooseTavern(IList<Tavern> all, Func<Tavern, bool> allowed, int level, uint fromLb, int pick)
        {
            IEnumerable<Tavern> pool = all;
            if (level < CARRY_LOCAL_BELOW)
                pool = all.OrderBy(t => BlockDistance(t.Cell >> 16, fromLb)).Take(3);
            var ok = pool.Where(t => allowed == null || allowed(t)).ToList();
            if (ok.Count == 0) return null;
            return ok[(pick % ok.Count + ok.Count) % ok.Count];
        }

        /// <summary>Chebyshev distance in land blocks between two block ids (0xXXYY).</summary>
        public static int BlockDistance(uint a, uint b) =>
            Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)), Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)));

        /// <summary>The wake line in the tavern. {0} the town.</summary>
        public static readonly string[] CARRY_LINES =
        {
            "You wake up on the floor of a tavern in {0}. Someone carried you here and tucked your pack under your head. Everything is still in it.",
            "You come to in {0}, propped in a tavern corner. A note pinned to your shirt reads: Found this one in the road. Pack untouched. You owe us a round.",
            "You wake up in {0} with no idea how you got here. Your pack is all there. Your dignity is not.",
            "You wake up in a tavern in {0}, wrapped in a borrowed blanket. Someone counted your pyreals and put every one of them back.",
            "You come around in {0}. The barkeeper says two strangers dropped you off and left without a word. Your pack is exactly as you left it.",
        };
        public const string CARRY_GONE_OTHERS = "Two passing strangers haul {0} off by the boots.";
        public const string CARRY_ARRIVE_OTHERS = "Two strangers drop {0} on the floor and leave without a word.";

        // ---- the stumble and the hangover ---------------------------------------------------------

        public const double STUMBLE_S = 6.5;
        /// <summary>When the stumble's fall begins, for the onlookers' line.</summary>
        public const double FALL_AT_S = 4.4;
        public const double SERVER_WOAH_S = 2.1;

        public const double HEALTH_LOSS = 1.0 / 3.0;
        public const double HEALTH_FLOOR = 0.25;

        /// <summary>The hangover lasts until you have RESTED this long in all, or HANGOVER_MAX_S passes, or Ulgrim's cure.</summary>
        public const double REST_NEEDED_S = 60.0;
        public const double HANGOVER_MAX_S = 600.0;
        public const double HANGOVER_TICK_S = 5.0;
        public const double GROAN_GAP_S = 75.0;

        public static int HealthAfterWaking(int current, int max)
        {
            if (current <= 0 || max <= 0) return current;
            int floor = Math.Max(1, (int)Math.Ceiling(max * HEALTH_FLOOR));
            if (current <= floor) return current;
            int after = current - (int)Math.Round(max * HEALTH_LOSS);
            return Math.Max(floor, after);
        }

        /// <summary>Resting, as ACE's regeneration counts it: Crouch, Sitting or Sleeping as the forward command, out of combat.</summary>
        public static bool Resting(uint forwardCommand, bool combatStance) =>
            !combatStance && (forwardCommand == 0x41000012u || forwardCommand == 0x41000013u || forwardCommand == 0x41000014u);

        /// <summary>Is the hangover over: rested long enough, or it has run its longest.</summary>
        public static bool HangoverOver(double restedS, double sinceWakeS) => restedS >= REST_NEEDED_S || sinceWakeS >= HANGOVER_MAX_S;

        public const string HANGOVER_REST_LINE = "Your head is splitting. Sit or lie down a while to sleep it off.";
        public const string HANGOVER_HALF_LINE = "The pounding in your head is easing. A little more rest.";
        public const string HANGOVER_OVER_LINE = "You feel human again. The hangover is gone.";

        public static readonly string[] GROAN_LINES =
        {
            "Ugh. Your head.",
            "You hiccup. It hurts.",
            "Everything is too bright and too loud.",
            "You swear off ale forever. Again.",
            "Your stomach makes a noise like a dying Banderling.",
        };

        /// <summary>What players nearby see. {0} is the drinker's name.</summary>
        public static readonly string[] SWAY_OTHERS =
        {
            "{0} sways like a tree in a storm.",
            "{0} squints at the floor as if it moved.",
            "{0} waves both arms for balance and loses the argument.",
        };
        public static readonly string[] FALL_OTHERS =
        {
            "{0} topples over like a felled tree, out cold.",
            "{0} takes a few bold steps and lands flat on their face.",
            "{0} staggers, wobbles, and goes down like a sack of turnips.",
        };
        public const string WAKE_OTHERS = "{0} sits up, blinking, and holds their head.";
        public const string GROAN_OTHERS = "{0} groans and holds their head.";

        /// <summary>One line of a list for any pick (wraps; negatives too).</summary>
        public static string Pick(string[] lines, int pick) => lines[(pick % lines.Length + lines.Length) % lines.Length];
        public const uint HANGOVER_CURE = 23924;   // Ulgrim's Hangover Cure (retail): ours, it also clears ours
        public const uint DINER_COFFEE = 909705;   // the Pancake House's Diner Coffee: clears it the same way
        /// <summary>Ulgrim's cure or the diner's coffee: sobers you and lifts the hangover.</summary>
        public static bool IsCure(uint wcid) => wcid == HANGOVER_CURE || wcid == DINER_COFFEE;

        /// <summary>Every drinkable alcohol on the world, by class id, with its weight in drinks.</summary>
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

        /// <summary>The count now, from the count at at (seconds).</summary>
        public static double Now(double drinks, double at, double now) =>
            Math.Max(0.0, drinks - Math.Max(0.0, now - at) * DECAY_PER_S);

        public static double Add(double now, double weight) => Math.Min(CAP, Math.Max(0.0, now) + Math.Max(0.0, weight));

        public static bool PassesOut(double after) => after >= PASS;

        /// <summary>0 sober, 1 tipsy (the screen sways), 2 drunk (blur, double vision), 3 staggering.</summary>
        public static int Stage(double d) => d >= 6.0 ? 3 : d >= 4.0 ? 2 : d >= 2.0 ? 1 : 0;

        public enum Block { None, Fighting, Hostile, PlayerKiller, Stance }

        /// <summary>Is an ACE DamageHistory entry a hit by someone else?</summary>
        public static bool IsHit(int amount, bool attackerIsSelf, bool attackerInvalid) => amount < 0 && !attackerIsSelf && !attackerInvalid;

        /// <summary>Why a drink that would pass you out is refused.</summary>
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

        /// <summary>The line when the count climbs into a new stage.</summary>
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

        public const string HANGOVER_LINE = "Your head is splitting. You have a hangover.";   // woken early: only the short one
        public const string CURE_LINE = "Ulgrim's cure works. Your head clears and the world holds still.";
        public const string COFFEE_CURE_LINE = "The coffee is strong enough to stand a spoon in. Your head clears and the world holds still.";

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
