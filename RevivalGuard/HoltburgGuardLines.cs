using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// WHAT HOLTBURG'S ROYAL GUARDS SAY (HoltburgGuards.cs). Data only: edit a line here, rebuild the mod, nothing else
    /// changes. Turbine's lore voice (docs/outreach/VOICE.md): plain ASCII, no em dashes, nothing modern or jokey. Retail's
    /// one heartbeat line, "All's quiet on this front!", stays in the general pool. Pools are picked by
    /// HoltburgGuardsRule.PickPool (time of day by Dereth's hours, the season, and what has just happened); within a pool a
    /// guard says every line once before any line again (HoltburgGuardsRule.LineBag).
    /// </summary>
    public static class HoltburgGuardLines
    {
        public static readonly Dictionary<string, string[]> Pools = new Dictionary<string, string[]>
        {
            ["general"] = new[]
            {
                "All's quiet on this front!",
                "Keep to the road after dark, traveller. The drudges are bolder than they were.",
                "Mind your purse in the square. Not every face in Holtburg is a friendly one.",
                "The lifestone has been busy today. I hope they learned something.",
                "Another quiet watch. I will not complain of it.",
                "Holtburg stands. That is the whole of the job.",
                "If you see smoke on the horizon, send word to the nearest guard.",
                "The Crown pays us to watch, and watch we will.",
                "I have walked this street a thousand times. I have yet to tire of it.",
                "Anything with too many legs, report it to the guard.",
                "The mosswarts keep to the marshes, for now.",
                "Stay off the Arwic road at night unless you travel in company.",
                "There is no shame in waking at the lifestone. Only in not getting up again.",
                "Stand straight and keep your eyes open, and the town sleeps sound.",
            },
            ["time.dawn"] = new[]
            {
                "Morning comes. The night watch has earned its rest.",
                "First light, and the road is empty. A good start.",
                "Dawnsong already. The traders will be opening soon.",
                "Cold this early. The sun will see to that soon enough.",
                "The mist lifts off the fields. Nothing in it, this time.",
            },
            ["time.day"] = new[]
            {
                "Midday, and the square is full. Keep your wits about you.",
                "Warm work in this armor under the sun.",
                "The traders do well when the weather holds.",
                "A fine day for a patrol. Few enough of those.",
                "Busy road today. Half of Dereth must be passing through.",
            },
            ["time.dusk"] = new[]
            {
                "Evensong. Light the lamps and close the shutters.",
                "The light is going. Anything that stirs out there will stir soon.",
                "Dusk. I like it least of all the hours.",
                "Gloaming already. Keep a torch handy if you head out.",
            },
            ["time.night"] = new[]
            {
                "Night watch. Keep your voice down, the town is sleeping.",
                "Quiet tonight. Too quiet, some would say. I say quiet is good.",
                "Darktide. If you have business, be quick about it.",
                "The stars are out. Not much else is, I hope.",
                "I have heard wolves to the north. They keep their distance from the torches.",
                "Only fools and adventurers walk abroad at this hour. Which are you?",
            },
            ["season.spring"] = new[]
            {
                "Morningthaw again. The fields will want hands soon.",
                "Spring rains make for muddy boots and a muddy watch.",
            },
            ["season.summer"] = new[]
            {
                "Hot season. The drudges grow restless in the heat.",
                "Long days in summer. Long watches too.",
            },
            ["season.autumn"] = new[]
            {
                "Harvest is near. The farmers will want an eye kept on their stores.",
                "The leaves are turning, and the nights grow colder every one.",
            },
            ["season.winter"] = new[]
            {
                "Cold enough to freeze a guard to his post.",
                "Snow on the road. Watch your footing, traveller.",
            },
            ["event.after"] = new[]
            {
                "That was close to the houses. Too close.",
                "The town is safe again. For now.",
                "They came in, and they did not go out. That is how it should be.",
                "I will be cleaning this blade for a week.",
                "Keep your eyes open. Where there is one, there are often more.",
            },
            ["event.busy"] = new[]
            {
                "Plenty of folk about today. Easier to watch a crowd than an empty street.",
                "So many travellers. Holtburg has not been this lively in years.",
                "Make way there. Mind the guard.",
                "A full square. Good for the traders, hard on the guard.",
            },
            ["event.quiet"] = new[]
            {
                "Not a soul on the road this hour. I could hear a coin drop in the square.",
                "Quiet watch. I have counted every stone on this street.",
                "Nothing stirs. Good.",
            },

            // spoken to a player who uses a guard or greets one in local chat
            ["greet.dawn"] = new[]
            {
                "Good morning to you. Early on the road?",
                "Morning, traveller. The night was quiet.",
                "You are up with the sun. A good habit.",
            },
            ["greet.day"] = new[]
            {
                "Good day to you.",
                "Well met, traveller. All is well in Holtburg.",
                "Greetings. Keep to the roads and you will have no trouble.",
            },
            ["greet.dusk"] = new[]
            {
                "Good evening. Be indoors before full dark, if you can.",
                "Evening, friend. The light is going.",
                "Greetings. Mind the road at dusk.",
            },
            ["greet.night"] = new[]
            {
                "Late to be about, friend. Stay near the lamps.",
                "Hail. The town sleeps, so keep your voice low.",
                "Greetings. Night watch, and all is well.",
            },
            ["greet.fight"] = new[]
            {
                "Not now, citizen! Stand clear!",
                "Back, back! Let us do our work!",
                "Later, friend. There is a beast in the street!",
            },

            // duty
            ["duty.callout"] = new[]
            {
                "To arms! There is a beast in the town!",
                "Monster in the streets! Guards, to me!",
                "Hold there, creature! You go no further!",
                "Intruder! Protect the townsfolk!",
                "There! Cut it down before it reaches the houses!",
                "For Holtburg! At it!",
            },
            ["duty.answer"] = new[]
            {
                "I see it! Coming!",
                "With you!",
                "On my way!",
                "Hold it there, I am coming!",
            },
            ["duty.closing"] = new[]
            {
                "It is done. Back to your business, all of you.",
                "That one will trouble no one again.",
                "Down it goes. The town is safe.",
                "Well fought. Back to the watch.",
                "Clear. All is quiet again.",
            },
            ["duty.abandon"] = new[]
            {
                "It has fled the town. Let it go, we keep to our posts.",
                "Gone into the wilds. Good riddance.",
                "We do not leave the town unguarded. Back to your posts.",
            },
            ["duty.rescue"] = new[]
            {
                "Hold on, young one! I am coming!",
                "Stand fast, recruit! The guard is with you!",
            },
            ["duty.watch"] = new[]
            {
                "Your fight, young one. I will be watching.",
                "Go on, you have this one. Call if you need me.",
            },
            ["duty.toolong"] = new[]
            {
                "This one is beyond a town guard. Keep clear of it, citizens!",
                "Too tough for my blade. Someone fetch stronger arms than mine!",
            },
            ["duty.fallback"] = new[]
            {
                "I am hurt. Falling back!",
                "I need a moment. Hold the line without me!",
            },
        };
    }
}
