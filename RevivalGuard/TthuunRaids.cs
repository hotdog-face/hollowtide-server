using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using SetPosition = ACE.Server.Physics.Common.SetPosition;
using SetPositionFlags = ACE.Server.Physics.Common.SetPositionFlags;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;
using Site = RevivalGuard.TthuunRaidsRule.Site;

namespace RevivalGuard
{
    /// <summary>
    /// T'THUUN'S MARAUDERS, THE TOWN RAIDS (retail Nov 2008; revived 2026-11-01 for the owner's review, calendar Nov 16 to 29;
    /// docs/EVENT-TESTS/tthuun-marauders.md). The calendar (SeasonalCalendar "tthuun") puts a Royal Scout and a Falatacot
    /// pyramid at each of the twelve retail sites for the fortnight (row TthuunMarauders); the Event Director runs one site's
    /// raid at a time (tthuun-*, its own Director row, 60 min). This file runs the raid itself, rules in TthuunRaidsRule:
    ///   * WAVES: wave 1 is the site's wave generators (the datafix); when every creature of it is down, the waves after it
    ///     (four Elites or Executioners, an Inquisitor or Champion, the town's Raid Commander) come one by one at the site's
    ///     centre. Wave 1's generators are HELD from then on (GeneratorProfile.ProcessQueue), never disabled: ACE's staged
    ///     event check would turn a disabled generator of a row that is still On back on within 10 s. The last boss down
    ///     stops the row, and the director ends the event as won (its victory line).
    ///   * SCALING like the Frostfell raids: each raid creature's health by FrostfellRule.RaiderShare of the defenders near
    ///     the site when it appears (40% for one, full from six).
    ///   * STONES: a raid creature's corpse carries its site's Invoking Stone (1 in 3; bosses always), not the Patriarch's
    ///     Valley mix its retail list names.
    ///   * ROYAL SCOUTS (904160): retail's lines, retail's stone turn-ins (data), and here the three kill tasks (ACE kill
    ///     quests on the raiders and marauders, the datafix) and the boss titles.
    ///   * THE SPARK: a Raid Commander's fall leaves his Spark for 3 minutes; its Use gives a Mana Forge Key (data).
    ///   * THE KAREB MASK: a raid Raid Commander's corpse carries retail's Kareb Mask 1 time in 10 (owner 2026-11-08).
    /// Cleanup is the director's (its generators, disabled before their spawn is destroyed) plus EventEnded here for the
    /// waves spawned by this file. Only loaded landblocks are ever looked at (IfLoaded never loads one).
    /// Staff: @tthuun [status | reset] (reset: your own kill tasks, flags and wait).
    /// </summary>
    static class TthuunRaids
    {
        internal static bool On => SeasonalCalendar.IsFestivalActive(TthuunRaidsRule.FESTIVAL);
        static double Now => ACE.Common.Time.GetUnixTime();
        static readonly Random s_Rng = new Random();
        const float SITE_R = 150f;      // metres round the site's centre: its defenders and its spawn
        const float SCATTER_R = 15f;    // the later waves' scatter round the centre
        const int SPARK_S = 180;        // wiki: "Disappears after about 3 minutes"

