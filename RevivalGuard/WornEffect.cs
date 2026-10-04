using System;
using System.Collections.Concurrent;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN ITEM THAT PLAYS AN EFFECT ON ITS WEARER WHEN PUT ON. Ours, data-driven: an item with PropertyInt
    /// 29130 (WORN_PLAYSCRIPT) broadcasts that PlayScript type on the creature wielding it, from the
    /// wielder, so everyone in range sees it. First user: the Necklace of the Night Swarm (901700,
    /// docs/NIGHT-SWARM.md), type 0x10001, the client's expansion/vfx/night_swarm bat swarm.
    ///
    /// Retail's model for it is Raeta's Necklace (11336): its Laying on of Hands is cast on the wearer when
    /// worn, and that spell's TargetEffect (PlayScript 155) is the butterfly swarm. A spell's effect comes
    /// from the DAT SpellTable, so a new effect cannot ride a spell without a new spell; this property is
    /// the one-row way to give an item one.
    ///
    /// Our numbers are 0x10000 up, clear of retail's PlayScript enum (0x00..0xAD). A retail client looks
    /// one up in the wearer's PhysicsScriptTable, finds nothing and draws nothing; ours plays the effect
    /// whose Spec.playScript matches (AcVfx.ForPlayScript). One play per wearer per COOLDOWN, so taking a
    /// necklace on and off cannot stack swarms on everyone nearby.
    /// </summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.OnWield))]
    static class WornEffect
    {
        internal const PropertyInt WORN_PLAYSCRIPT = (PropertyInt)29130;
        static readonly TimeSpan COOLDOWN = TimeSpan.FromSeconds(12);
        static readonly ConcurrentDictionary<uint, DateTime> s_Last = new ConcurrentDictionary<uint, DateTime>();

        static void Postfix(WorldObject __instance, Creature creature)
        {
            try
            {
                if (__instance == null || creature == null) return;
                int? type = __instance.GetProperty(WORN_PLAYSCRIPT);
                if (type == null || type.Value <= 0) return;
                if (creature.CurrentLandblock == null) return;   // still loading in: nobody to show
                var now = DateTime.UtcNow;
                if (s_Last.TryGetValue(creature.Guid.Full, out var last) && now - last < COOLDOWN) return;
                s_Last[creature.Guid.Full] = now;
                creature.EnqueueBroadcast(new GameMessageScript(creature.Guid, (PlayScript)(uint)type.Value, 1f));
            }
            catch (Exception e) { Mod.Log.Warn($"[WornEffect] {__instance?.Name} on {creature?.Name}: {e.Message}"); }
        }
    }
}
