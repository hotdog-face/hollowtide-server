using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>The pure part of the Pancake House diner</summary>
    public static class DinerRule
    {
        // ---- the menu (tools/gpubox-ace/gen_diner.py makes these weenies; the prices are their values, Bo Lin sells at 1.0)
        public const uint SHORT_STACK = 909700, TALL_STACK = 909701, SPECIAL_PLATE = 909702, EGGS = 909703, PIE = 909704,
                          COFFEE = 909705, CHALLENGE = 909706, CONTEST_PANCAKE = 909707;
        public const uint GLADYS = 80092;     // retail: Gladys the Golem Waitress
        public const uint PYREAL = 273;
        public static readonly uint[] MEALS = { SHORT_STACK, TALL_STACK, SPECIAL_PLATE, EGGS, PIE };

        public static bool IsMeal(uint wcid) => Array.IndexOf(MEALS, wcid) >= 0;
        public static bool IsDinerFood(uint wcid) => IsMeal(wcid) || wcid == COFFEE || wcid == CHALLENGE || wcid == CONTEST_PANCAKE;

        public static string MealName(uint wcid) => wcid switch
        {
            SHORT_STACK => "Short Stack", TALL_STACK => "Tall Stack", SPECIAL_PLATE => "Shou-jen Special", EGGS => "Eggs and Toast",
            PIE => "Gladys's Apple Pie", COFFEE => "Diner Coffee", _ => "the special",
        };

        // ---- the hours ----
        public enum Hours { Night, Rush, Midday, Evening }

        public static Hours HoursOf(int derethHour)
        {
            if (derethHour >= 5 && derethHour <= 8) return Hours.Rush;
            if (derethHour >= 9 && derethHour <= 12) return Hours.Midday;
            if (derethHour >= 13 && derethHour <= 15) return Hours.Evening;
            return Hours.Night;
        }

        /// <summary>How many of the three regulars sit in their booths</summary>
        public static int RegularsIn(Hours h) => h switch { Hours.Rush => 3, Hours.Midday => 2, Hours.Evening => 1, _ => 0 };

        /// <summary>Seconds Gladys waits at her post between rounds of the tables: busy at the rush, slow at night.</summary>
        public static double PostWait(Hours h, double r) => h switch
        {
            Hours.Rush => 20 + 25 * r,
            Hours.Midday => 45 + 45 * r,
            Hours.Evening => 75 + 60 * r,
            _ => 180 + 120 * r,
        };

        /// <summary>Late at night the stools go up on the counter.</summary>
        public static bool StoolsUp(Hours h) => h == Hours.Night;

        // ---- the special of the day ----
        static readonly uint[] SPECIALS = { TALL_STACK, EGGS, SPECIAL_PLATE, PIE, SHORT_STACK };

        public static int DayNumber(DateTime localDate) => (int)(localDate.Date - new DateTime(2026, 1, 1)).TotalDays;

        /// <summary>Today's special: one of the meals, a different one each day, in a fixed round.</summary>
        public static uint Special(int day) => SPECIALS[((day % SPECIALS.Length) + SPECIALS.Length) % SPECIALS.Length];

        public static string SpecialLine(int day) => $"Today's special is the {MealName(Special(day))}. Eat it at a booth and Gladys brings you a coffee on the house.";

        // ---- well fed ----
        public const uint REGEN_I = 165, REJUV_I = 54;      // Regeneration Self I, Rejuvenation Self I (retail spells)
        public const double FED_S = 1800;                   // half an hour
        public const float AT_TABLE_M = 2.8f, AT_STOOL_M = 0.9f;   // a player at a booth's open end stands ~2.3 m from its middle
        public const string FED_LINE = "You feel well fed. Your health and stamina come back a little faster for half an hour.";
        public const string NOT_SEATED_LINE = "Eat it at a booth or the counter and you would feel well fed.";

        /// <summary>At a booth: within AT_TABLE_M of a booth table's middle, or AT_STOOL_M of a counter stool, and inside the hall.</summary>
        public static bool Seated(float x, float y, IList<(float x, float y)> tables, IList<(float x, float y)> stools, float hx0, float hy0, float hx1, float hy1)
        {
            if (x < hx0 || x > hx1 || y < hy0 || y > hy1) return false;
            foreach (var t in tables) if (Dist(x, y, t.x, t.y) <= AT_TABLE_M) return true;
            foreach (var s in stools) if (Dist(x, y, s.x, s.y) <= AT_STOOL_M) return true;
            return false;
        }

        static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        // ---- the Pancake Challenge ----
        public const double CONTEST_S = 60;
        public const int CONTEST_STACK = 40;        // more than anyone can eat in a minute
        public const string BEST_FLAG = "rg_diner_pancakes";   // a character's best (quest registry; LeaderboardRule reads it)
        public const string TITLE = "HungryHungryHippo";      // retail title "Hungry Hungry Hippo", for the day's champion

        public static string StartLine => $"The clock starts! You have one minute. {CONTEST_STACK} contest pancakes are in your pack.";
        public static string HalfLine => "Thirty seconds left!";
        public static string LastLine => "Ten seconds!";

        public static string ResultLine(int eaten, int best) =>
            eaten == 0 ? "Time! Not a single pancake. Gladys looks disappointed."
            : $"Time! You ate {eaten} pancake{(eaten == 1 ? "" : "s")}." + (eaten > best ? (best > 0 ? $" A new personal best (was {best})." : " Your first time on the board.") : $" Your best is {best}.");

        public static string ChampionLine(string name, int eaten) => $"{name} ate {eaten} pancakes in a minute: the best today! Hungry Hungry Hippo!";

        /// <summary>Does this result take the day's best?</summary>
        public static bool NewDayBest(int dayOfBest, int today, int best, int eaten) => eaten > 0 && (dayOfBest != today || eaten > best);

        // ---- tips for Gladys ----
        public const int BIG_TIP = 100;
        public const int MAX_TIP = 500;          // more than this is handed back (a whole stack dragged on her by mistake)
        public static int TipTaken(int offered) => Math.Clamp(offered, 0, MAX_TIP);
        public const string TOO_MUCH = "That's too much, hon. I'll take five hundred and not a pyreal more.";
        public const double REFILL_GAP_S = 600;     // one free refill per player per ten minutes

        public enum Tip { Small, Fair, Big }
        public static Tip TipOf(int amount) => amount >= BIG_TIP ? Tip.Big : amount >= 20 ? Tip.Fair : Tip.Small;

        public static string TipLine(Tip t, bool refill) => t switch
        {
            Tip.Big => refill ? "Well, aren't you a sweetheart! Here, hon, this refill is on me." : "Well, aren't you a sweetheart! Thank you, hon.",
            Tip.Fair => "Thank you kindly, hon. You come back soon.",
            _ => "Every little bit helps, hon. Thank you.",
        };

        // ---- Gladys and the way downstairs (retail: paying her sends a flagged player into the Shou-jen hideout) ----
        public const string FLAG = "IHOPflag", SPRING_EVENT = "IHOPopen";
        public const string KITCHEN_CLOSED = "Kitchen's closed to the public, hon. Order from Bo Lin at the counter, and if you're feeling generous, hand me a tip.";
        public const string KITCHEN_OFF_SEASON = "The back's locked up till spring, hon. Chow Doun opens it again when the Spring Festival comes. Can I top up your coffee?";

        /// <summary>Gladys's retail Use (pay 19 pyreals and go downstairs) runs only for a flagged player in the spring.</summary>
        public static bool DownstairsOpen(bool hasFlag, bool spring) => hasFlag && spring;

        // ---- staff lines (unprompted ones go through the diner's speech budget: TownLifeRule.Budget) ----
        public static readonly string[] HOSTESS_GREET =
        {
            "Welcome to the Tanada House of Pancakes! Sit anywhere that is free, hon.",
            "Grab a booth, hon. Bo Lin at the counter has the menu.",
            "Table for one? Take any booth you like.",
        };
        public static readonly string[] HOSTESS_RUSH = { "Morning! Grab a booth while there's one free. Bo Lin at the counter has the menu." };
        public static readonly string[] HOSTESS_NIGHT = { "We're quiet this late, but the coffee's still on. Sit anywhere." };
        public static readonly string[] GLADYS_TABLE = { "More coffee, hon?", "Everything all right over here?", "Can I get you anything else, hon?", "You want a warm-up on that?" };
        public static readonly string[] GLADYS_COUNTER = { "Two short stacks for the booth by the wall!", "I need a tall stack, Daichi!" };
        public static readonly string[] COOK_BELL = { "Order up!", "Order up! Pick it up while it's hot!" };
        public static readonly string[] CLERK_IDLE = { "Coffee's fresh!", "Pancake Challenge, anyone? One minute, all you can eat." };

        /// <summary>Two-voice exchanges: (who speaks, line).</summary>
        public static readonly (string Who, string Line)[][] EXCHANGES =
        {
            new[] { ("regular1", "Gladys, these eggs are cold."), ("gladys", "They were hot when they left the kitchen, hon.") },
            new[] { ("regular1", "Cold again. Every morning, cold eggs."), ("gladys", "And every morning you order them, sweetie.") },
            new[] { ("regular2", "Kenta, you are not eating twenty pancakes."), ("regular3", "Watch me, Auntie.") },
            new[] { ("regular1", "In my day the eggs were warm."), ("regular2", "In your day there were no eggs, Tsuyoshi.") },
        };
    }
}
