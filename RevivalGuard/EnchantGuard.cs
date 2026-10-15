using System;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SPELL LANDING MUST NEVER TAKE THE WORLD DOWN WITH IT (docs/PERF-PLAYABILITY-02.md item 7).
    ///
    /// 2026-10-13 01:59 UTC the live server aborted (systemd: "code=dumped, status=6/ABRT", every player disconnected)
    /// on a NullReferenceException in WorldObject.CreateEnchantment, reached from a player's cast:
    /// Player.HandleMotionDone_Magic, DoCastSpell_Inner, CreatePlayerSpell (our DispelNullTargetCast prefix runs there),
    /// HandleCastSpell, CreateEnchantment, on the landblock's single-threaded tick.
    ///
    /// CreateEnchantment dereferences, with no check: target (and target.EnchantmentManager), caster (caster.ProcSpell,
    /// caster.Name), the add result's Surpass/Refresh/Surpassed spell for the chat suffix, and a player target's
    /// Session (null once that player has logged off, which a cast in flight can outlive). The journal names the
    /// method, not the line, so the exact field is not proven; the dump on the server
    /// (logs/ACE.Server-363636-1791856757.dmp) could say.
    ///
    /// Two guards, as DeathGuard:
    ///   * Prefix: a cast with no target, no caster or no spell, or onto a player with no session (gone), lands
    ///     nowhere and is logged; the caster has spent the mana, as for any fizzle.
    ///   * Finalizer: anything else it throws is logged with caster, target and spell named, and swallowed, so a
    ///     bad spell costs one enchantment, not the server.
    /// </summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CreateEnchantment))]
    public static class EnchantGuard
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance, WorldObject target, WorldObject caster, Spell spell)
        {
            string why = target == null ? "no target" : caster == null ? "no caster" : spell == null ? "no spell"
                       : target.EnchantmentManager == null ? "the target has no enchantments"
                       : target is Player tp && tp.Session == null ? "the target has logged off" : null;
            if (why == null) return true;
            Mod.Log.Warn($"[RevivalGuard] enchantment skipped ({why}): {spell?.Name ?? "?"} by {caster?.Name ?? __instance?.Name} on {target?.Name}");
            return false;
        }

        static Exception Finalizer(WorldObject __instance, WorldObject target, WorldObject caster, Spell spell, Exception __exception)
        {
            if (__exception != null)
                Mod.Log.Error($"[RevivalGuard] enchantment {spell?.Name} by {caster?.Name ?? __instance?.Name} on {target?.Name} threw, kept the server up: {__exception}");
            return null;
        }
    }
}
