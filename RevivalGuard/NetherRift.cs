using System.Numerics;
using System.Reflection;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NETHER RIFT, a new void spell (docs/NEW-ANIMATIONS.md, part 3). Ours, not retail. MADE, NOT
    /// ADDED, OWNER-APPROVED PERMANENT 2026-09-28: on by default (server property rg_nether_rift), and its world data
    /// (tools/gpubox-ace/nether-rift.sql) is not applied. Off, every patch below returns at once.
    ///
    /// The spell: void magic, level VII, a STRIKE. The caster tears the air open beside the target and
    /// a nether bolt lances out of the tear into it. War has strikes (ACE ProjectileSpellType.Strike:
    /// the projectile is born at the target, Black Spear Strike, MeteorStrike); void never had one.
    /// Every step is retail's pipeline and retail's objects, so a retail client sees a void cast (the
    /// purple Platinum windup, the Birch talisman's MagicRecoilMissile) and a retail Nether Bolt
    /// (weenie 43230) appearing at the target. Our client, opted in, sees the Nether Rend gesture in
    /// place of MagicRecoilMissile ("xover") and the tear the bolt comes out of ("xfx").
    ///
    /// What the data cannot say, done here:
    ///   the spell       ACE reads a spell's base from the DAT's SpellTable and we do not write DATs:
    ///                   when the DATs load (switch on) a SpellBase cloned from Nether Bolt VII (5355) is put in
    ///                   the loaded table under id 60001. ACE's spell row comes from the SQL.
    ///   a strike        SpellProjectile.GetProjectileSpellType picks the shape from the category, and no
    ///                   void category is a strike: a postfix answers Strike for 60001.
    ///   never on a      A retail client looks every spell id it is sent up in its own SpellTable, so
    ///   retail client   60001 must never reach one. It is NOT written to the spellbook: learning it
    ///                   stamps quest flag RevivalNetherRift instead (a LearnSpellWithNetworking prefix),
    ///                   SpellIsKnown answers from the stamp, the spell-bar favourite that would persist
    ///                   it in PlayerDescription is refused, and only an opted-in session is sent the
    ///                   MagicUpdateSpell that shows it in our client's spellbook.
    ///   the overlay     DoCastGesture postfix: "xover" to opted-in clients near the caster; the
    ///                   LaunchSpellProjectiles postfix: "xfx" with the projectile, target and caster.
    ///
    ///   @netherrift          status (admin)
    ///   @netherrift inject   put the spell in the loaded table now, without a restart (admin)
    ///   @netherrift teach    learn it yourself (admin; the test path)
    ///
    /// How a player learns it (owner 2026-09-28, "let players learn it from your recommended spot"):
    /// Riftwatcher Ysanne (weenie 901420, tools/gpubox-ace/nether_rift_trainer.py) at the Encampment
    /// near Rynthid Infested Plains. Using her runs TrainerUse: level 150+ with Void Magic trained
    /// learns it through LearnSpellWithNetworking (so the flag, never the spellbook); otherwise she
    /// says what is missing, or that it is already known. Her dialogue is here, not in emote data,
    /// so it can say "not while the spell is off" instead of teaching nothing.
    /// </summary>
    static class NetherRift
    {
        internal const uint SPELL = 60001;
        const uint TEMPLATE = 5355;                   // Nether Bolt VII: school, range, flags, effects
        const string P_ON = "rg_nether_rift";
        const string STAMP = "RevivalNetherRift";
        const float HEAR = 60f;
        internal const uint TRAINER = 901420;         // Riftwatcher Ysanne (nether-rift-trainer.sql)
        const int MIN_LEVEL = 150;
        internal static bool HookDats, HookType, HookGesture, HookLaunch, HookLearn, HookKnown, HookFavorite, HookTrainer;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> s_Talking = new();
        static readonly object s_Lock = new object();
        static bool s_Injected;

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;   // owner-approved permanent 2026-09-28
        static bool Live => s_Injected && On;

        internal static void Register()
        {
            ExpansionMotions.RegisterProperty(P_ON, true, "RevivalGuard NetherRift: the Nether Rift void strike, spell 60001 (docs/NEW-ANIMATIONS.md); needs tools/gpubox-ace/nether-rift.sql");
            CommandManager.TryAddCommand(Handle, "netherrift", AccessLevel.Admin, CommandHandlerFlag.None,
                "Nether Rift (RevivalGuard, on by default): status; 'inject' adds spell 60001 to the loaded spell table; 'teach' learns it.", "[inject|teach]");
            Mod.Log.Info($"[RevivalGuard] NetherRift: {(On ? "ON" : "off")} ({P_ON}); spell {SPELL} {(On ? "added when the DATs load" : "not loaded")}; "
                + $"hooks dats {Y(HookDats)} type {Y(HookType)} gesture {Y(HookGesture)} launch {Y(HookLaunch)} learn {Y(HookLearn)} known {Y(HookKnown)} favourite {Y(HookFavorite)} trainer {Y(HookTrainer)}");
        }

        static string Y(bool b) => b ? "ok" : "MISSING";

        static void Handle(Session session, params string[] parameters)
        {
            string msg;
            if (parameters.Length > 0 && parameters[0].Equals("inject", StringComparison.OrdinalIgnoreCase))
                msg = Inject() ? $"[NetherRift] spell {SPELL} is in the loaded spell table." : "[NetherRift] could not add the spell; see ACE_Log.txt.";
            else if (parameters.Length > 0 && parameters[0].Equals("teach", StringComparison.OrdinalIgnoreCase))
            {
                // How a player comes by it is the owner's call (docs/NEW-ANIMATIONS.md); a scroll is
                // out, since a retail client appraising one would be sent spell 60001. This is the test path.
                var p = session?.Player;
                if (p == null) msg = "[NetherRift] teach: in game only.";
                else if (!Live) msg = $"[NetherRift] teach: {P_ON} is off or the spell is not loaded.";
                else { p.LearnSpellWithNetworking(SPELL); msg = "[NetherRift] taught (quest flag " + STAMP + ")."; }
            }
            else
            {
                msg = $"[NetherRift] {P_ON} {(On ? "ON" : "off")}; spell {SPELL} {(s_Injected ? "in the table" : "not loaded")}; "
                    + $"row in ace_world.spell {(DatabaseManager.World.GetCachedSpell(SPELL) != null ? "present" : "MISSING (tools/gpubox-ace/nether-rift.sql)")}.";
                if (s_Injected)
                {
                    // ACE's own view of it: the checks a cast makes, without casting
                    var sp = new Spell(SPELL);
                    msg += sp.NotFound ? " ACE: NotFound." : $" ACE: '{sp.Name}', {sp.School}, level {sp.Level}, mana {sp.BaseMana}, power {sp.Power}, "
                        + $"range {sp.BaseRangeConstant}+{sp.BaseRangeMod}/lvl, damage {sp.BaseIntensity}-{sp.BaseIntensity + sp.Variance} {sp.DamageType}, "
                        + $"projectile wcid {sp.Wcid}, shape {SpellProjectile.GetProjectileSpellType(SPELL)}, words '{sp._spellBase.GetSpellWords(DatManager.PortalDat.SpellComponentsTable)}', "
                        + $"windup {string.Join("+", sp.Formula.WindupGestures)}, gesture {sp.Formula.CastGesture}.";
                }
            }
            if (session?.Network != null) session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
            else Mod.Log.Info(msg);
        }

        /// <summary>The spell's base, as retail's table shape has it: school, level, mana, range,
        /// components, target, flags. Cloned from Nether Bolt VII and then given its own identity.</summary>
        static bool Inject()
        {
            lock (s_Lock)
            {
                if (s_Injected) return true;
                try
                {
                    var table = DatManager.PortalDat.SpellTable.Spells;
                    if (table.ContainsKey(SPELL)) { s_Injected = true; return true; }
                    if (!table.TryGetValue(TEMPLATE, out var src)) { Mod.Log.Error($"[RevivalGuard] NetherRift: template spell {TEMPLATE} missing"); return false; }
                    var sb = (SpellBase)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(src, null);
                    var t = Traverse.Create(sb);
                    t.Property("Name").SetValue("Nether Rift");
                    t.Property("Desc").SetValue("Tears the air open beside the target. A bolt of nether lances out of the rift and does 150-240 points of nether damage to the target.");
                    t.Property("Icon").SetValue(0x06FF0600u);
                    t.Property("MetaSpellId").SetValue(SPELL);
                    t.Property("Power").SetValue(310u);                 // between Nether Bolt VII (300) and the curses VII (325)
                    t.Property("BaseMana").SetValue(50u);               // Bolt VII 35, Streak VII 70
                    t.Property("DisplayOrder").SetValue(8057u);         // after Nether Streak VII (8055) in the void list
                    // Platinum Scarab, Indigo Taper, Soulweed, Bottled Rage, Birch Talisman: "Traku Bor",
                    // words no retail void spell uses; the Birch talisman is every void bolt's (MagicRecoilMissile).
                    t.Property("Formula").SetValue(new List<uint> { 112, 70, 195, 197, 55 });
                    table[SPELL] = sb;
                    s_Injected = true;
                    Mod.Log.Info($"[RevivalGuard] NetherRift: spell {SPELL} added to the loaded spell table (from {TEMPLATE})");
                    return true;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] NetherRift: inject failed: {e.Message}"); return false; }
            }
        }

        /// <summary>ACE loads the DATs after the mods (Program.cs: ModManager.Initialize, then
        /// DatManager.Initialize), so the spell goes in once the table exists: a postfix on
        /// DatManager.Initialize, still single-threaded startup, before any world thread reads it.</summary>
        internal static void AfterDats() { if (On) Inject(); }

        /// <summary>From ExpansionMotions when a session opts in: show the spell in its spellbook.</summary>
        internal static void OnOptIn(Session s)
        {
            var p = s?.Player;
            if (!Live || p == null || !p.QuestManager.HasQuest(STAMP)) return;
            s.Network.EnqueueSend(new GameEventMagicUpdateSpell(s, (ushort)SPELL));
        }

        // ------------------------------------------------------------------ patches (all no-ops while off)

        internal static void TypePostfix(uint spellID, ref ProjectileSpellType __result)
        {
            if (spellID == SPELL && Live) __result = ProjectileSpellType.Strike;
        }

        internal static void GesturePostfix(Player p, Spell spell, WorldObject casterItem)
        {
            if (spell == null || spell.Id != SPELL || !Live || casterItem != null) return;
            string from = p.MagicState.CastGesture.ToString();
            ExpansionMotions.ToOptedInNear(p.Location, HEAR, "xover",
                "{\"g\":" + p.Guid.Full + ",\"from\":\"" + from + "\",\"to\":\"NetherRend\",\"ttl\":8}");
        }

        internal static void LaunchPostfix(WorldObject caster, Spell spell, WorldObject target, List<SpellProjectile> result)
        {
            if (spell == null || spell.Id != SPELL || !Live || result == null || result.Count == 0) return;
            var sp = result[0];
            ExpansionMotions.ToOptedInNear(sp.Location, HEAR, "xfx",
                "{\"k\":\"nether_rift\",\"p\":" + sp.Guid.Full + ",\"t\":" + (target?.Guid.Full ?? 0) + ",\"c\":" + caster.Guid.Full + "}");
        }

        /// <summary>Learning stamps a flag instead of writing the spellbook. False = ACE's own is skipped.</summary>
        internal static bool LearnPrefix(Player p, uint spellId, bool uiOutput)
        {
            if (spellId != SPELL) return true;
            if (!Live) return false;   // never let ACE's own write 60001 to a spellbook, switch or no switch
            if (p.QuestManager.HasQuest(STAMP))
            {
                if (uiOutput) p.Session.Network.EnqueueSend(new GameMessageSystemChat("You already know that spell!", ChatMessageType.Broadcast));
                return false;
            }
            p.QuestManager.Stamp(STAMP);
            if (ExpansionMotions.OptedIn(p.Session)) p.Session.Network.EnqueueSend(new GameEventMagicUpdateSpell(p.Session, (ushort)SPELL));
            if (uiOutput)
            {
                p.ApplyVisualEffects(PlayScript.SkillUpPurple);
                p.Session.Network.EnqueueSend(new GameMessageSystemChat("You learn the Nether Rift spell.\n", ChatMessageType.Broadcast));
            }
            return false;
        }

        internal static void KnownPostfix(Player p, uint spellId, ref bool __result)
        {
            if (!__result && spellId == SPELL && Live && p.QuestManager.HasQuest(STAMP)) __result = true;
        }

        /// <summary>A favourite is saved on the character and sent in PlayerDescription to whatever
        /// client logs in next. Never for 60001; our client keeps its own bar entry.</summary>
        internal static bool FavoritePrefix(uint spellId) => spellId != SPELL;

        // ------------------------------------------------------------------ the trainer

        /// <summary>Creature.ActOnUse postfix (the NPC has no emote data, so EmoteManager.OnUse did
        /// nothing before this). Turns to the player, then speaks as a retail NPC does: Tells, about
        /// two seconds apart (the Professor of Void Magic's own spacing, weenie 53385).</summary>
        internal static void TrainerUse(Creature npc, WorldObject activator)
        {
            if (npc == null || npc.WeenieClassId != TRAINER || activator is not Player p || p.Session == null) return;
            var now = DateTime.UtcNow;
            if (s_Talking.TryGetValue(p.Guid.Full, out var until) && until > now) return;   // mid-conversation

            bool levelOk = (p.Level ?? 1) >= MIN_LEVEL;
            var void_ = p.GetCreatureSkill(Skill.VoidMagic);
            bool voidOk = void_ != null && void_.AdvancementClass >= SkillAdvancementClass.Trained;
            bool known = p.QuestManager.HasQuest(STAMP);

            string[] lines;
            bool teach = false;
            if (!Live)
                lines = new[] { "The Rifts are quiet today, and I cannot show you what is not there to be seen. Come back another time." };
            else if (known)
                lines = new[] { "You already know how to open the Rift. I have nothing more to teach you.",
                                "Mind what comes through, each time you tear it." };
            else if (!levelOk && !voidOk)
                lines = new[] { "The Rift is not a door for the unready. I teach it only to those of level 150 or greater who have trained Void Magic." };
            else if (!levelOk)
                lines = new[] { "The Void answers you, but the Rifts would swallow you whole.",
                                $"Return when you have reached level {MIN_LEVEL}, and I will teach you." };
            else if (!voidOk)
                lines = new[] { "You are strong, but the Void does not answer you. I can only teach one who has trained Void Magic.",
                                "Train it, and then we will speak." };
            else
            {
                teach = true;
                lines = new[] { "You feel it too. The air here is thin. The Rynthid tear it open like wet parchment and step through.",
                                "I have watched their Rifts for many seasons. What they do by instinct, one who walks the Void may do by will.",
                                "Hold your focus on the space beside your foe. Grip the nothing there, and pull." };
            }

            float delay = npc.Rotate(p);
            s_Talking[p.Guid.Full] = now.AddSeconds(delay + 2.0 * lines.Length + 1.5);
            var chain = new ActionChain();
            chain.AddDelaySeconds(Math.Min(delay, 2f));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) chain.AddDelaySeconds(2.0);
                var line = lines[i];
                chain.AddAction(npc, () => { if (p.Session != null) p.Session.Network.EnqueueSend(new GameEventTell(npc, line, p, ChatMessageType.Tell)); });
            }
            if (teach)
            {
                chain.AddDelaySeconds(1.0);
                chain.AddAction(npc, () =>
                {
                    if (p.Session == null || !Live || p.QuestManager.HasQuest(STAMP)) return;
                    p.LearnSpellWithNetworking(SPELL);   // LearnPrefix: the flag, the purple sparkle, "You learn the Nether Rift spell."
                    p.Session.Network.EnqueueSend(new GameEventTell(npc, "Let the Darkness fill your mind. Use it sparingly; every tear is a door, and not every door stays empty.", p, ChatMessageType.Tell));
                    Mod.Log.Info($"[RevivalGuard] NetherRift: {p.Name} (level {p.Level}) learned Nether Rift from the trainer");
                });
            }
            chain.EnqueueChain();
        }
    }

    [HarmonyPatch]
    static class NetherRiftDats
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(DatManager), nameof(DatManager.Initialize), new[] { typeof(string), typeof(bool), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: DatManager.Initialize(string, bool, bool) not found; @netherrift inject adds the spell");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookDats = TargetMethod() != null);
        static void Postfix()
        {
            try { NetherRift.AfterDats(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: after DATs: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftType
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(SpellProjectile), nameof(SpellProjectile.GetProjectileSpellType), new[] { typeof(uint) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: SpellProjectile.GetProjectileSpellType(uint) not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookType = TargetMethod() != null);
        static void Postfix(uint spellID, ref ProjectileSpellType __result)
        {
            try { NetherRift.TypePostfix(spellID, ref __result); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: type: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftGesture
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.DoCastGesture), new[] { typeof(Spell), typeof(WorldObject), typeof(ActionChain) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: Player.DoCastGesture(Spell, WorldObject, ActionChain) not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookGesture = TargetMethod() != null);
        static void Postfix(Player __instance, Spell spell, WorldObject casterItem)
        {
            try { NetherRift.GesturePostfix(__instance, spell, casterItem); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: gesture: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftLaunch
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(WorldObject), nameof(WorldObject.LaunchSpellProjectiles), new[] { typeof(Spell), typeof(WorldObject),
                typeof(ProjectileSpellType), typeof(WorldObject), typeof(bool), typeof(bool), typeof(List<Vector3>), typeof(Vector3), typeof(uint) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: WorldObject.LaunchSpellProjectiles not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookLaunch = TargetMethod() != null);
        static void Postfix(WorldObject __instance, Spell spell, WorldObject target, List<SpellProjectile> __result)
        {
            try { NetherRift.LaunchPostfix(__instance, spell, target, __result); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: launch: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftLearn
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.LearnSpellWithNetworking), new[] { typeof(uint), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: Player.LearnSpellWithNetworking(uint, bool) not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookLearn = TargetMethod() != null);
        static bool Prefix(Player __instance, uint spellId, bool uiOutput)
        {
            try { return NetherRift.LearnPrefix(__instance, spellId, uiOutput); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: learn: {e.Message}"); return spellId != NetherRift.SPELL; }
        }
    }

    [HarmonyPatch]
    static class NetherRiftKnown
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.SpellIsKnown), new[] { typeof(uint) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: Player.SpellIsKnown(uint) not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookKnown = TargetMethod() != null);
        static void Postfix(Player __instance, uint spellId, ref bool __result)
        {
            try { NetherRift.KnownPostfix(__instance, spellId, ref __result); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: known: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftTrainer
    {
        // Creature DECLARES its ActOnUse override (Creature.cs: "handled in base.OnActivate ->
        // EmoteManager.OnUse()"), so it is the type to patch; the wcid filter is inside.
        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.ActOnUse), new[] { typeof(WorldObject) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: Creature.ActOnUse(WorldObject) not found; the trainer is silent");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookTrainer = TargetMethod() != null);
        static void Postfix(Creature __instance, WorldObject worldObject)
        {
            try { NetherRift.TrainerUse(__instance, worldObject); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NetherRift: trainer: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class NetherRiftFavorite
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.HandleActionAddSpellFavorite), new[] { typeof(uint), typeof(uint), typeof(uint) });
            if (m == null) Mod.Log.Error("[RevivalGuard] NetherRift: Player.HandleActionAddSpellFavorite(uint, uint, uint) not found");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (NetherRift.HookFavorite = TargetMethod() != null);
        static bool Prefix(uint spellId) => NetherRift.FavoritePrefix(spellId);
    }
}
