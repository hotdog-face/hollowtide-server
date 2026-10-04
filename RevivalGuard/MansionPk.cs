using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE PK SIDE OF A MANSION (owner, 2026-09-27: "i want corpses to last longer on mansion lawns.
    /// rival allegiances do mansion raids frequently and the corpses become battle trophies and
    /// evidence of an active PK allegiance. open to recommendations to enhance the pk dynamic").
    /// Ours; docs/MANSIONS-AND-PK.md lists the recommendations that were not built.
    ///
    /// LAWN CORPSES. A player who dies on a mansion's landblock (lawn, yard, the house, and its
    /// basement block) leaves a corpse that lasts mansion_corpse_decay_seconds (a server property,
    /// default 86400 = 24 h; @modifylong changes it) of WALL-CLOCK time, empty or not, looted or not,
    /// across restarts. ACE normally gives a player corpse max(1 h, level x 5 min) of ticking time and
    /// drops an empty one to 15 s (WorldObject.Decay). The corpse carries its expiry (PropertyInt64
    /// 29112, ours); the Decay prefix keeps TimeToRot at what is left until the last 15 s, then lets
    /// ACE rot it the usual way. corpse_spam_limit is still honoured: when ACE cuts the oldest corpse
    /// of a repeat victim to 15 s, that cut stands. The name ("Corpse of X") and the appraisal line
    /// ("Killed by Y.") are ACE's own, retail's words.
    ///
    /// THE KILL BOARD. Every player killed by a player on those grounds is recorded against the
    /// mansion (mansions.json, last 60) and shown on the Allegiance Hall Board and by /mansion kills;
    /// the defending allegiance is told at once.
    ///
    /// THE RAID ALARM. Every 3 s the world loop (PlayerManager.Tick, which runs even where a
    /// landblock is quiet) looks at where each online PK is. One who steps onto a mansion's grounds
    /// and is not of the owner's allegiance (or account) raises one Allegiance line to every online
    /// member of the defending allegiance, at most once per intruder per mansion per 10 minutes.
    /// </summary>
    internal static class MansionPk
    {
        internal const string DECAY_PROPERTY = "mansion_corpse_decay_seconds";
        const long DECAY_DEFAULT = 86400;
        internal const PropertyInt64 TROPHY_UNTIL = (PropertyInt64)29112;
        const double ALARM_EVERY = 3.0, ALARM_REPEAT = 600.0;

        /// <summary>Make mansion_corpse_decay_seconds an ordinary ACE property: ModifyLong refuses a key
        /// that is not in DefaultLongProperties, a ReadOnlyDictionary over a private dictionary, so the
        /// key is added to that inner dictionary. If that fails the property is still read (GetLong
        /// reads config_properties_long by name); only @modifylong would refuse it.</summary>
        internal static void RegisterProperty()
        {
            try
            {
                var ro = DefaultPropertyManager.DefaultLongProperties;
                var inner = typeof(ReadOnlyDictionary<string, Property<long>>)
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<long>>>().FirstOrDefault();
                if (inner == null) throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
                if (!inner.ContainsKey(DECAY_PROPERTY))
                    inner[DECAY_PROPERTY] = new Property<long>(DECAY_DEFAULT, "seconds a player's corpse lasts on a mansion's grounds (RevivalGuard)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionPk: {DECAY_PROPERTY} not registered with @modifylong ({e.Message}); it is still read"); }
        }

        static long DecaySeconds() => PropertyManager.GetLong(DECAY_PROPERTY, DECAY_DEFAULT).Item;

        // ------------------------------------------------------------ the corpse, at death

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.RecalculateDecayTime))]
        static class AtDeath
        {
            static void Postfix(Corpse __instance, Player player)
            {
                try { OnPlayerCorpse(__instance, player); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionPk: corpse of {player?.Name}: {e}"); }
            }
        }

        static void OnPlayerCorpse(Corpse corpse, Player victim)
        {
            var loc = corpse.Location;
            if (loc == null || victim == null) return;
            uint root = MansionIndex.MansionRootAt(loc.LandblockId.Landblock);
            if (root == 0) return;

            long secs = DecaySeconds();
            if (secs > 0)
            {
                long until = (long)ACE.Common.Time.GetUnixTime() + secs;
                corpse.SetProperty(TROPHY_UNTIL, until);
                corpse.TimeToRot = Math.Max(corpse.TimeToRot ?? 0, secs);
                Mod.Log.Info($"[RevivalGuard] MansionPk: {corpse.Name} (0x{corpse.Guid.Full:X8}) lies on the grounds of mansion 0x{root:X8}; it lasts {secs} s");
            }

            uint killerGuid = corpse.KillerId ?? 0;
            if (killerGuid == 0 || killerGuid == victim.Guid.Full || !new ObjectGuid(killerGuid).IsPlayer()) return;
            var killer = PlayerManager.FindByGuid(killerGuid);
            if (killer == null) return;
            var killerAlleg = AllegianceManager.GetAllegiance(killer);
            var rec = new KillRec
            {
                At = (long)ACE.Common.Time.GetUnixTime(), Victim = victim.Name, Killer = killer.Name,
                KillerAllegiance = killerAlleg != null ? Mansions.AllegianceLabel(killerAlleg) : null,
                Where = Mansions.Where(loc),
            };
            Store.KillAdd(root, rec);
            uint owner = MansionIndex.OwnerOf(root);
            if (owner == 0) return;
            string of = rec.KillerAllegiance != null ? $" of {rec.KillerAllegiance}" : "";
            foreach (var m in Mansions.Defenders(owner))
                Mansions.Say(m, $"{victim.Name} was killed by {killer.Name}{of} on the grounds of your mansion.", ChatMessageType.Allegiance);
        }

        // ------------------------------------------------------------ the corpse, as it lies there

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Decay))]
        static class Lasts
        {
            static bool Prefix(WorldObject __instance)
            {
                if (!(__instance is Corpse c)) return true;
                var until = c.GetProperty(TROPHY_UNTIL);
                if (until == null || !c.InventoryLoaded) return true;
                // corpse_spam_limit (Landblock.AddWorldObjectInternal) or an earlier pass cut it to 15 s: let it go
                if ((c.TimeToRot ?? 0) <= Corpse.EmptyDecayTime) return true;
                double left = until.Value - ACE.Common.Time.GetUnixTime();
                if (left > Corpse.EmptyDecayTime) { c.TimeToRot = left; return false; }
                c.TimeToRot = Math.Max(0.5, left);
                return true;
            }
        }

        // ------------------------------------------------------------ the kill board

        internal static List<string> KillPages(uint root)
        {
            var pages = new List<string>();
            if (root == 0) return pages;
            var kills = Store.Kills(root);
            kills.Reverse();
            if (kills.Count == 0) { pages.Add("Kills on these grounds\n\nNone yet."); return pages; }
            const int PER_PAGE = 5;
            for (int i = 0; i < Math.Min(kills.Count, 30); i += PER_PAGE)
            {
                var sb = new StringBuilder(i == 0 ? "Kills on these grounds, newest first (UTC)\n\n" : "Kills, continued\n\n");
                foreach (var k in kills.Skip(i).Take(PER_PAGE))
                    sb.Append(Line(k)).Append("\n\n");
                pages.Add(sb.ToString().TrimEnd());
            }
            return pages;
        }

        static string Line(KillRec k)
        {
            string when = DateTimeOffset.FromUnixTimeSeconds(k.At).UtcDateTime.ToString("MM/dd HH:mm");
            string of = k.KillerAllegiance != null ? $" of {k.KillerAllegiance}" : "";
            return $"{when} {k.Victim} was killed by {k.Killer}{of}.";
        }

        internal static void KillsCommand(Player p)
        {
            uint monarch = p.Allegiance?.MonarchId ?? p.Guid.Full;
            uint root = PlayerManager.FindByGuid(monarch)?.HouseInstance ?? 0;
            if (root == 0 || MansionIndex.MansionRootAt((ushort)((root >> 12) & 0xFFFF)) != root)
            { Mansions.Say(p, "Your allegiance holds no mansion."); return; }
            var kills = Store.Kills(root);
            if (kills.Count == 0) { Mansions.Say(p, "No one has been killed on your mansion's grounds."); return; }
            Mansions.Say(p, "Kills on your mansion's grounds, most recent last (UTC):");
            foreach (var k in kills.Skip(Math.Max(0, kills.Count - 10))) Mansions.Say(p, "  " + Line(k));
        }

        // ------------------------------------------------------------ the raid alarm

        static readonly Dictionary<uint, uint> s_On = new Dictionary<uint, uint>();          // player -> mansion root it stands on
        static readonly Dictionary<(uint, uint), double> s_Told = new Dictionary<(uint, uint), double>();
        static double s_Next;

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class RaidAlarm
        {
            static void Postfix()
            {
                var now = ACE.Common.Time.GetUnixTime();
                if (now < s_Next) return;
                s_Next = now + ALARM_EVERY;
                try { Scan(now); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionPk raid alarm: {e.Message}"); }
            }
        }

        static void Scan(double now)
        {
            var seen = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline())
            {
                var loc = p.Location;
                if (loc == null || !p.IsPK || p.CloakStatus == CloakStatus.On) continue;
                uint g = p.Guid.Full;
                seen.Add(g);
                uint root = MansionIndex.MansionRootAt(loc.LandblockId.Landblock);
                s_On.TryGetValue(g, out var was);
                s_On[g] = root;
                if (root == 0 || root == was) continue;

                uint owner = MansionIndex.OwnerOf(root);
                if (owner == 0 || owner == g) continue;
                var alleg = Mansions.AllegianceOfOwner(owner, out var ownerP);
                if (alleg != null && alleg.IsMember(p.Guid)) continue;
                if (ownerP?.Account != null && p.Account != null && ownerP.Account.AccountId == p.Account.AccountId) continue;
                if (s_Told.TryGetValue((g, root), out var at) && now - at < ALARM_REPEAT) continue;
                s_Told[(g, root)] = now;

                var theirs = p.Allegiance != null && p.Allegiance.TotalMembers > 1 ? Mansions.AllegianceLabel(p.Allegiance) : null;
                string line = theirs != null
                    ? $"Raid alarm: {p.Name} of {theirs}, a player killer, is on the grounds of your mansion."
                    : $"Raid alarm: {p.Name}, a player killer, is on the grounds of your mansion.";
                Mod.Log.Info($"[RevivalGuard] MansionPk: raid alarm, {p.Name} on the grounds of mansion 0x{root:X8} (owner 0x{owner:X8})");
                foreach (var m in Mansions.Defenders(owner))
                    Mansions.Say(m, line, ChatMessageType.Allegiance);
            }
            foreach (var g in s_On.Keys.Where(k => !seen.Contains(k)).ToList()) s_On.Remove(g);
            if (s_Told.Count > 500)
                foreach (var k in s_Told.Where(kv => now - kv.Value > ALARM_REPEAT).Select(kv => kv.Key).ToList()) s_Told.Remove(k);
        }
    }
}