        sealed class Raid
        {
            public Site Site;
            public int Stage = 1;                 // 1 = the generators' wave; 2.. = Waves(site)[Stage - 2]
            public int Wave1Peak;
            public bool Won, Spawning;
            public readonly List<WeakReference<Creature>> Wave = new List<WeakReference<Creature>>();
            public readonly List<WeakReference<Creature>> All = new List<WeakReference<Creature>>();
        }
        static Raid s_Raid;
        static double s_NextTick;
        sealed class Tag { public Site Site; }
        static readonly ConditionalWeakTable<Creature, Tag> s_Site = new ConditionalWeakTable<Creature, Tag>();
        static readonly ConditionalWeakTable<Creature, Tag> s_Scaled = new ConditionalWeakTable<Creature, Tag>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "tthuun", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "T'thuun's Marauders (RevivalGuard): status, or reset (your own kill tasks, boss flags and wait).", "[status | reset]");
            Mod.Log.Info($"[RevivalGuard] TthuunRaids: {TthuunRaidsRule.Sites.Length} raid sites, waves after the generators, site stones, Royal Scout kill tasks (@tthuun)");
        }

        // ---- the running raid -----------------------------------------------------------------------------------------

        internal static void Tick()
        {
            var now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 2;
            var ev = EventDirector.CurrentEvent();
            var site = TthuunRaidsRule.ByKey(ev?.key);
            if (site != s_Raid?.Site) s_Raid = site == null ? null : new Raid { Site = site };
            if (s_Raid == null || s_Raid.Won || s_Raid.Spawning) return;
            if (IfLoaded(site.Block) == null) return;   // nobody there: nothing spawns, nothing to watch

            var gens = SiteGenerators(site).ToList();
            if (gens.Count == 0) return;
            var centre = gens[0];
            var defenders = Defenders(centre);

            // tag and scale every live raid creature of the site, once
            var wave1 = gens.SelectMany(Spawned).ToList();
            foreach (var c in wave1.Concat(Live(s_Raid.All))) Adopt(c, site, defenders.Count);

            if (s_Raid.Stage == 1)
            {
                s_Raid.Wave1Peak = Math.Max(s_Raid.Wave1Peak, wave1.Count);
                if (s_Raid.Wave1Peak == 0 || wave1.Count > 0) return;
                Next(centre, defenders, "The last of the raiders falls, and the Patriarchs' device hums louder. Something bigger is coming through!");
                return;
            }
            if (Live(s_Raid.Wave).Any()) return;
            var waves = TthuunRaidsRule.Waves(site);
            if (s_Raid.Stage - 1 < waves.Length) { Next(centre, defenders, null); return; }

            // the last boss is down: the raid is won. Stopping the row lets the director end it as won (its victory line).
            s_Raid.Won = true;
            Mod.Log.Info($"[RevivalGuard] TthuunRaids: {site.Key} won by {defenders.Count} defender(s)");
            if (EventManager.GetEventStatus(site.Row) == GameEventState.On) EventManager.StopEvent(site.Row, null, null);
        }

        /// <summary>The next wave at the site's centre (its first wave generator).</summary>
        static void Next(WorldObject centre, List<Player> defenders, string line)
        {
            var raid = s_Raid;
            var (wcid, count) = TthuunRaidsRule.Waves(raid.Site)[raid.Stage - 1];
            raid.Stage++;
            raid.Wave.Clear();
            raid.Spawning = true;
            var at = new ACE.Entity.Position(centre.Location);
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var made = new List<Creature>();
                    for (int i = 0; i < count; i++)
                    {
                        var c = Spawn(wcid, at, SCATTER_R) as Creature;
                        if (c == null) continue;
                        raid.Wave.Add(new WeakReference<Creature>(c));
                        raid.All.Add(new WeakReference<Creature>(c));
                        s_Site.AddOrUpdate(c, new Tag { Site = raid.Site });
                        made.Add(c);
                    }
                    if (made.Count == 0) { raid.Stage--; Mod.Log.Warn($"[RevivalGuard] TthuunRaids: {raid.Site.Key}: wave of {wcid} did not spawn; trying again"); return; }
                    string name = made[0].Name;
                    string text = made.Count > 1 ? $"{made.Count} {made[0].GetPluralName()} step out of the Patriarchs' device!"
                                                 : $"{name} steps out of the Patriarchs' device!";
                    foreach (var p in defenders) { if (line != null) Say(p, line); Say(p, text); }
                    Mod.Log.Info($"[RevivalGuard] TthuunRaids: {raid.Site.Key} wave {raid.Stage}: {made.Count} x {name}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: wave spawn: {e.Message}"); }
                finally { raid.Spawning = false; }
            }));
        }

        /// <summary>A Generic object entering the world at a point, scattered as ACE's own generators do (Spawn_Scatter:
        /// the physics places it on the ground within the radius, retrying when a spot fails).</summary>
        static WorldObject Spawn(uint wcid, ACE.Entity.Position at, float radius)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: no weenie {wcid}"); return null; }
            wo.Location = new ACE.Entity.Position(at);
            wo.Location.PositionZ += 0.05f;
            if (radius > 0) wo.ScatterPos = new SetPosition(new ACE.Server.Physics.Common.Position(wo.Location), SetPositionFlags.RandomScatter, radius);
            bool ok = wo.EnterWorld();
            wo.ScatterPos = null;
            if (!ok) { wo.Destroy(); return null; }
            return wo;
        }

        static void Adopt(Creature c, Site site, int defenders)
        {
            if (!s_Site.TryGetValue(c, out _)) s_Site.Add(c, new Tag { Site = site });
            if (s_Scaled.TryGetValue(c, out _)) return;
            s_Scaled.Add(c, new Tag { Site = site });
            double k = FrostfellRule.RaiderShare(defenders);
            uint oldMax = c.Health.MaxValue, want = (uint)Math.Max(1, Math.Round(oldMax * k));
            if (want == oldMax) return;
            var cc = c;
            var chain = new ActionChain();
            chain.AddAction(cc, () =>
            {
                try
                {
                    if (cc.IsDead) return;
                    cc.Health.StartingValue = (uint)Math.Max(1, (long)cc.Health.StartingValue + want - oldMax);
                    cc.UpdateVital(cc.Health, cc.Health.MaxValue);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: scale: {e.Message}"); }
            });
            chain.EnqueueChain();
            Mod.Log.Info($"[RevivalGuard] TthuunRaids: {site.Key} {c.Name} 0x{c.Guid.Full:X8}: {defenders} defender(s), health {oldMax:N0} -> {want:N0}");
        }

        /// <summary>The site's wave generators, in loaded landblocks only (the site's and its eight neighbours).</summary>
        static IEnumerable<WorldObject> SiteGenerators(Site site)
        {
            int bx = site.Block >> 8, by = site.Block & 0xFF;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int x = bx + dx, y = by + dy;
                    if (x < 0 || y < 0 || x > 0xFE || y > 0xFE) continue;
                    var lb = IfLoaded((ushort)(x << 8 | y));
                    if (lb == null) continue;
                    foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                        if (wo != null && wo.IsGenerator && !string.IsNullOrEmpty(wo.GeneratorEvent)
                            && site.Row.Equals(EventManager.GetEventName(wo.GeneratorEvent), StringComparison.OrdinalIgnoreCase))
                            yield return wo;
                }
        }

        static IEnumerable<Creature> Spawned(WorldObject gen) =>
            gen.GeneratorProfiles == null ? Enumerable.Empty<Creature>()
            : gen.GeneratorProfiles.SelectMany(p => p.Spawned.Values).Select(i => i.TryGetWorldObject()).OfType<Creature>().Where(c => !c.IsDead && !c.IsDestroyed);

        static IEnumerable<Creature> Live(List<WeakReference<Creature>> list) =>
            list.Select(w => w.TryGetTarget(out var c) ? c : null).Where(c => c != null && !c.IsDead && !c.IsDestroyed);

        static List<Player> Defenders(WorldObject centre) =>
            PlayerManager.GetAllOnline().Where(p => p?.Location != null && centre?.Location != null && !Mercenaries.IsMerc(p)
                && Math.Abs((int)p.Location.LandblockX - (int)centre.Location.LandblockX) <= 1
                && Math.Abs((int)p.Location.LandblockY - (int)centre.Location.LandblockY) <= 1
                && p.Location.DistanceTo(centre.Location) <= SITE_R)
            .GroupBy(p => p.Session?.AccountId ?? p.Guid.Full).Select(g => g.First()).ToList();

        /// <summary>Wave 1's generators wait from wave 2 on: a queued respawn is held, not dropped (Frostfell.HoldWave).</summary>
        internal static bool HoldWave(GeneratorProfile prof)
        {
            var raid = s_Raid;
            if (raid == null || raid.Stage < 2 && !raid.Won) return false;
            var ge = prof?.Generator?.GeneratorEvent;
            return !string.IsNullOrEmpty(ge) && raid.Site.Row.Equals(EventManager.GetEventName(ge), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>EventDirector.End: the waves this file spawned go with the event (alive ones only: corpses are loot; the
        /// Spark stays its 3 minutes). Only objects still in a loaded landblock are touched.</summary>
        internal static void EventEnded(string key, bool won)
        {
            var raid = s_Raid;
            if (raid == null || !raid.Site.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return;
            s_Raid = null;
            var left = Live(raid.All).Where(c => c.CurrentLandblock != null).ToList();
            if (left.Count == 0) return;
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                foreach (var c in left) try { if (!c.IsDead && !c.IsDestroyed) c.Destroy(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: cleanup: {e.Message}"); }
            }));
            Mod.Log.Info($"[RevivalGuard] TthuunRaids: {key} ended ({(won ? "won" : "not won")}); {left.Count} wave creature(s) removed");
        }

        // ---- deaths: stones, boss credit, the Spark ---------------------------------------------------------------------

        /// <summary>Creature.GenerateTreasure: a raid creature's corpse gets its site's stone, not the Valley's mix.</summary>
        internal static void OnTreasure(Creature c, Corpse corpse)
        {
            if (corpse == null || !s_Site.TryGetValue(c, out var tag)) return;
            foreach (var item in corpse.Inventory.Values.Where(i => TthuunRaidsRule.STONES.Contains(i.WeenieClassId)).ToList())
                if (corpse.TryRemoveFromInventory(item.Guid, out var gone)) gone?.Destroy();
            if (c.WeenieClassId == tag.Site.Commander && s_Rng.NextDouble() < TthuunRaidsRule.KAREB_CHANCE)
            {
                var mask = WorldObjectFactory.CreateNewWorldObject(TthuunRaidsRule.KAREB_MASK);
                if (mask != null && !corpse.TryAddToInventory(mask)) mask.Destroy();
                else if (mask != null) Mod.Log.Info($"[RevivalGuard] TthuunRaids: {tag.Site.Key} {c.Name} carried a Kareb Mask");
            }
            if (!TthuunRaidsRule.IsBoss(c.WeenieClassId) && s_Rng.NextDouble() >= TthuunRaidsRule.STONE_CHANCE) return;
            var stone = WorldObjectFactory.CreateNewWorldObject(tag.Site.Stone);
            if (stone != null && !corpse.TryAddToInventory(stone)) stone.Destroy();
        }

        /// <summary>Creature.OnDeath: a boss's fall flags everyone who hurt it (the Royal Scouts pay it); a Raid Commander's
        /// leaves his Spark.</summary>
        internal static void OnDeath(Creature c)
        {
            if (!On) return;
            var credit = TthuunRaidsRule.BossCredits.FirstOrDefault(b => b.wcid == c.WeenieClassId);
            if (credit.flag != null && s_Site.TryGetValue(c, out _))
                foreach (var p in Hurt(c))
                {
                    p.QuestManager.Stamp(credit.flag);
                    Say(p, "Speak to a Royal Scout about it.");
                }
            if (s_Site.TryGetValue(c, out var tag) && tag.Site.Commander == c.WeenieClassId && tag.Site.Spark != 0)
            {
                var at = new ACE.Entity.Position(c.Location);
                var site = tag.Site;
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        var spark = WorldObjectFactory.CreateNewWorldObject(site.Spark);
                        if (spark == null) return;
                        spark.Lifespan = SPARK_S;
                        spark.Location = new ACE.Entity.Position(at);
                        spark.Location.PositionZ += 0.05f;
                        spark.ScatterPos = new SetPosition(new ACE.Server.Physics.Common.Position(spark.Location), SetPositionFlags.RandomScatter, 3f);
                        bool ok = spark.EnterWorld();
                        spark.ScatterPos = null;
                        if (!ok) { spark.Destroy(); return; }
                        var near = PlayerManager.GetAllOnline().Where(p => p?.Location != null && p.Location.DistanceTo(spark.Location) <= SITE_R).ToList();
                        foreach (var p in near) Say(p, $"{spark.Name} lingers where the Raid Commander fell. Speak to it within 3 minutes.");
                        Mod.Log.Info($"[RevivalGuard] TthuunRaids: {site.Key} {spark.Name} for {SPARK_S} s, {near.Count} near");
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: spark: {e.Message}"); }
                }));
            }
        }

        static IEnumerable<Player> Hurt(Creature c) =>
            c.DamageHistory.TotalDamage.Values.Where(i => i.TotalDamage > 0)
                .Select(i => i.TryGetAttacker() as Player ?? i.TryGetPetOwner())
                .Where(p => p != null).Distinct();

        // ---- the Royal Scouts ---------------------------------------------------------------------------------------------

        /// <summary>EmoteManager.OnUse of a raid-site Royal Scout (after retail's lines, which the scout's Use emote says):
        /// the boss titles first, then the kill tasks (TthuunRaidsRule.TaskTalk).</summary>
        internal static void OnScout(WorldObject scout, Player p)
        {
            if (!On) return;
            var lines = new List<string>();
            foreach (var b in TthuunRaidsRule.BossCredits)
            {
                if (!p.QuestManager.HasQuest(b.flag)) continue;
                p.QuestManager.Erase(b.flag);
                lines.Add(b.line);
                if (b.xp > 0) p.GrantXP(b.xp, XpType.Quest, ShareType.None);
                AwardTitle(p, b.title);
            }
            var qm = p.QuestManager;
            bool active = TthuunRaidsRule.FAMILIES.Any(f => qm.HasQuest(TthuunRaidsRule.KillQuest(f)));
            var kills = TthuunRaidsRule.FAMILIES.ToDictionary(f => f, f => qm.GetCurrentSolves(TthuunRaidsRule.KillQuest(f)));
            var paid = TthuunRaidsRule.FAMILIES.ToDictionary(f => f, f => qm.HasQuest(TthuunRaidsRule.PaidQuest(f)));
            double wait = qm.CanSolve(TthuunRaidsRule.TASK_WAIT) ? 0 : qm.GetNextSolveTime(TthuunRaidsRule.TASK_WAIT).TotalSeconds;
            var talk = TthuunRaidsRule.TaskTalk(kills, paid, active, wait);
            if (talk.start)
                foreach (var f in TthuunRaidsRule.FAMILIES) { qm.SetQuestCompletions(TthuunRaidsRule.KillQuest(f), 0); qm.Erase(TthuunRaidsRule.PaidQuest(f)); }
            foreach (var f in talk.pay)
            {
                qm.Stamp(TthuunRaidsRule.PaidQuest(f));
                p.GrantXP(TthuunRaidsRule.TASK_XP, XpType.Quest, ShareType.None);
                Give(p, TthuunRaidsRule.GRAND_CASINO_KEY);
            }
            if (talk.allDone)
            {
                foreach (var f in TthuunRaidsRule.FAMILIES) { qm.Erase(TthuunRaidsRule.KillQuest(f)); qm.Erase(TthuunRaidsRule.PaidQuest(f)); }
                qm.Stamp(TthuunRaidsRule.TASK_WAIT);
                AwardTitle(p, TthuunRaidsRule.TITLE_HERO);
            }
            lines.AddRange(talk.lines);
            var chain = new ActionChain();
            chain.AddDelaySeconds(6);   // after the scout's own five lines
            foreach (var line in lines)
            {
                var text = line;
                chain.AddAction(scout, () => p.Session?.Network?.EnqueueSend(new GameEventTell(scout, text, p, ChatMessageType.Tell)));
                chain.AddDelaySeconds(1.5);
            }
            chain.EnqueueChain();
        }

        static void AwardTitle(Player p, uint title)
        {
            try { p.AddTitle(title); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids: title {title}: {e.Message}"); }
        }

        static void Give(Player p, uint wcid)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return;
            if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); Say(p, "Your pack is full: the Royal Scout keeps your reward until you have room. Speak to him again."); return; }
            Say(p, $"The Royal Scout gives you {wo.Name}.");
        }

        // ---- helpers -------------------------------------------------------------------------------------------------------

        /// <summary>A landblock only if it is already loaded (LandblockManager.GetLandblock would load it; Frostfell.IfLoaded).</summary>
        static ACE.Server.Entity.Landblock IfLoaded(ushort block)
        {
            var id = new LandblockId((uint)block << 16 | 0xFFFF);
            return LandblockManager.IsLoaded(id) ? LandblockManager.GetLandblock(id, false) : null;
        }

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (verb == "reset")
            {
                foreach (var f in TthuunRaidsRule.FAMILIES) { me.QuestManager.Erase(TthuunRaidsRule.KillQuest(f)); me.QuestManager.Erase(TthuunRaidsRule.PaidQuest(f)); }
                me.QuestManager.Erase(TthuunRaidsRule.TASK_WAIT);
                me.QuestManager.Erase("TthuunRaidSparkKey");
                foreach (var b in TthuunRaidsRule.BossCredits) me.QuestManager.Erase(b.flag);
                Say(me, "[tthuun] your kill tasks, boss flags, Spark key timer and 6-day wait are cleared.");
                return;
            }
            var raid = s_Raid;
            Say(me, $"[tthuun] festival {(On ? "on" : "off")}; raid {(raid == null ? "none" : $"{raid.Site.Key} stage {raid.Stage} (wave 1 peak {raid.Wave1Peak}){(raid.Won ? " won" : "")}, {Live(raid.Wave).Count()} of this wave alive")}");
            var qm = me.QuestManager;
            Say(me, "[tthuun] you: " + string.Join(", ", TthuunRaidsRule.FAMILIES.Select(f => $"{f} {qm.GetCurrentSolves(TthuunRaidsRule.KillQuest(f))}{(qm.HasQuest(TthuunRaidsRule.PaidQuest(f)) ? " paid" : "")}"))
                + $"; wait {(qm.CanSolve(TthuunRaidsRule.TASK_WAIT) ? "none" : TthuunRaidsRule.Wait(qm.GetNextSolveTime(TthuunRaidsRule.TASK_WAIT).TotalSeconds))}"
                + "; flags " + string.Join(", ", TthuunRaidsRule.BossCredits.Where(b => qm.HasQuest(b.flag)).Select(b => b.flag).DefaultIfEmpty("none")));
        }
    }

    // ---- patches ---------------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TthuunRaidsTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { TthuunRaids.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] TthuunRaids: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { TthuunRaids.Tick(); }
            catch (Exception e) { TickGuard.Report("TthuunRaids", e); }
        }
    }

    [HarmonyPatch(typeof(GeneratorProfile), nameof(GeneratorProfile.ProcessQueue))]
    static class TthuunRaidsHoldWave
    {
        static bool Prefix(GeneratorProfile __instance)
        {
            try { return !TthuunRaids.HoldWave(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids hold: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch]
    static class TthuunRaidsTreasure
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            if (m == null) Mod.Log.Error("[RevivalGuard] TthuunRaids: Creature.GenerateTreasure(DamageHistoryInfo, Corpse) not found; raid creatures drop the Valley's stones");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Creature __instance, Corpse corpse)
        {
            try { TthuunRaids.OnTreasure(__instance, corpse); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids treasure: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class TthuunRaidsDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] TthuunRaids: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; no boss titles or Sparks");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Creature __instance)
        {
            try { TthuunRaids.OnDeath(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids death: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnUse))]
    static class TthuunRaidsScout
    {
        static void Postfix(EmoteManager __instance, Creature activator)
        {
            try
            {
                var wo = __instance?.WorldObject;
                if (wo?.WeenieClassId == TthuunRaidsRule.ROYAL_SCOUT && activator is Player p) TthuunRaids.OnScout(wo, p);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TthuunRaids scout: {e.Message}"); }
        }
    }
}
