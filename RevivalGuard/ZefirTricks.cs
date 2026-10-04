using System.Collections.Concurrent;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ZEFIR PET TRICKS (owner, 2026-10-01: "Pet tricks: use the charm on a summoned pet to make it
    /// dance, spin or sit, using the Zefir's own animations"). A Zefir Charm (900760-900768,
    /// tools/gpubox-ace/gen_zefir_pets.py) used ON its owner's summoned pet plays one trick; a plain
    /// Use still summons or dismisses (ACE's PetDevice.ActOnUse, untouched). Anything else the charm
    /// is used on gets ACE's own TargetCompatibleWithObject line.
    ///
    /// The tricks come from the Zefir motion table 0x09000069 (sweepdata mt/anim, 2026-10-01):
    /// TurnRight (omega -4 rad/s), SideStepRight (anim 0x03000563) and AttackHigh1 (0x03000182, a
    /// forward dip). Not used: Dead (0x03000187, sinks to the ground; no way back up in this table,
    /// and clients treat a Dead forward command as a death pose) and CastSpell (0x0300062C, rears
    /// back and surges; our client has no clip for it, so its players saw nothing).
    ///
    /// Plain motion broadcasts, so every client in range sees them, retail's included. The server's
    /// own copy of the pet does not move (applyPhysics is off for non-players), so the spin is two
    /// whole turns and the dance ends where it began. If the pet starts following mid-trick, the
    /// trick stops sending and the follow wins.
    /// </summary>
    static class ZefirTricks
    {
        internal const uint FirstCharm = 900760, LastCharm = 900768;
        const double Cooldown = 3.0;

        internal static long Done;

        // per player: the next trick in the cycle and when the next one may start
        static readonly ConcurrentDictionary<uint, (int next, DateTime until)> s_State = new();
        static readonly Random s_Rng = new();

        internal static bool IsCharm(WorldObject wo)
            => wo is PetDevice && wo.WeenieClassId >= FirstCharm && wo.WeenieClassId <= LastCharm;

        /// <summary>"Dusk Zefir" out of "+Name's Pet Dusk Zefir".</summary>
        static string Kind(Pet pet)
        {
            var n = pet.Name ?? "pet";
            int i = n.LastIndexOf("Pet ", StringComparison.Ordinal);
            return i >= 0 ? n.Substring(i + 4) : n;
        }

        internal static void Perform(Player player, Pet pet)
        {
            var now = DateTime.UtcNow;
            var st = s_State.GetOrAdd(player.Guid.Full, _ => (s_Rng.Next(3), DateTime.MinValue));
            if (now < st.until) { player.SendUseDoneEvent(); return; }   // the cooldown answers with nothing
            if (pet.IsMoving)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your {Kind(pet)} is too busy following you.", ChatMessageType.Broadcast));
                player.SendUseDoneEvent();
                return;
            }
            int trick = st.next % 3;
            s_State[player.Guid.Full] = ((trick + 1) % 3, now.AddSeconds(Cooldown));

            var stance = pet.CurrentMotionState?.Stance ?? MotionStance.NonCombat;
            if (stance == MotionStance.Invalid) stance = MotionStance.NonCombat;
            var chain = new ActionChain();
            string line;
            switch (trick)
            {
                case 0:   // two whole turns: TurnRight's omega is 4 rad/s, at speed 2 that is 8 rad/s for pi/2 s
                    line = "twirls in the air";
                    Step(chain, pet, Turn(stance, 2f), 0);
                    Step(chain, pet, new Motion(stance), Math.PI / 2);
                    break;
                case 1:   // right, left twice as long, right: back where it started
                    line = "dances from side to side";
                    Step(chain, pet, Side(stance, 1f), 0);
                    Step(chain, pet, Side(stance, -1f), 0.5);
                    Step(chain, pet, Side(stance, 1f), 1.0);
                    Step(chain, pet, new Motion(stance), 0.5);
                    break;
                default:  // AttackHigh1: a forward dip, 15 frames at 15 fps
                    line = "dips in a little bow";
                    Step(chain, pet, new Motion(stance, MotionCommand.AttackHigh1), 0);
                    Step(chain, pet, new Motion(stance), 1.05);
                    break;
            }
            chain.EnqueueChain();
            Done++;
            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your {Kind(pet)} {line}.", ChatMessageType.Emote));
            Mod.Log.Info($"[RevivalGuard] ZefirTricks: {pet.Name} (0x{pet.Guid}) trick {trick} ({line})");
            player.SendUseDoneEvent();
        }

        static Motion Turn(MotionStance stance, float speed)
        {
            var m = new Motion(stance);
            m.SetTurnCommand(MotionCommand.TurnRight, speed);
            return m;
        }

        static Motion Side(MotionStance stance, float speed)
        {
            var m = new Motion(stance);
            m.SetSidestepCommand(MotionCommand.SideStepRight, speed);   // ACE's (and retail's) left is right * -1
            return m;
        }

        static void Step(ActionChain chain, Pet pet, Motion motion, double delay)
        {
            if (delay > 0) chain.AddDelaySeconds(delay);
            chain.AddAction(pet, () =>
            {
                if (pet.IsMoving || pet.IsDestroyed || pet.CurrentLandblock == null) return;   // following: the follow wins
                pet.CurrentMotionState = motion;
                pet.EnqueueBroadcastMotion(motion);
            });
        }
    }

    /// <summary>Ahead of ACE's use-with-target, as Keyrings is: a Zefir Charm is the source.</summary>
    [HarmonyPatch]
    static class ZefirTrickUseWith
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.HandleActionUseWithTarget), new[] { typeof(uint), typeof(uint) });
            if (m == null) { Mod.Log.Error("[RevivalGuard] ZefirTricks: Player.HandleActionUseWithTarget(uint, uint) not found; no pet tricks"); yield break; }
            // Logged from here so Mod.cs (shared) needs no line: PatchAll resolves this once at load.
            Mod.Log.Info("[RevivalGuard] ZefirTricks: hook on (a Zefir Charm used on its own pet plays a trick). "
                       + "Grep ACE_Log.txt for 'ZefirTricks: ' + ' trick ' to see it run.");
            yield return m;
        }

        static bool Prefix(Player __instance, uint sourceObjectGuid, uint targetObjectGuid)
        {
            try
            {
                var player = __instance;
                if (player == null || player.PKLogout) return true;
                var charm = player.FindObject(sourceObjectGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);
                if (!ZefirTricks.IsCharm(charm)) return true;
                var target = player.FindObject(targetObjectGuid,
                    Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems | Player.SearchLocations.Landblock);
                if (target is Pet pet && pet.P_PetOwner == player && player.CurrentActivePet == pet)
                {
                    ZefirTricks.Perform(player, pet);
                    return false;
                }
                // ACE's ItemHolder::TargetCompatibleWithObject line (Player_Use.cs)
                player.SendTransientError($"Cannot use the {charm.Name} with the {target?.Name ?? "that"}");
                player.SendUseDoneEvent();
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] ZefirTricks: {e.GetType().Name} in use-with-target: {e.Message}; ACE's path runs");
                return true;
            }
        }
    }
}
