using System;
using System.Collections.Concurrent;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>An item that plays an effect on its wearer when put on.</summary>
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
