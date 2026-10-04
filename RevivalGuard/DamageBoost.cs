using System.Collections.Concurrent;
using System.Globalization;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN ADMIN'S DAMAGE BOOST, FOR TESTING BOSS FIGHTS (owner 2026-09-29: an admin character "needs to do more damage so
    /// he can win these boss fights while testing"). Ours, not retail, and never a player's.
    ///
    ///   @dmgboost on [x]   your damage to creatures times x (default 5, 1.5 to 1000)
    ///   @dmgboost off      back to normal
    ///   @dmgboost          what it is set to
    ///
    /// The admin panel's Self tab has it as the "Damage x5" switch (AcAdminHud reads the reply line).
    ///
    /// SAFE BY CONSTRUCTION: the command is AccessLevel.Admin, the boost lives in memory only (a restart or a
    /// relog to another character drops it; nothing is written to anyone's character), and the hooks re-check
    /// the attacker's session access level on every hit, so a boost can never outlive the rights that set it.
    /// Creatures only: a boosted admin's damage to a PLAYER is untouched.
    ///
    /// What it multiplies: melee and missile hits (DamageEvent.CalculateDamage, which Player_Combat.cs and
    /// ProjectileCollisionHelper.cs both land) and war-magic bolts (SpellProjectile.CalculateDamage).
    /// Life-magic drains and harms are left alone.
    /// </summary>
    internal static class DamageBoost
    {
        const float DEFAULT = 5f, MIN = 1.5f, MAX = 1000f;   // 1000: enough to test a world boss (Bael, 1.25M health), owner 2026-09-29
        static readonly ConcurrentDictionary<uint, float> s_On = new ConcurrentDictionary<uint, float>();
        static bool s_Registered;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            CommandManager.TryAddCommand(Handle, "dmgboost", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Admin test aid (ours, not retail): multiply YOUR damage to creatures. Memory only, off after a restart.",
                "[on [multiplier] | off]");
            Mod.Log.Info("[RevivalGuard] DamageBoost: @dmgboost (admin only, memory only)");
        }

        /// <summary>The multiplier for this attacker against this target, or 1.</summary>
        static float For(WorldObject source, WorldObject target)
        {
            if (s_On.IsEmpty || target is Player || !(source is Player p)) return 1f;
            if (!s_On.TryGetValue(p.Guid.Full, out var x)) return 1f;
            if (p.Session == null || p.Session.AccessLevel < AccessLevel.Admin) { s_On.TryRemove(p.Guid.Full, out _); return 1f; }
            return x;
        }

        static void Say(Session s, string line) => s?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        static void Handle(Session session, params string[] args)
        {
            var p = session?.Player;
            if (p == null) return;
            string verb = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (verb == "on")
            {
                float x = DEFAULT;
                if (args.Length > 1 && float.TryParse(args[1].TrimStart('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) x = v;
                x = Math.Clamp(x, MIN, MAX);
                s_On[p.Guid.Full] = x;
                Mod.Log.Info($"[RevivalGuard] DamageBoost: {p.Name} ON x{x:0.#}");
            }
            else if (verb == "off")
            {
                s_On.TryRemove(p.Guid.Full, out _);
                Mod.Log.Info($"[RevivalGuard] DamageBoost: {p.Name} OFF");
            }
            // The panel's switch keys on this exact prefix: "[DamageBoost] (ours) ON" / "OFF".
            Say(session, s_On.TryGetValue(p.Guid.Full, out var cur)
                ? $"[DamageBoost] (ours) ON: your damage to creatures is x{cur:0.#} until you switch it off, relog or the server restarts."
                : "[DamageBoost] (ours) OFF: your damage is normal.");
        }

        /// <summary>Melee and missile: the DamageEvent itself, so the "You hit ... for N" line shows the boosted
        /// number that actually lands (Player_Combat.cs and ProjectileCollisionHelper.cs both read .Damage).</summary>
        [HarmonyPatch(typeof(DamageEvent), nameof(DamageEvent.CalculateDamage))]
        static class MeleeMissile
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(Creature attacker, Creature defender, DamageEvent __result)
            {
                try { if (__result != null && __result.HasDamage) { float x = For(attacker, defender); if (x != 1f) __result.Damage *= x; } }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.CalculateDamage))]
        static class Bolts
        {
            static void Postfix(WorldObject source, Creature target, ref float? __result)
            {
                try { if (__result.HasValue) { float x = For(source, target); if (x != 1f) __result = __result.Value * x; } }
                catch { }
            }
        }
    }
}
