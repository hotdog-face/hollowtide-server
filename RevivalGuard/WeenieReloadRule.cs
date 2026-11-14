using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of @reloadweenie (WeenieReload.cs; no ACE types, tools/nettest compiles it by path,
    /// WeenieReloadCase): for one live object made from the reloaded wcid, whether it is respawned through its
    /// generator or left as it is, and why. The adapter reads the facts off the WorldObject; this decides.
    ///
    /// Every live object owns its own biota, a full copy of the weenie's properties taken when it was made. Evicting
    /// the weenie from ACE's cache changes nothing that already exists: it changes what the NEXT object made from the
    /// wcid gets (generator spawns, loot, vendor purchases, crafting, @create). So "refresh" can only mean destroy and
    /// make again, and that is only safe for an object nobody holds and nothing is fighting: a generator spawn standing
    /// idle in the world. Everything else is left and named.
    /// </summary>
    public static class WeenieReloadRule
    {
        public enum Action
        {
            Respawn,            // destroyed and made again at once by its own generator profile
            WouldRespawn,       // safe to respawn, but the verb was run without `respawn`
            KeepPlayer,         // a player character (never touched)
            KeepPlayerOwned,    // in a player's pack or worn: the player's own biota, keeps its old values
            KeepContained,      // inside a chest, corpse, vendor or creature's pack
            KeepWielded,        // worn by a creature: refreshed when its wearer respawns
            KeepDead,           // a dying creature: its corpse and the generator's own regen follow
            KeepInCombat,       // awake, has a target, or hurt
            KeepBusy,           // mid-emote (talking to a player, a quest step)
            KeepPet,            // a summoned pet: belongs to a player
            KeepIsGenerator,    // itself a generator: its profiles come from its biota, @reloadblock remakes it
            KeepGeneratorEmpties, // its generator destroys itself when its last spawn goes
            KeepPlaced,         // a landblock_instance placement, no generator: @reloadblock <lb> remakes it
            KeepNoGenerator,    // made by hand (@create, a drop, an emote): nothing would make it again
        }

        /// <summary>What the adapter knows about one live object of the wcid.</summary>
        public sealed class Facts
        {
            public bool IsPlayer;
            public bool OwnedByPlayer;      // a player is somewhere up its container/wielder chain
            public bool InContainer;        // has a container that is not a player (chest, corpse, vendor, creature)
            public bool WieldedByCreature;  // has a wielder that is not a player
            public bool IsCreature;
            public bool IsDead;
            public bool InCombat;           // awake / attack target / health below max
            public bool Busy;               // EmoteManager busy or IsBusy
            public bool IsPet;
            public bool IsGenerator;        // has generator profiles of its own
            public bool HasGenerator;       // spawned by a generator that still tracks it
            public bool GeneratorEmpties;   // destroying it would empty a self-destroying generator
            public bool StaticGuid;         // a landblock_instance placement
        }

        public static Action Decide(Facts f, bool respawn)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            // Order matters: ownership first (a player's item is never ours to touch, whatever else is true of it),
            // then where it is, then what it is doing.
            if (f.IsPlayer) return Action.KeepPlayer;
            if (f.OwnedByPlayer) return Action.KeepPlayerOwned;
            if (f.IsPet) return Action.KeepPet;
            if (f.WieldedByCreature) return Action.KeepWielded;
            if (f.InContainer) return Action.KeepContained;
            if (f.IsGenerator) return Action.KeepIsGenerator;
            if (f.IsCreature && f.IsDead) return Action.KeepDead;
            if (f.IsCreature && f.InCombat) return Action.KeepInCombat;
            if (f.Busy) return Action.KeepBusy;
            if (!f.HasGenerator) return f.StaticGuid ? Action.KeepPlaced : Action.KeepNoGenerator;
            if (f.GeneratorEmpties) return Action.KeepGeneratorEmpties;
            return respawn ? Action.Respawn : Action.WouldRespawn;
        }

        /// <summary>One short phrase per action for the report ("3 in combat").</summary>
        public static string Phrase(Action a)
        {
            switch (a)
            {
                case Action.Respawn: return "respawned by their generator";
                case Action.WouldRespawn: return "idle generator spawns (add `respawn` to remake them)";
                case Action.KeepPlayer: return "player characters";
                case Action.KeepPlayerOwned: return "in online players' packs or worn (their own copies keep the old values)";
                case Action.KeepContained: return "inside a chest, corpse, vendor or pack";
                case Action.KeepWielded: return "worn by a creature (new with the creature's next spawn)";
                case Action.KeepDead: return "dying";
                case Action.KeepInCombat: return "in combat or hurt";
                case Action.KeepBusy: return "busy with an emote";
                case Action.KeepPet: return "summoned pets";
                case Action.KeepIsGenerator: return "generators themselves (@reloadblock remakes them)";
                case Action.KeepGeneratorEmpties: return "last spawn of a self-destroying generator";
                case Action.KeepPlaced: return "placed in a landblock (@reloadblock remakes them)";
                case Action.KeepNoGenerator: return "made by hand, no generator";
                default: return a.ToString();
            }
        }

        /// <summary>The verb's report: counts by action, respawns first, in a stable order.</summary>
        public static string Summary(uint wcid, string name, bool evicted, IEnumerable<Action> actions, bool respawn)
        {
            var counts = actions.GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
            var head = $"wcid {wcid}{(string.IsNullOrEmpty(name) ? "" : $" ({name})")}: "
                + (evicted ? "dropped from the weenie cache" : "was not in the weenie cache")
                + "; the next one made reads the database.";
            if (counts.Count == 0) return head + " No live copies in loaded landblocks or online players' packs.";
            var parts = Enum.GetValues(typeof(Action)).Cast<Action>()
                .Where(counts.ContainsKey).Select(a => $"{counts[a]} {Phrase(a)}");
            var tail = head + " Live copies: " + string.Join("; ", parts) + ".";
            if (!respawn && counts.ContainsKey(Action.WouldRespawn)) tail += " Nothing was destroyed.";
            return tail;
        }

        /// <summary>wcids for the verb: decimal, 1..uint.MaxValue. Hex is refused: wcids are written in decimal
        /// everywhere in this project (SQL, the wiki, @create).</summary>
        public static bool TryParseWcid(string arg, out uint wcid)
        {
            wcid = 0;
            if (string.IsNullOrWhiteSpace(arg)) return false;
            var s = arg.Trim();
            if (!s.All(char.IsDigit)) return false;
            return uint.TryParse(s, out wcid) && wcid > 0;
        }
    }
}
