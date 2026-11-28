using System;
using System.Collections.Generic;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Seasonal drops are seasonal.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.CreateListSelect), new Type[] { typeof(List<PropertiesCreateList>) })]
    static class SeasonalDrops
    {
        static readonly Dictionary<uint, string[]> Held = new Dictionary<uint, string[]>
        {
            { 36361, new[] { "festivalseason" } },   // Undead Sailor's Head
            { 70324, new[] { "festivalseason" } },   // Undead Captain's Head
            { 70279, new[] { "festivalseason" } },   // Ruschk Head
            { 70280, new[] { "festivalseason" } },   // Snowman Head
            { 32185, new[] { "festivalseason" } },   // Two Headed Snowman Head
            { 70278, new[] { "festivalseason" } },   // Uber Penguin Head
            { 70275, new[] { "festivalseason" } },   // Penguin Head
            { 32181, new[] { "festivalseason" } },   // Ghostly Shroud
            { 25741, new[] { "festivalseason" } },   // Knath Husk
            { 32170, new[] { "festivalseason" } },   // Ursuin Arm
            { 32171, new[] { "festivalseason" } },   // Ursuin Legs
            { 32174, new[] { "festivalseason" } },   // Ursuin Torso
            { 28871, new[] { "festivalseason" } },   // Armored Skeletal Arm 
            { 28874, new[] { "festivalseason" } },   // Armored Skeletal Legs
            { 28892, new[] { "festivalseason" } },   // Armored Skeletal Torso
            { 28872, new[] { "festivalseason" } },   // Armored Undead Arm 
            { 28875, new[] { "festivalseason" } },   // Armored Undead Legs
            { 28893, new[] { "festivalseason" } },   // Armored Undead Torso
            { 8144, new[] { "festivalseason" } },   // Banderling Head
            { 28886, new[] { "festivalseason" } },   // Burun Guruk Head
            { 28888, new[] { "festivalseason" } },   // Chittick Head
            { 12206, new[] { "festivalseason" } },   // Doll Mask
            { 8145, new[] { "festivalseason" } },   // Drudge Head
            { 25557, new[] { "festivalseason" } },   // Eye Patch
            { 34028, new[] { "festivalseason" } },   // Falatacot Abbess Head
            { 32179, new[] { "festivalseason" } },   // Fiun Head
            { 44864, new[] { "festivalseason" } },   // Gurog Arm
            { 44870, new[] { "festivalseason" } },   // Gurog Leg
            { 44868, new[] { "festivalseason" } },   // Gurog Torso with a Head
            { 25559, new[] { "festivalseason" } },   // Hollow Minion's Face
            { 25560, new[] { "festivalseason" } },   // Knath Husk
            { 25737, new[] { "festivalseason" } },   // Knath Husk
            { 25738, new[] { "festivalseason" } },   // Knath Husk
            { 25739, new[] { "festivalseason" } },   // Knath Husk
            { 25740, new[] { "festivalseason" } },   // Knath Husk
            { 25742, new[] { "festivalseason" } },   // Knath Husk
            { 25743, new[] { "festivalseason" } },   // Knath Husk
            { 25744, new[] { "festivalseason" } },   // Knath Husk
            { 28866, new[] { "festivalseason" } },   // Left Peg Leg
            { 28889, new[] { "festivalseason" } },   // Mite Head
            { 25561, new[] { "festivalseason" } },   // Moarsman Head
            { 8146, new[] { "festivalseason" } },   // Mosswart Head
            { 22025, new[] { "festivalseason" } },   // Mu-miyah Arm
            { 22029, new[] { "festivalseason" } },   // Mu-miyah Leg
            { 22045, new[] { "festivalseason" } },   // Mu-miyah Torso
            { 22060, new[] { "festivalseason" } },   // Mu-miyah Torso with a Head
            { 36362, new[] { "festivalseason" } },   // Mukkir Head
            { 28861, new[] { "festivalseason" } },   // Pirate Hook
            { 8232, new[] { "festivalseason" } },   // Pumpkin
            { 12215, new[] { "festivalseason" } },   // Pumpkin Head
            { 28868, new[] { "festivalseason" } },   // Right Peg Leg
            { 28873, new[] { "festivalseason" } },   // Scarecrow Arm 
            { 28876, new[] { "festivalseason" } },   // Scarecrow Legs
            { 28898, new[] { "festivalseason" } },   // Scarecrow Torso
            { 22026, new[] { "festivalseason" } },   // Sclavus Arm
            { 12216, new[] { "festivalseason" } },   // Sclavus Head
            { 22030, new[] { "festivalseason" } },   // Sclavus Leg
            { 22046, new[] { "festivalseason" } },   // Sclavus Torso
            { 34029, new[] { "festivalseason" } },   // Shadow Head
            { 22027, new[] { "festivalseason" } },   // Skeletal Arm
            { 22031, new[] { "festivalseason" } },   // Skeletal Leg
            { 22047, new[] { "festivalseason" } },   // Skeletal Torso
            { 8147, new[] { "festivalseason" } },   // Tusker Head
            { 22028, new[] { "festivalseason" } },   // Undead Arm
            { 22032, new[] { "festivalseason" } },   // Undead Leg
            { 22048, new[] { "festivalseason" } },   // Undead Torso
            { 12225, new[] { "festivalseason" } },   // Zombie Head
            { 36359, new[] { "festivalseason", "aprilfools" } },   // Cow Head
            { 12219, new[] { "festivalseason", "spring" } },   // Ursuin Head
            { 34071, new[] { "festivalseason" } },   // Decaying Zombie Brain Portion
            { 36401, new[] { "festivalseason" } },   // Enchanted Bone Fragment
            { 36495, new[] { "festivalseason" } },   // Ancient Crest
            { 36489, new[] { "festivalseason" } },   // Chilling Ebony Staff
            { 901700, new[] { "festivalseason" } },   // Necklace of the Night Swarm
            { 34386, new[] { "frostfell" } },   // Hot Coal
            { 14765, new[] { "frostfell" } },   // Eggnog
            { 52580, new[] { "frostfell" } },   // Holiday Sweater (the imposter Santa)
            { 47168, new[] { "frostfell" } },   // Snow Tuskie Kennel (the imposter Santa)
            { 80078, new[] { "spring" } },   // Pon Mi's confession
            { 27249, new[] { "spring" } },   // Spring Cleaner Title Token
            // Retired live events (docs/EVENTS-RETIRED.md; tools/gpubox-ace/gen_retired_feb2.py).
            { 5878, new[] { "greatwork" } },     // Tremblant's Ivory Staff (Sir Joffre Tremblant, 1 in 10)
            { 5904, new[] { "greatwork" } },     // Crystal Device (The Great Work)
            { 903733, new[] { "burritual" } },   // Illuminated Falatacot Scroll (Matriarch Mirta'kir)
        };

        internal static int Count => Held.Count;

        /// <summary>Called from Mod.Initialize: a typo in a key would hold an item back forever.</summary>
        internal static void Validate()
        {
            foreach (var kv in Held)
                foreach (var key in kv.Value)
                    if (!SeasonalCalendar.IsFestivalKey(key))
                        Mod.Log.Warn($"[SeasonalDrops] wcid {kv.Key}: no festival '{key}'");
            Mod.Log.Info($"[SeasonalDrops] {Held.Count} festival items drop only in their season");
        }

        static bool InSeason(string[] keys)
        {
            foreach (var key in keys)
                if (SeasonalCalendar.IsFestivalActive(key)) return true;
            return false;
        }

        static void Postfix(List<PropertiesCreateList> __result)
        {
            if (__result == null || __result.Count == 0) return;
            try
            {
                __result.RemoveAll(c => c != null && Held.TryGetValue(c.WeenieClassId, out var keys) && !InSeason(keys));
            }
            catch (Exception e) { Mod.Log.Warn($"[SeasonalDrops] {e.Message}"); }
        }
    }
}
