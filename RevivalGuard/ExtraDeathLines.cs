using System;
using System.Collections.Generic;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using ACE.Common;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// OWNER-APPROVED EXTRA DEATH LINES (2026-10-20, "i like all the new death lines. add them all.";
    /// docs/DIVERGENCES.md). Appended to ACE's retail lists in Strings at mod load, never replacing them.
    /// {0} = victim, {1} = killer, as in Strings.cs.
    ///
    /// Strings.GetDeathMessage picks ThreadSafeRandom.Next(0, Count - 1) (inclusive), so appended lines
    /// mix in by themselves, Critical included. PKCritical does not: Creature.GetDeathMessage always takes
    /// Strings.PKCritical[0], so the prefix below handles only that case (a player killed by a player's
    /// critical) with a random pick, and leaves every other case to ACE.
    /// </summary>
    public static class ExtraDeathLines
    {
        static bool added;

        static DeathMessage M(string k, string v, string b) => new DeathMessage(k, v, b);

        public static void Register()
        {
            try
            {
                if (added) return;
                added = true;
                Add(Strings.Critical,
                    M("You hit {0} so hard their lifestone files a complaint!",
                      "{1} hits you so hard your lifestone files a complaint!",
                      "{1} hits {0} so hard their lifestone files a complaint!"),
                    M("You knock {0} into last Morningthaw, and they have to live the whole year over again!",
                      "{1} knocks you into last Morningthaw, and you have to live the whole year over again!",
                      "{1} knocks {0} into last Morningthaw, and they have to live the whole year over again!"),
                    M("{0} dies so fast their corpse beats them to the lifestone!",
                      "You die so fast from {1}'s attack that your corpse beats you to the lifestone!",
                      "{0} dies so fast from {1}'s attack that their corpse beats them to the lifestone!"),
                    M("You kill {0} so thoroughly that the Olthoi refuse to eat the remains!",
                      "{1} kills you so thoroughly that the Olthoi refuse to eat the remains!",
                      "{1} kills {0} so thoroughly that the Olthoi refuse to eat the remains!"),
                    M("The force of your attack is felt by {0}'s ancestors, their descendants, and a very confused Drudge nearby!",
                      "The force of {1}'s attack is felt by your ancestors, your descendants, and a very confused Drudge nearby!",
                      "The force of {1}'s attack is felt by {0}'s ancestors, their descendants, and a very confused Drudge nearby!"),
                    M("You strike {0} so hard that Asheron himself winces!",
                      "{1} strikes you so hard that Asheron himself winces!",
                      "{1} strikes {0} so hard that Asheron himself winces!"));
                Add(Strings.Slashing,
                    M("You slice {0} into enough pieces to stock a vendor!",
                      "{1} slices you into enough pieces to stock a vendor!",
                      "{1} slices {0} into enough pieces to stock a vendor!"));
                Add(Strings.Piercing,
                    M("You poke so many holes in {0} that the wind whistles a tune through them!",
                      "{1} pokes so many holes in you that the wind whistles a tune through them!",
                      "{1} pokes so many holes in {0} that the wind whistles a tune through them!"));
                Add(Strings.Bludgeoning,
                    M("You flatten {0} so completely they could be slid under a door!",
                      "{1} flattens you so completely you could be slid under a door!",
                      "{1} flattens {0} so completely they could be slid under a door!"));
                Add(Strings.Fire,
                    M("You cook {0} to a lovely golden brown!",
                      "{1} cooks you to a lovely golden brown!",
                      "{1} cooks {0} to a lovely golden brown!"));
                Add(Strings.Ice,
                    M("You freeze {0} so solid a Gromnie could use them as a sled!",
                      "{1} freezes you so solid a Gromnie could use you as a sled!",
                      "{1} freezes {0} so solid a Gromnie could use them as a sled!"));
                Add(Strings.Acid,
                    M("{0} dissolves into a puddle that even Mosswarts find disgusting!",
                      "{1}'s attack dissolves you into a puddle that even Mosswarts find disgusting!",
                      "{1}'s attack dissolves {0} into a puddle that even Mosswarts find disgusting!"));
                Add(Strings.Lightning,
                    M("You shock {0} so badly their hair will never lie flat again, which is the least of their problems!",
                      "{1} shocks you so badly your hair will never lie flat again, which is the least of your problems!",
                      "{1} shocks {0} so badly their hair will never lie flat again, which is the least of their problems!"));
                Add(Strings.Void,
                    M("You drain {0} so dry that even their shadow packs up and leaves!",
                      "{1} drains you so dry that even your shadow packs up and leaves!",
                      "{1} drains {0} so dry that even their shadow packs up and leaves!"));
                Add(Strings.PKCritical,
                    M("You defeat {0} so badly their allegiance pretends not to know them!",
                      "{1} defeats you so badly your allegiance pretends not to know you!",
                      "{1} defeats {0} so badly their allegiance pretends not to know them!"),
                    M("{0} is slain so hard their patron feels the experience loss!",
                      "You are slain by {1} so hard your patron feels the experience loss!",
                      "{0} is slain by {1} so hard their patron feels the experience loss!"));
                // Wildcards, owner 2026-10-20: "all the new death lines are approved. please add them."
                var wild = new[] {
                    M("You hit {0} so hard they soil their armor on the way down!",
                      "{1} hits you so hard you soil your armor on the way down!",
                      "{1} hits {0} so hard they soil their armor on the way down!"),
                    M("{0} dies, and their last act in Dereth is a long, sad fart!",
                      "You die at the hands of {1}, and your last act in Dereth is a long, sad fart!",
                      "{0} dies at the hands of {1}, and their last act in Dereth is a long, sad fart!"),
                    M("You hit {0} so hard their lunch leaves before they do!",
                      "{1} hits you so hard your lunch leaves before you do!",
                      "{1} hits {0} so hard their lunch leaves before they do!"),
                    M("{0}'s corpse makes a noise that will haunt this landblock forever!",
                      "{1} kills you, and your corpse makes a noise that will haunt this landblock forever!",
                      "{1} kills {0}, and their corpse makes a noise that will haunt this landblock forever!"),
                    M("You beat {0} so badly their mother logs in to apologize!",
                      "{1} beats you so badly your mother logs in to apologize!",
                      "{1} beats {0} so badly their mother logs in to apologize!"),
                    M("{0} explodes like a Mosswart after bean night!",
                      "{1} makes you explode like a Mosswart after bean night!",
                      "{1} makes {0} explode like a Mosswart after bean night!"),
                    M("You knock the stink right out of {0}, and it was a LOT of stink!",
                      "{1} knocks the stink right out of you, and it was a LOT of stink!",
                      "{1} knocks the stink right out of {0}, and it was a LOT of stink!"),
                    M("You beat {0} so hard they'll be picking your boot out of their backside at the lifestone!",
                      "{1} beats you so hard you'll be picking their boot out of your backside at the lifestone!",
                      "{1} beats {0} so hard they'll be picking {1}'s boot out of their backside at the lifestone!"),
                    M("{0} falls over, and something wet comes out of a place that should stay dry!",
                      "{1} drops you, and something wet comes out of a place that should stay dry!",
                      "{1} drops {0}, and something wet comes out of a place that should stay dry!"),
                    M("{0}'s last words were \"hold my mead\"!",
                      "{1} kills you. Your last words were \"hold my mead\"!",
                      "{1} kills {0}. Their last words were \"hold my mead\"!")
                };
                Add(Strings.Critical, wild);
                Add(Strings.General, wild);
                Mod.Log.Info($"[RevivalGuard] ExtraDeathLines: 26 added (critical {Strings.Critical.Count}, pk critical {Strings.PKCritical.Count})");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExtraDeathLines: {e.Message}"); }
        }

        static void Add(List<DeathMessage> list, params DeathMessage[] lines)
        {
            foreach (var l in lines)
                if (!list.Exists(x => x.Killer == l.Killer)) list.Add(l);
        }
    }

    /// <summary>
    /// A random PKCritical line instead of always [0]. Mirrors Creature.GetDeathMessage for that one case
    /// (same early-out, same killer notification); any other case, or an error, runs ACE's own.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetDeathMessage))]
    public static class ExtraDeathLines_PkCritical
    {
        static bool Prefix(Creature __instance, DamageHistoryInfo lastDamagerInfo, bool criticalHit, ref DeathMessage __result)
        {
            try
            {
                if (!criticalHit || !(__instance is Player) || lastDamagerInfo == null || !lastDamagerInfo.IsPlayer) return true;
                if (lastDamagerInfo.Guid == __instance.Guid) return true;
                var lastDamager = lastDamagerInfo.TryGetAttacker();
                if (lastDamager is Hotspot || lastDamager is Food) return true;
                var list = Strings.PKCritical;
                if (list.Count < 2) return true;
                var msg = list[ThreadSafeRandom.Next(0, list.Count - 1)];
                if (lastDamager is Player killer && killer.Session != null)
                    killer.Session.Network.EnqueueSend(new GameEventKillerNotification(killer.Session, string.Format(msg.Killer, __instance.Name)));
                __result = msg;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExtraDeathLines pk critical: {e.Message}"); return true; }
        }
    }
}
