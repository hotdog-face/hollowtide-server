using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// KNOCKBACK. Ours, not retail: AC never had it. Owner, playtest 2026-09-29, of the
    /// Unbound Queen: "some sort of knockback ... where a boss causes ... something to be afraid of".
    ///
    /// A player's position is the client's to report, so the server cannot shove one. It asks: a
    /// hit from a listed creature that takes at least a set share of the player's health sends
    /// "knock" on the mod channel with a world velocity (AC axes: x east, y north, z up, m/s), and our
    /// client hands that to its physics body the way a jump leaves the ground. Retail clients never
    /// read the channel and are never sent it, so they are simply not knocked. The movement guard is
    /// credited with the distance first, so the landing is not refused as a speed cheat.
    ///
    /// Switch `rg_knockback`, default OFF (tools/gpubox-ace/live-switch.sh). `@knockme [m/s]` (admin)
    /// knocks you backwards to feel it.
    /// </summary>
    public static class Knockback
    {
        public const string P_ON = "rg_knockback";

        /// <summary>Which creatures knock, how hard, and how big a hit must be: horizontal m/s, lift
        /// m/s, and the share of the target's maximum health one hit has to take. First-pass tuning:
        /// 7 m/s out and 3 m/s up is about 0.6 s in the air and 4 m of ground.</summary>
        static readonly Dictionary<uint, (float h, float up, float frac)> KNOCKERS = new Dictionary<uint, (float, float, float)>
        {
            [900803] = (6f, 2.5f, 0.10f),  // The Unbound Queen
        };

        static bool s_Registered;

        public static bool On => PropertyManager.GetBool(P_ON, false).Item;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard Knockback: big hits from the Unbound Queen knock our client's player back (ours, not retail)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Knockback: property default not registered: {e.Message}"); }
            CommandManager.TryAddCommand(KnockMe, "knockme", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Knock yourself backwards to feel the knockback (ours, not retail).", "[m/s]");
            Mod.Log.Info($"[RevivalGuard] Knockback: {KNOCKERS.Count} knockers, {(On ? "ON" : "off")} ({P_ON}), @knockme");
        }

        /// <summary>Is this a hit that knocks? Called after the damage has landed.</summary>
        internal static void OnHit(Player p, WorldObject source, int damage)
        {
            if (p == null || source == null || damage <= 0 || p.IsDead || !On) return;
            if (!KNOCKERS.TryGetValue(source.WeenieClassId, out var k)) return;
            uint max = p.Health.MaxValue;
            if (max == 0 || damage < max * k.frac) return;
            Push(p, source, k.h, k.up);
        }

        /// <summary>Knock a player away from a source.</summary>
        public static bool Push(Player p, WorldObject from, float h, float up)
        {
            if (p?.Location == null || from?.Location == null) return false;
            var off = from.Location.GetOffset(p.Location);   // from the source to the player, world metres
            var dir = new Vector3(off.X, off.Y, 0f);
            if (dir.LengthSquared() < 0.0001f) dir = -Facing(p);   // standing on it: back from where they look
            return Send(p, Vector3.Normalize(dir) * h + new Vector3(0f, 0f, up));
        }

        /// <summary>Knock a player away from a point in the same landblock (a slam's centre, say),
        /// if the switch is on.</summary>
        public static bool PushFrom(Player p, Vector3 point, float h, float up)
        {
            if (p?.Location == null || !On) return false;
            var d = p.Location.Pos - point; d.Z = 0f;
            if (d.LengthSquared() < 0.0001f) d = -Facing(p);
            return Send(p, Vector3.Normalize(d) * h + new Vector3(0f, 0f, up));
        }

        /// <summary>The unit vector a player faces, in world axes.</summary>
        static Vector3 Facing(Player p)
        {
            var f = Vector3.Transform(Vector3.UnitY, p.Location.Rotation);
            f.Z = 0f;
            return f.LengthSquared() < 0.0001f ? Vector3.UnitY : Vector3.Normalize(f);
        }

        static bool Send(Player p, Vector3 v)
        {
            if (p.Session == null || !ModChannel.Capable(p.Session)) return false;
            // Credit the movement guard with the flight before the client moves: the budget refills at
            // run speed, and a knock is faster than a run.
            if (MovementGuard.s_State.TryGetValue(p, out var st)) st.Budget += new Vector2(v.X, v.Y).Length() * 1.5f + 2f;
            // A KNOCK BREAKS YOUR ATTACK (owner, 2026-09-29: "knockback should cancel a players attacks when
            // hit with it"). ACE's own cancel, the one the client's Cancel Attack message calls: a swing in
            // progress is marked cancelled and the auto-repeat stops, so the player has to re-engage.
            p.HandleActionCancelAttack();
            ModChannel.Send(p.Session, "knock", string.Format(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3}", v.X, v.Y, v.Z));
            return true;
        }

        static void KnockMe(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            float h = 7f;
            if (parameters != null && parameters.Length > 0)
                float.TryParse(parameters[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h);
            h = Math.Clamp(h, 1f, 20f);
            bool sent = Send(p, -Facing(p) * h + new Vector3(0f, 0f, 3f));
            session.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(
                sent ? $"[Knockback] knocked back at {h:F1} m/s (ours, not retail; switch {P_ON} is {(On ? "ON" : "off")}, @knockme ignores it)"
                     : "[Knockback] this client does not read the mod channel, so it cannot be knocked",
                ChatMessageType.Broadcast));
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }
    }

    /// <summary>Monster melee lands through this overload (Monster_Melee.cs:108). The patch's Prepare
    /// registers the feature, so Mod.cs needs no line for it.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage), new Type[] { typeof(WorldObject), typeof(DamageEvent) })]
    static class KnockbackOnHit
    {
        static bool Prepare() { Knockback.Register(); return true; }
        static void Postfix(Player __instance, WorldObject source, int __result) => Knockback.OnHit(__instance, source, __result);
    }
}
