using System;
using System.Collections.Concurrent;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BAEL'ZHARON CANNOT BE KILLED FROM A DISTANCE. Ours, not retail. Owner, 2026-09-29: "im able to
    /// shoot him from far away and he doesnt attack back. thats a ticket for players to simply kill him
    /// without a fight."
    ///
    /// Every hit a creature takes, melee, missile or spell, is written through DamageHistory.Add, so a
    /// postfix there sees them all. A player (or their arrow or spell) that hits him from beyond
    /// PULL_RANGE twice inside WINDOW seconds is marked with the boss_shadowfall warning
    /// (BossWarning.Warn) and, PULL_DELAY later, dragged through the shadow to stand in front of him.
    /// Up close nothing changes. Rides the rg_boss_telegraphs switch.
    /// </summary>
    public static class BaelShadowPull
    {
        const float PULL_RANGE = 22f;
        const double WINDOW = 15, PULL_DELAY = 1.2, COOLDOWN = 12;

        sealed class St { public int Far; public DateTime FirstFar, PulledAt; }
        static readonly ConcurrentDictionary<uint, St> s_St = new ConcurrentDictionary<uint, St>();

        internal static void OnHit(Creature boss, WorldObject attacker)
        {
            if (boss == null || boss.WeenieClassId != WorldBoss.BAEL || boss.IsDead || !BossWarning.On) return;
            var p = attacker as Player ?? attacker?.ProjectileSource as Player;
            if (p == null || p.IsDead || p.Location == null || boss.Location == null) return;
            // Outdoors (since 2026-10-01) a player one landblock over can stand 5 m away: DistanceTo
            // measures across landblocks, so only a different dungeon counts as far by itself.
            bool otherDungeon = p.Location.Landblock != boss.Location.Landblock && (IsDungeon(boss) || IsDungeon(p));
            bool far = otherDungeon || p.Location.DistanceTo(boss.Location) > PULL_RANGE;
            if (!far) return;
            var st = s_St.GetOrAdd(p.Guid.Full, _ => new St());
            var now = DateTime.UtcNow;
            if ((now - st.PulledAt).TotalSeconds < COOLDOWN) return;
            if ((now - st.FirstFar).TotalSeconds > WINDOW) { st.FirstFar = now; st.Far = 0; }
            if (++st.Far < 2) return;
            st.Far = 0; st.PulledAt = now;

            BossWarning.Warn(p);
            p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                "Bael'Zharon turns his gaze on you across the dark. The shadow takes hold of you!", ChatMessageType.Broadcast));
            var chain = new ActionChain();
            chain.AddDelaySeconds(PULL_DELAY);
            chain.AddAction(p, () =>
            {
                if (p.IsDead || boss.IsDead || boss.IsDestroyed || boss.Location == null) return;
                var to = boss.Location.InFrontOf(2.5f);
                // Outdoors the 2.5 m can cross into the next 24 m cell or landblock; a cell id of 0
                // makes ACE's Position work out the right one (Position(uint, ...) calls SetPosition).
                if (!boss.Location.Indoors)
                    to = new ACE.Entity.Position(to.LandblockId.Raw & 0xFFFF0000, to.PositionX, to.PositionY, to.PositionZ, 0f, 0f, to.RotationZ, to.RotationW);
                p.Teleport(to);
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    "You are dragged through the shadow to Bael'Zharon's feet.", ChatMessageType.Combat));
                Mod.Log.Info($"[RevivalGuard] Bael'Zharon: shadow-pulled {p.Name} (hit him from beyond {PULL_RANGE} m)");
            });
            chain.EnqueueChain();
        }

        static bool IsDungeon(WorldObject wo) => wo.CurrentLandblock?.IsDungeon ?? true;
    }

    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class BaelShadowPullHook
    {
        static void Postfix(DamageHistory __instance, WorldObject attacker)
        {
            try { BaelShadowPull.OnHit(__instance.Creature, attacker); }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] BaelShadowPull threw", e); }
        }
    }
}
