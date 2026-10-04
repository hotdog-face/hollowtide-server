using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE FORGE-WARDEN OF ISPAR (weenie 900300), the boss of the Empyrean Vault: season beat 4,
    /// docs/VAULT-AND-FORGE-WARDEN.md. Owner-confirmed 2026-09-27. NOT RETAIL: a new boss, so its
    /// chat lines are ours (labelled nowhere as retail) and carry no em dashes.
    ///
    /// Three mechanics on top of an ordinary ACE monster, all driven from its own tick:
    ///   FORGE SUMMONS  every forge_warden_summon_s while awake, an Aetherium Golem (the family's own
    ///                  900211-900213, heavier as its health falls) steps out of an Empyrean Forge
    ///                  (900321) in the room and goes for the Warden's target; at most
    ///                  forge_warden_max_adds alive.
    ///   CRYSTAL SHIELD at forge_warden_shield_1 and _2 of its health it spawns a Forge Crystal
    ///                  (900322) at every forge and takes no damage until all of them are broken
    ///                  (TakeDamage prefix for weapons, a heal-back each tick for everything else).
    ///   OVERLOAD PULSE under forge_warden_overload it warns, waits forge_warden_pulse_warn_s, then
    ///                  hits every player in the room it can SEE (ACE's own IsDirectVisible, which the
    ///                  Ward-Statues 900323 block) for forge_warden_pulse_frac of their max health;
    ///                  again every forge_warden_pulse_s.
    /// On death every player in its damage history gets the retail title Golem Slayer (248) and the
    /// quest stamp ForgeWardenSlain; the trophy is TrophyMounts' job. When it goes back to sleep the
    /// adds and crystals are removed and the phases reset.
    ///
    /// Hooks, all declared on Creature (AGENTS.md): Monster_Tick postfix, TakeDamage prefix, OnDeath
    /// postfix, Sleep postfix. Every hook returns at once unless the creature is weenie 900300 (or one
    /// of its crystals), and an exception is logged, never thrown. Registered from Prepare, so Mod.cs
    /// is not touched. Admin command @forgewarden (status | hp &lt;percent&gt; | reset | pulse | kill).
    /// </summary>
    static class ForgeWarden
    {
        internal const uint WARDEN = 900300, FORGE = 900321, CRYSTAL = 900322, STATUE = 900323, TROPHY = 900345;
        internal const uint TITLE_GOLEM_SLAYER = 248;           // CharacterTitle.GolemSlayer, unused by retail content
        internal const string SLAIN_QUEST = "ForgeWardenSlain";
        static readonly uint[] SUMMONS = { 900211, 900212, 900213 };   // Copper, Bronze, Iron Aetherium Golems

        const string P_ON = "forge_warden_enabled";
        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("forge_warden_summon_s", 40, "ForgeWarden: seconds between forge summons"),
            ("forge_warden_max_adds", 6, "ForgeWarden: summoned golems alive at once"),
            ("forge_warden_shield_1", 0.70, "ForgeWarden: health fraction of the first crystal shield"),
            ("forge_warden_shield_2", 0.40, "ForgeWarden: health fraction of the second crystal shield"),
            ("forge_warden_overload", 0.20, "ForgeWarden: health fraction where the overload pulse starts"),
            ("forge_warden_pulse_s", 25, "ForgeWarden: seconds between overload pulses"),
            ("forge_warden_pulse_warn_s", 5, "ForgeWarden: warning before each pulse, seconds"),
            ("forge_warden_pulse_frac", 0.6, "ForgeWarden: pulse damage as a fraction of max health"),
            ("forge_warden_room_m", 45, "ForgeWarden: radius of the room the pulse and summons use, metres"),
        };

        sealed class St
        {
            public double NextThink, NextSummon, NextPulse, PulseAt, NextShieldFx, NextAbsorbNote;
            public bool Woke, Shielded, Overload;
            public int ShieldsDone;
            public uint ShieldHp;
            public readonly List<WorldObject> Adds = new List<WorldObject>();
            public readonly List<WorldObject> Crystals = new List<WorldObject>();
        }
        static readonly ConditionalWeakTable<Creature, St> s_State = new ConditionalWeakTable<Creature, St>();
        static int s_Registered, s_Summons, s_Shields, s_Pulses, s_Kills;

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "ForgeWarden: the Forge-Warden's scripted fight (summons, shields, overload)");
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden: switches not registered with @modify* ({e.Message}); defaults apply"); }
            // The trophy: TrophyMounts inscribes a boss's trophy with its killer. Its table is private and
            // other sessions edit that file, so the Warden's row is added here rather than there.
            try
            {
                if (AccessTools.Field(typeof(TrophyMounts), "s_Boss")?.GetValue(null) is Dictionary<uint, uint> bosses)
                    bosses[WARDEN] = TROPHY;
                else Mod.Log.Warn("[RevivalGuard] ForgeWarden: TrophyMounts table not found; the Warden's trophy drops uninscribed");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden: trophy row not added ({e.Message})"); }
            CommandManager.TryAddCommand(Handle, "forgewarden", AccessLevel.Admin, CommandHandlerFlag.None,
                "The Forge-Warden of Ispar (RevivalGuard): state of the nearest Warden, or set its health to test a phase.",
                "[status | hp <percent> | reset | pulse | kill]");
            Mod.Log.Info("[RevivalGuard] ForgeWarden: forge summons, crystal shields, overload pulse (weenie 900300, forge_warden_* properties, @forgewarden)");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static double P(int i) => PropertyManager.GetDouble(s_Doubles[i].key, s_Doubles[i].def).Item;
        static double SummonS => P(0); static int MaxAdds => (int)P(1);
        static double Shield1 => P(2); static double Shield2 => P(3); static double OverloadAt => P(4);
        static double PulseS => P(5); static double PulseWarnS => P(6); static double PulseFrac => P(7); static float RoomM => (float)P(8);

        // ------------------------------------------------------------------ the tick
        internal static void Tick(Creature w)
        {
            if (w.WeenieClassId != WARDEN || w.IsDead || !w.IsAwake || !On) return;
            var st = s_State.GetOrCreateValue(w);
            double now = Time.GetUnixTime();

            // The shield is enforced every tick: whatever got past TakeDamage (spells, DoTs) is healed back.
            if (st.Shielded && w.Health.Current < st.ShieldHp)
                w.UpdateVital(w.Health, st.ShieldHp);

            if (now < st.NextThink) return;
            st.NextThink = now + 0.5;

            if (!st.Woke)
            {
                st.Woke = true;
                st.NextSummon = now + 12;
                Announce(w, "The Forge-Warden of Ispar wakes. Its forges roar to life around it.");
            }

            float hp = w.Health.MaxValue > 0 ? (float)w.Health.Current / w.Health.MaxValue : 1f;
            st.Adds.RemoveAll(a => a == null || a.IsDestroyed || (a is Creature c && c.IsDead));
            st.Crystals.RemoveAll(a => a == null || a.IsDestroyed || (a is Creature c && c.IsDead));

            // CRYSTAL SHIELD
            if (st.Shielded)
            {
                if (st.Crystals.Count == 0) DropShield(w, st);
                else if (now >= st.NextShieldFx)
                {
                    st.NextShieldFx = now + 4;
                    w.EnqueueBroadcast(new GameMessageScript(w.Guid, PlayScript.ShieldUpPurple));
                }
            }
            else if (st.ShieldsDone < 2 && hp <= (st.ShieldsDone == 0 ? Shield1 : Shield2))
                RaiseShield(w, st, now);

            // FORGE SUMMONS
            if (now >= st.NextSummon)
            {
                st.NextSummon = now + Math.Max(10, SummonS);
                if (st.Adds.Count < MaxAdds) Summon(w, st, hp);
            }

            // OVERLOAD PULSE
            if (!st.Shielded && hp <= OverloadAt)
            {
                if (!st.Overload)
                {
                    st.Overload = true; st.NextPulse = now;
                    Announce(w, "Cracks of white fire split the Forge-Warden's core. It is going to overload!");
                }
                if (st.PulseAt == 0 && now >= st.NextPulse)
                {
                    st.PulseAt = now + PulseWarnS;
                    Announce(w, "The Forge-Warden's core blazes. Get behind the statues!");
                    w.EnqueueBroadcast(new GameMessageScript(w.Guid, PlayScript.ShieldUpRed));
                }
                else if (st.PulseAt > 0 && now >= st.PulseAt)
                {
                    st.PulseAt = 0; st.NextPulse = now + Math.Max(8, PulseS);
                    Pulse(w);
                }
            }
        }

        static IEnumerable<WorldObject> Forges(Creature w)
        {
            var lb = w.CurrentLandblock;
            if (lb == null) return Enumerable.Empty<WorldObject>();
            return lb.GetAllWorldObjectsForDiagnostics()
                .Where(o => o.WeenieClassId == FORGE && o.Location != null && Dist(o, w) <= RoomM);
        }

        static float Dist(WorldObject a, WorldObject b) =>
            a.Location.Landblock == b.Location.Landblock ? Vector3.Distance(a.Location.Pos, b.Location.Pos) : float.MaxValue;

        /// <summary>A point <paramref name="step"/> metres from a forge towards the Warden's home, in
        /// the forge's own cell (the forge room's corner cells are 10 m square).</summary>
        static ACE.Entity.Position InFrontOf(WorldObject forge, Creature w, float step)
        {
            var home = w.Home ?? w.Location;
            var f = forge.Location.Pos;
            var dir = new Vector3(home.PositionX - f.X, home.PositionY - f.Y, 0);
            if (dir.LengthSquared() < 0.01f) dir = Vector3.UnitY;
            var p = f + Vector3.Normalize(dir) * step;
            return new ACE.Entity.Position(forge.Location.Cell, p.X, p.Y, f.Z + 0.05f, 0, 0, 0, 1);
        }

        static void Summon(Creature w, St st, float hp)
        {
            var forges = Forges(w).ToList();
            if (forges.Count == 0) return;
            var forge = forges[ThreadSafeRandom.Next(0, forges.Count - 1)];
            uint wcid = SUMMONS[hp > 0.66f ? 0 : hp > 0.33f ? 1 : 2];
            if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature g)) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden: weenie {wcid} missing; no summon"); return; }
            g.Location = InFrontOf(forge, w, 3.0f);
            if (!g.EnterWorld()) { g.Destroy(); return; }
            st.Adds.Add(g);
            s_Summons++;
            forge.EnqueueBroadcast(new GameMessageScript(forge.Guid, PlayScript.Create));
            if (w.AttackTarget is Creature target && !target.IsDead)
            {
                g.AttackTarget = target;
                g.WakeUp(false);
            }
            Announce(w, $"The Empyrean Forge flares, and a {g.Name} steps out of the fire.");
        }

        static void RaiseShield(Creature w, St st, double now)
        {
            st.ShieldsDone++;
            int made = 0;
            foreach (var forge in Forges(w))
            {
                var c = WorldObjectFactory.CreateNewWorldObject(CRYSTAL);
                if (c == null) break;
                c.Location = InFrontOf(forge, w, 2.0f);
                if (!c.EnterWorld()) { c.Destroy(); continue; }
                st.Crystals.Add(c); made++;
            }
            if (made == 0) { Mod.Log.Warn("[RevivalGuard] ForgeWarden: no forge crystal could be placed; shield skipped"); return; }
            st.Shielded = true; st.ShieldHp = w.Health.Current; st.NextShieldFx = now; s_Shields++;
            Announce(w, $"The Forge-Warden draws on its forges and a shield of runes closes around it. Break the {made} Forge Crystals!");
        }

        static void DropShield(Creature w, St st)
        {
            st.Shielded = false;
            w.EnqueueBroadcast(new GameMessageScript(w.Guid, PlayScript.ShieldDownPurple));
            Announce(w, "The last Forge Crystal shatters. The Forge-Warden's shield falls!");
        }

        static void Pulse(Creature w)
        {
            s_Pulses++;
            w.EnqueueBroadcast(new GameMessageScript(w.Guid, PlayScript.AetheriaSurgeDestruction));
            int hit = 0, safe = 0;
            var statues = w.CurrentLandblock?.GetAllWorldObjectsForDiagnostics().Where(o => o.WeenieClassId == STATUE && o.Location != null).ToList() ?? new List<WorldObject>();
            foreach (var p in PlayersInRoom(w))
            {
                if (p.IsDead) continue;   // an @neversaydie admin is still told (TakeDamage deals it 0)
                if (!BehindStatue(w, p, statues) && w.IsDirectVisible(p))
                {
                    float dmg = (float)(PulseFrac * p.Health.MaxValue);
                    int dealt = p.TakeDamage(w, DamageType.Electric, dmg, BodyPart.Chest);
                    p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.HealthDownRed));
                    p.SendMessage($"The Forge-Warden's overload pulse sears you for {dealt} points of damage!", ChatMessageType.Combat);
                    hit++;
                }
                else
                {
                    p.SendMessage("You shelter behind the stone as the overload pulse breaks around you.", ChatMessageType.Combat);
                    safe++;
                }
            }
            Mod.Log.Info($"[RevivalGuard] ForgeWarden: overload pulse, {hit} hit, {safe} in cover");
        }

        /// <summary>COVER. ACE's IsDirectVisible did not stop at the Ward-Statues on staging (a bot 2.8 m
        /// behind one, dead in line with the Warden, was hit), so the statues are also tested here:
        /// a statue whose footprint (its cylinder, r 0.5 x scale, plus 0.3 m) crosses the line from the
        /// Warden to the player on the floor plane is cover.</summary>
        static bool BehindStatue(Creature w, Player p, List<WorldObject> statues)
        {
            var a = new Vector2(w.Location.Pos.X, w.Location.Pos.Y);
            var b = new Vector2(p.Location.Pos.X, p.Location.Pos.Y);
            var ab = b - a; float len2 = ab.LengthSquared();
            if (len2 < 0.01f) return false;
            foreach (var s in statues)
            {
                if (s.Location.Landblock != w.Location.Landblock) continue;
                var c = new Vector2(s.Location.Pos.X, s.Location.Pos.Y);
                float t = Math.Clamp(Vector2.Dot(c - a, ab) / len2, 0f, 1f);
                float r = 0.5f * (s.ObjScale ?? 1f) + 0.3f;
                if (t > 0.05f && t < 0.98f && Vector2.DistanceSquared(a + ab * t, c) <= r * r) return true;
            }
            return false;
        }

        static IEnumerable<Player> PlayersInRoom(Creature w) =>
            PlayerManager.GetAllOnline().Where(p => p.Location != null && p.CurrentLandblock == w.CurrentLandblock && Dist(p, w) <= RoomM);

        static void Announce(Creature w, string text)
        {
            foreach (var p in PlayersInRoom(w))
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        static void Cleanup(St st)
        {
            // DEFERRED, NEVER INLINE. Destroying an add from inside the Warden's tick or Sleep took the
            // staging shard down (2026-09-28): the landblock's parallel tick had already listed the
            // add, ran its Monster_Tick after Destroy nulled its physics, and UpdatePosition threw an
            // unhandled NullReferenceException on the world thread. An ActionChain runs at a safe point.
            foreach (var o in st.Adds.Concat(st.Crystals).ToList())
            {
                if (o == null || o.IsDestroyed || (o is Creature c && c.IsDead)) continue;
                var chain = new ActionChain();
                chain.AddAction(o, () => { if (!o.IsDestroyed) o.Destroy(); });
                chain.EnqueueChain();
            }
            st.Adds.Clear(); st.Crystals.Clear();
            st.Shielded = false; st.Overload = false; st.PulseAt = 0; st.ShieldsDone = 0; st.Woke = false;
        }

        // ------------------------------------------------------------------ hooks
        internal static bool AbsorbDamage(Creature c, ref float amount, WorldObject source)
        {
            if (c.WeenieClassId != WARDEN || !s_State.TryGetValue(c, out var st) || !st.Shielded) return false;
            amount = 0;
            double now = Time.GetUnixTime();
            if (now >= st.NextAbsorbNote && source is Player pl)
            {
                st.NextAbsorbNote = now + 6;
                pl.SendMessage("Your blow is swallowed by the Forge-Warden's rune shield. Break the Forge Crystals!", ChatMessageType.Combat);
            }
            return true;
        }

        static readonly HashSet<uint> s_Dead = new HashSet<uint>();

        internal static void OnDeath(Creature w, DamageHistoryInfo lastDamager)
        {
            if (w.WeenieClassId != WARDEN) return;
            lock (s_Dead) { if (!s_Dead.Add(w.Guid.Full)) return; }   // ACE may enter OnDeath twice
            s_Kills++;
            if (s_State.TryGetValue(w, out var st)) Cleanup(st);
            Announce(w, "The Forge-Warden of Ispar crashes to the floor. Its core flickers and goes dark.");
            var credited = new HashSet<uint>();
            foreach (var info in w.DamageHistory.Damagers.Append(lastDamager))
            {
                var p = info?.TryGetPetOwnerOrAttacker() as Player;
                if (p == null || !credited.Add(p.Guid.Full)) continue;
                p.QuestManager.Stamp(SLAIN_QUEST);
                p.AddTitle(TITLE_GOLEM_SLAYER);
                p.SendMessage("You have helped destroy the Forge-Warden of Ispar.", ChatMessageType.Broadcast);
            }
            Mod.Log.Info($"[RevivalGuard] ForgeWarden: slain ({s_Kills} this run); {credited.Count} players credited");
        }

        internal static void OnSleep(Creature w)
        {
            if (w.WeenieClassId != WARDEN || !s_State.TryGetValue(w, out var st)) return;
            if (st.Adds.Count + st.Crystals.Count > 0 || st.Woke)
                Mod.Log.Info($"[RevivalGuard] ForgeWarden: reset (went to sleep); {st.Adds.Count} adds and {st.Crystals.Count} crystals removed");
            Cleanup(st);
        }

        // ------------------------------------------------------------------ @forgewarden
        static void Say(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Handle(Session session, params string[] parameters)
        {
            var me = session?.Player;
            var w = me?.CurrentLandblock?.GetAllWorldObjectsForDiagnostics().OfType<Creature>()
                .Where(c => c.WeenieClassId == WARDEN && !c.IsDead).OrderBy(c => Dist(c, me)).FirstOrDefault();
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";
            if (w == null) { Say(session, $"[ForgeWarden] no living Warden in this landblock (ours, not retail). Run: {s_Summons} summons, {s_Shields} shields, {s_Pulses} pulses, {s_Kills} kills."); return; }
            var st = s_State.GetOrCreateValue(w);
            switch (verb)
            {
                case "hp":
                    if (parameters.Length < 2 || !float.TryParse(parameters[1], out var pct)) { Say(session, "[ForgeWarden] usage: @forgewarden hp <percent>"); return; }
                    w.UpdateVital(w.Health, (uint)Math.Max(1, w.Health.MaxValue * Math.Clamp(pct, 0.1f, 100f) / 100f));
                    if (st.Shielded) st.ShieldHp = w.Health.Current;
                    Say(session, $"[ForgeWarden] health set to {w.Health.Current:N0} of {w.Health.MaxValue:N0}");
                    return;
                case "reset":
                    Cleanup(st); w.SetMaxVitals();
                    Say(session, "[ForgeWarden] reset: adds and crystals removed, full health, phases cleared");
                    return;
                case "kill":
                    Say(session, "[ForgeWarden] smiting the Warden (death, loot, trophy and title as a real kill)");
                    w.Smite(me, true); return;   // through TakeDamage, so the kill is credited like a real one
                case "pulse":
                    Pulse(w); Say(session, "[ForgeWarden] pulse fired"); return;
            }
            Say(session, $"[ForgeWarden] {w.Name} 0x{w.Guid.Full:X8}: {w.Health.Current:N0}/{w.Health.MaxValue:N0} ({100f * w.Health.Current / Math.Max(1, w.Health.MaxValue):0.#}%), awake {w.IsAwake}, shield {(st.Shielded ? $"UP ({st.Crystals.Count} crystals)" : "down")} after {st.ShieldsDone} of 2, overload {st.Overload}, adds {st.Adds.Count}, forges {Forges(w).Count()}, target {w.AttackTarget?.Name ?? "none"}");
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class ForgeWardenTick
    {
        static int s_Errors;
        static bool Prepare() { ForgeWarden.Register(); return true; }
        static void Postfix(Creature __instance)
        {
            if (__instance.WeenieClassId != ForgeWarden.WARDEN) return;
            try { ForgeWarden.Tick(__instance); }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref s_Errors) <= 5 || s_Errors % 1000 == 0)
                    Mod.Log.Error($"[RevivalGuard] ForgeWarden tick threw (error {s_Errors})", e);
            }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(bool) })]
    static class ForgeWardenAbsorb
    {
        static void Prefix(Creature __instance, WorldObject source, ref float amount)
        {
            if (__instance.WeenieClassId != ForgeWarden.WARDEN) return;
            try { ForgeWarden.AbsorbDamage(__instance, ref amount, source); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden absorb: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class ForgeWardenDeath
    {
        // A PREFIX: Creature.OnDeath resets the damage history on its way out, so a postfix saw no
        // damagers (staging, 2026-09-28: "0 damagers credited").
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            if (__instance.WeenieClassId != ForgeWarden.WARDEN) return;
            try { ForgeWarden.OnDeath(__instance, lastDamager); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden death hook: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Sleep))]
    static class ForgeWardenSleep
    {
        static void Postfix(Creature __instance)
        {
            if (__instance.WeenieClassId != ForgeWarden.WARDEN) return;
            try { ForgeWarden.OnSleep(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForgeWarden sleep hook: {e.Message}"); }
        }
    }
}
