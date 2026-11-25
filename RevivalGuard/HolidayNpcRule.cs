using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// HOLIDAY TOWNSFOLK (owner 2026-11-24: "on the day of holidays can npcs in holtburg acknowledge and be aware of the
    /// holidays? azamir's lines change, etc"). Pure: which holiday a Pacific date is, by each holiday's real rule for its
    /// year, and the lines Holtburg's NPCs and Azimar say on it (docs/proposals/holiday-npcs.md, the owner reads them
    /// there). Glue in HolidayNpcs (switch rg_holiday_npcs, default off); nettest HolidayNpcCase checks the dates.
    /// Pool names: hol.KEY.town (ambient), hol.KEY.greet, hol.KEY.npc.NPC[.greet], hol.KEY.role.ROLE, hol.KEY.azimar.greet|idle.
    /// </summary>
    public static class HolidayNpcRule
    {
        public sealed class Holiday
        {
            public string Key, Name;
            public bool Eve;                                   // the day before a holiday, labelled as such
            public Func<int, DateTime> Date;                   // its date in a given year
        }

        // ---- date rules ----
        public static DateTime NthWeekday(int year, int month, DayOfWeek dow, int n)
        {
            var d = new DateTime(year, month, 1);
            int add = ((int)dow - (int)d.DayOfWeek + 7) % 7;
            return d.AddDays(add + 7 * (n - 1));
        }

        /// <summary>Western (Gregorian) Easter Sunday: the anonymous Gregorian algorithm (Meeus/Jones/Butcher).</summary>
        public static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateTime(y, month, day);
        }

        /// <summary>US Pacific local time from UTC: PST (UTC-8), PDT (UTC-7) from the second Sunday of March 2:00 local to the
        /// first Sunday of November 2:00 local (US rule since 2007). Ours, so the date never hangs on the host's tz data.</summary>
        public static DateTime PacificFromUtc(DateTime utc)
        {
            int y = utc.Year;
            var dstStartUtc = NthWeekday(y, 3, DayOfWeek.Sunday, 2).AddHours(2 + 8);   // 2:00 PST = 10:00 UTC
            var dstEndUtc = NthWeekday(y, 11, DayOfWeek.Sunday, 1).AddHours(2 + 7);    // 2:00 PDT = 09:00 UTC
            bool dst = utc >= dstStartUtc && utc < dstEndUtc;
            return utc.AddHours(dst ? -7 : -8);
        }

        public static readonly Holiday[] Holidays =
        {
            new Holiday { Key = "newyearseve",  Name = "New Year's Eve", Eve = true, Date = y => new DateTime(y, 12, 31) },
            new Holiday { Key = "newyearsday",  Name = "New Year's Day",             Date = y => new DateTime(y, 1, 1) },
            new Holiday { Key = "valentines",   Name = "Valentine's Day",            Date = y => new DateTime(y, 2, 14) },
            new Holiday { Key = "aprilfools",   Name = "April Fools' Day",           Date = y => new DateTime(y, 4, 1) },
            new Holiday { Key = "easter",       Name = "Easter (Spring Festival)",   Date = Easter },
            new Holiday { Key = "mothersday",   Name = "Mother's Day",               Date = y => NthWeekday(y, 5, DayOfWeek.Sunday, 2) },
            new Holiday { Key = "fathersday",   Name = "Father's Day",               Date = y => NthWeekday(y, 6, DayOfWeek.Sunday, 3) },
            new Holiday { Key = "julyfourth",   Name = "Independence Day",           Date = y => new DateTime(y, 7, 4) },
            new Holiday { Key = "halloween",    Name = "Halloween (Festival Season)", Date = y => new DateTime(y, 10, 31) },
            new Holiday { Key = "thanksgiving", Name = "Thanksgiving",               Date = y => NthWeekday(y, 11, DayOfWeek.Thursday, 4) },
            new Holiday { Key = "christmaseve", Name = "Christmas Eve (Frostfell)", Eve = true, Date = y => new DateTime(y, 12, 24) },
            new Holiday { Key = "christmas",    Name = "Christmas Day (Frostfell)",  Date = y => new DateTime(y, 12, 25) },
        };

        /// <summary>The holiday on this Pacific calendar date, or null. Only the day itself (or a labelled eve).</summary>
        public static Holiday On(DateTime pacificDate)
        {
            var d = pacificDate.Date;
            foreach (var h in Holidays) if (h.Date(d.Year).Date == d) return h;
            return null;
        }

        /// <summary>The next holidays from this Pacific date (inclusive), for the status command.</summary>
        public static IEnumerable<(Holiday h, DateTime at)> Upcoming(DateTime pacificDate, int count)
        {
            var d = pacificDate.Date;
            return Holidays.SelectMany(h => new[] { (h, h.Date(d.Year)), (h, h.Date(d.Year + 1)) })
                .Where(x => x.Item2 >= d).OrderBy(x => x.Item2).Take(count);
        }

        // ---- which pools, in the order tried (the caller falls through to the usual pools) ----
        /// <summary>Ambient line pools for an NPC on a holiday.</summary>
        public static List<string> AmbientPools(string holiday, string npc, string role) =>
            new List<string> { $"hol.{holiday}.npc.{npc}", $"hol.{holiday}.role.{role}", $"hol.{holiday}.town" };

        /// <summary>Greeting pools (an arrival greeting or an answer to a hello).</summary>
        public static List<string> GreetPools(string holiday, string npc) =>
            new List<string> { $"hol.{holiday}.npc.{npc}.greet", $"hol.{holiday}.greet" };

        /// <summary>Share of lines that are the holiday's on its day: ambient, greetings, Azimar.</summary>
        public const double AMBIENT_SHARE = 0.4, GREET_SHARE = 0.6, AZIMAR_SHARE = 0.6;

        // ---- the lines (docs/proposals/holiday-npcs.md mirrors these; plain, no em dashes) ----
        public static readonly Dictionary<string, string[]> Lines = new Dictionary<string, string[]>
        {
            // ======== THANKSGIVING (fourth Thursday of November): a harvest feast, Farmer Ar'Oyd's Fowl Play is on ========
            ["hol.thanksgiving.town"] = new[]
            {
                "A day for giving thanks. The harvest is in and the town is still standing. That will do.",
                "Smell that? Somebody in Holtburg is roasting a bird the size of a drudge.",
                "Thanks to the Royal Guard, to the healers, and to whoever keeps the olthoi out of the wheat.",
                "Feast day today. Eat well, friend. Tomorrow the monsters will still be there.",
                "They say Farmer Ar'Oyd near Nanto lost his prize tom. I would not want to meet that bird.",
            },
            ["hol.thanksgiving.greet"] = new[]
            {
                "Good feast day to you, {name}!",
                "Welcome, {name}. You picked a fine day to come home. There is food enough for all.",
                "Ah, {name}. Give thanks you made it back in one piece. Then go find something to eat.",
                "A day of thanks, {name}. Sit a while if you can.",
            },
            ["hol.thanksgiving.role.patron"] = new[]
            {
                "Pass the potatoes. No, the other potatoes.",
                "I am thankful for this chair, and I am not getting out of it.",
                "Third helping. Do not tell the Royal Guard.",
            },
            ["hol.thanksgiving.role.shopkeeper"] = new[]
            {
                "Short hours today, it is a feast day. Buy what you need and go eat.",
                "I give thanks for good customers. You are one of them, I hope.",
            },
            ["hol.thanksgiving.role.crafter"] = new[]
            {
                "The forge can rest one day a year. Today is that day. Mostly.",
                "I am thankful for steady hands and sharp tools.",
            },
            ["hol.thanksgiving.role.arcanum"] = new[]
            {
                "Even the Arcanum stops for the feast. The scrolls will keep.",
                "I give thanks to the Empyreans for the lore they left us. And for pie.",
            },
            ["hol.thanksgiving.role.guardsman"] = new[]
            {
                "Feast day or not, someone has to watch the road.",
                "Save me a plate. I am on watch until dark.",
            },
            ["hol.thanksgiving.npc.wilomine"] = new[]
            {
                "Feast day! Mind the cider, it is stronger than it looks.",
                "Every table is full tonight. I have never been so thankful to be busy.",
            },
            ["hol.thanksgiving.npc.wilomine.greet"] = new[]
            {
                "Come in, {name}, come in! There is a seat by the fire and a plate with your name on it.",
            },
            ["hol.thanksgiving.npc.buckminster"] = new[]
            {
                "Mind your elbows, hot plates coming through!",
                "Roast bird, roast roots, and a pie I may have sampled. Only a little.",
            },
            ["hol.thanksgiving.npc.fispur"] = new[]
            {
                "Sold out of pumpkins, sold out of apples, nearly sold out of patience. Happy feast day!",
                "Fresh bread for the feast table. Get it while it is warm.",
            },
            ["hol.thanksgiving.npc.rand"] = new[]
            {
                "Best hunting day of the year. Every family in Holtburg wants a bird.",
                "I went for a gobbler near Nanto and something much bigger looked back at me.",
            },
            ["hol.thanksgiving.npc.herald"] = new[]
            {
                "Hear ye! Today Holtburg gives thanks for the harvest, for the Guard, and for every adventurer who came home.",
            },
            ["hol.thanksgiving.npc.alcott"] = new[]
            {
                "I give thanks for every traveller who walks through that gate. Even the noisy ones.",
            },
            ["hol.thanksgiving.azimar.greet"] = new[]
            {
                "A day of thanks, {name}. I am thankful you came. Hold still, and let me give you something to be thankful for.",
                "Feast day, {name}. Eat first if you like, the magic will keep. Ah, you are here now. Very well.",
                "Welcome, {name}. The harvest is in, the fires are lit, and my blessing is yours.",
                "Good feast day, {name}. I have given thanks to the Light. Now I give its gifts to you.",
                "{name}! You look like you could use a good stuffing. Hold still, I meant the blessing.",
                "Happy feast day, {name}. I am thankful for many things. The way you fill out that armor is near the top of the list.",
            },
            ["hol.thanksgiving.azimar.idle"] = new[]
            {
                "I could smell the roast from across the square. Even a mage must eat.",
                "Give thanks today, travellers. For the harvest, for your friends, for every fight you walked away from.",
                "No hunting until you have eaten, that is my rule today. Well, it is a suggestion.",
                "Everyone wants the breast or the thigh. Nobody ever asks about the old mage's drumstick.",
                "I ate so much I had to loosen my robe. Do not get excited, it was the belt.",
            },

            // ======== CHRISTMAS EVE and CHRISTMAS (Dec 24 eve, Dec 25): Frostfell, retail's winter festival ========
            ["hol.christmaseve.town"] = new[]
            {
                "Frostfell Eve. Hang a sock by the fire, they say the Gift Gophers are about tonight.",
                "Snow on the roofs and lamps in every window. Holtburg looks its best on Frostfell Eve.",
                "Last night before the great Frostfell feast. Get your gifts wrapped, friend.",
            },
            ["hol.christmaseve.greet"] = new[]
            {
                "Happy Frostfell Eve, {name}!",
                "Come in from the cold, {name}. It is Frostfell Eve.",
            },
            ["hol.christmaseve.azimar.greet"] = new[]
            {
                "Frostfell Eve, {name}. Here is a gift a day early.",
                "The night before Frostfell, {name}. Stand close, the cold bites tonight.",
                "{name}, I have been very naughty this year, and I regret nothing. Here, a blessing to balance the books.",
            },
            ["hol.christmaseve.azimar.idle"] = new[]
            {
                "Frostfell Eve. Even the wind sounds kinder tonight.",
                "They hang the mistletoe in the tavern. I hang it on my hat. Results vary.",
            },
            ["hol.christmas.town"] = new[]
            {
                "Merry Frostfell! May your hearth be warm and your pack be full.",
                "Did you find a gift in the snow this morning? The Gift Gophers were busy last night.",
                "Frostfell Day. No one should fight alone today. Find your friends.",
                "Peace in Holtburg today. Even the drudges seem quiet.",
            },
            ["hol.christmas.greet"] = new[]
            {
                "Merry Frostfell, {name}!",
                "Merry Frostfell, {name}. Warm yourself by the fire a while.",
                "A happy Frostfell to you, {name}. Glad you are home for it.",
            },
            ["hol.christmas.npc.wilomine"] = new[]
            {
                "Hot spiced cider, on the house for Frostfell. Just the one, mind.",
            },
            ["hol.christmas.azimar.greet"] = new[]
            {
                "Merry Frostfell, {name}. My gift to you is the same as always, but given with extra warmth.",
                "Merry Frostfell, {name}. Hold still, this one is wrapped in light.",
                "Frostfell Day, {name}. Of all the gifts I give, I like this day's best.",
                "Merry Frostfell, {name}. Sit on my knee and tell me what you want. No? A blessing, then.",
            },
            ["hol.christmas.azimar.idle"] = new[]
            {
                "Merry Frostfell, all of you. Come and be blessed, it is my gift.",
                "Snow and silence. Frostfell is the one day Dereth seems to rest.",
                "My gift to the ladies of Holtburg this year is me. Returns are not accepted.",
            },

            // ======== NEW YEAR'S EVE (Dec 31, eve) and NEW YEAR'S DAY (Jan 1): retail's New Year fireworks ========
            ["hol.newyearseve.town"] = new[]
            {
                "Last night of the year. Watch the sky at midnight.",
                "One more day and the year is done. Whatever it brought, we are still here.",
                "Tonight we see the old year out. Tomorrow, fireworks.",
            },
            ["hol.newyearseve.greet"] = new[]
            {
                "Last day of the year, {name}. Make it a good one.",
                "Come to see the old year out, {name}?",
            },
            ["hol.newyearseve.azimar.greet"] = new[]
            {
                "The year ends tonight, {name}. Let it end with you well blessed.",
                "{name}, at midnight someone kisses someone. I am only saying, I will be standing right here.",
            },
            ["hol.newyearseve.azimar.idle"] = new[]
            {
                "The old year is nearly spent. I wonder what the next one will ask of us.",
                "My resolution for next year is the same as every year: more wine, less robe.",
            },
            ["hol.newyearsday.town"] = new[]
            {
                "A new year in Dereth! Did you see the fireworks?",
                "New year, same Holtburg. I would not have it any other way.",
                "I made a promise for the new year. I will not say what. I have already broken it.",
            },
            ["hol.newyearsday.greet"] = new[]
            {
                "Happy New Year, {name}!",
                "A good new year to you, {name}. May it bring you good loot and few deaths.",
            },
            ["hol.newyearsday.azimar.greet"] = new[]
            {
                "Happy New Year, {name}. The first blessing of the year is yours.",
                "A new year, {name}. Let us start it properly.",
                "Happy new year, {name}. Do not ask me what happened last night. The fountain knows.",
            },
            ["hol.newyearsday.azimar.idle"] = new[]
            {
                "A new year. New roads, new foes, and the same old Light.",
            },

            // ======== VALENTINE'S DAY (Feb 14) ========
            ["hol.valentines.town"] = new[]
            {
                "A day for sweethearts. Somebody left roses on the well again.",
                "Half of Holtburg is writing love letters and the other half is pretending not to.",
                "Bring your sweetheart to the tavern tonight. Wilomine is pouring something pink.",
            },
            ["hol.valentines.greet"] = new[]
            {
                "Happy Valentine's Day, {name}!",
                "Out looking for your sweetheart, {name}?",
            },
            ["hol.valentines.azimar.greet"] = new[]
            {
                "Happy Valentine's Day, {name}. A blessing for you, and one for whoever holds your heart.",
                "Valentine's Day, {name}. Even an old mage remembers what it was to be young.",
                "{name}, are you a mana stone? Because every time I look at you, something in me charges.",
                "Roses are red, mana is blue, I have one more blessing, and it is for you. The rest is negotiable.",
            },
            ["hol.valentines.azimar.idle"] = new[]
            {
                "Love is the oldest magic in Dereth. Older than mine, anyway.",
                "Love is in the air. That, and whatever the drudges have been eating.",
                "I am single, I am powerful, and I own a very large staff. Form an orderly line.",
            },

            // ======== APRIL FOOLS' DAY (Apr 1): the shard's own April Fools festival ========
            ["hol.aprilfools.town"] = new[]
            {
                "Someone told me the drudges have taken Yaraq. I am not falling for it today.",
                "Check your pack before you leave. Someone swapped my sword for a fish.",
                "Do not believe a word anyone says today. Including this.",
            },
            ["hol.aprilfools.greet"] = new[]
            {
                "Your boots are untied, {name}. Ha! Got you.",
                "Happy April Fools' Day, {name}. Watch your back.",
            },
            ["hol.aprilfools.azimar.greet"] = new[]
            {
                "Welcome, {name}. Today I will turn you into a toad. No. Only joking. Hold still.",
            },
            ["hol.aprilfools.azimar.idle"] = new[]
            {
                "Someone has enchanted my robe to squeak. I will find them.",
                "Today I swapped the tavern's ale for water. Nobody noticed. That says a lot about the tavern.",
            },

            // ======== EASTER (Western Easter Sunday, computed): retail's Spring Festival, eggs and bunnies ========
            ["hol.easter.town"] = new[]
            {
                "The children have been hunting painted eggs since dawn. Mind where you step.",
                "Spring is here for good. You can smell it in the fields.",
                "I found an egg in my boot this morning. A painted one, thank the Light.",
            },
            ["hol.easter.greet"] = new[]
            {
                "Happy spring festival, {name}! Found any eggs yet?",
                "A bright spring morning to you, {name}.",
            },
            ["hol.easter.azimar.greet"] = new[]
            {
                "A fine spring day, {name}. Everything is new again. Let your strength be new too.",
                "Happy spring, {name}. If you find a painted egg in your pack after this, it was not me.",
                "Happy spring, {name}. I have hidden eggs all over town. Some of them are mine. Do not ask.",
            },
            ["hol.easter.azimar.idle"] = new[]
            {
                "The fields are green again. Spring is the one magic I never had to learn.",
            },

            // ======== MOTHER'S DAY (second Sunday of May) ========
            ["hol.mothersday.town"] = new[]
            {
                "A day for mothers. Go and see yours, if you can.",
                "My mother would have scolded me for the state of this street. I miss her scolding.",
            },
            ["hol.mothersday.greet"] = new[]
            {
                "Have you thanked your mother today, {name}?",
            },
            ["hol.mothersday.azimar.greet"] = new[]
            {
                "A day for mothers, {name}. Bless them all. And you, now.",
            },
            ["hol.mothersday.azimar.idle"] = new[]
            {
                "Every hero in Dereth was carried home by a mother once.",
                "Call your mother today, travellers. Mine still asks when I will settle down. I tell her I have a staff and a fountain, what more does a man need.",
            },

            // ======== FATHER'S DAY (third Sunday of June) ========
            ["hol.fathersday.town"] = new[]
            {
                "A day for fathers. Mine taught me to swing a sword. Badly, but he tried.",
                "Buy your father a drink today. He has earned it.",
            },
            ["hol.fathersday.greet"] = new[]
            {
                "A day for fathers, {name}. Raise a glass to yours.",
            },
            ["hol.fathersday.azimar.greet"] = new[]
            {
                "A day for fathers, {name}. Stand tall, as they taught you.",
            },
            ["hol.fathersday.azimar.idle"] = new[]
            {
                "My father had no magic at all. He was still the strongest man I ever knew.",
                "Happy Father's Day to every father in Dereth. And to the ones who do not know it yet, Holtburg has a long memory.",
            },

            // ======== INDEPENDENCE DAY (Jul 4): midsummer fireworks ========
            ["hol.julyfourth.town"] = new[]
            {
                "Fireworks over Holtburg tonight, they say. Keep the drudges away, if nothing else.",
                "Hottest day of the year and somebody wants to light fires in the sky.",
            },
            ["hol.julyfourth.greet"] = new[]
            {
                "Staying for the fireworks tonight, {name}?",
            },
            ["hol.julyfourth.azimar.greet"] = new[]
            {
                "Fireworks tonight, {name}. Mine are quieter, but they last longer.",
            },
            ["hol.julyfourth.azimar.idle"] = new[]
            {
                "Fire in the sky for fun. In my day we used it for drudges.",
                "Fireworks tonight! I set off a few of my own every year. The healers stopped asking.",
            },

            // ======== HALLOWEEN (Oct 31): the last night of retail's Festival Season ========
            ["hol.halloween.town"] = new[]
            {
                "Last night of Festival Season. Lock your door, the dead walk tonight.",
                "Carved my pumpkin with a face like a drudge. Kept the real ones away, I hope.",
                "Mind the scarecrows tonight. One of them moved. I am sure of it.",
            },
            ["hol.halloween.greet"] = new[]
            {
                "Boo! Ha, got you, {name}. Happy Festival night.",
                "Out after dark on the last night of Festival Season, {name}? Brave.",
            },
            ["hol.halloween.azimar.greet"] = new[]
            {
                "The last night of Festival Season, {name}. Stand close, I will keep the spirits off you.",
                "A dark night for it, {name}. Let me give you a little light.",
                "{name}, a costume? I thought that was just your face. Kidding. Mostly. Here, a blessing.",
            },
            ["hol.halloween.azimar.idle"] = new[]
            {
                "The veil is thin tonight. I can hear the dead whispering in the fields.",
                "Trick or treat? I am an old mage, I know some very good tricks. You will have to bring your own treat.",
            },
        };
    }
}
