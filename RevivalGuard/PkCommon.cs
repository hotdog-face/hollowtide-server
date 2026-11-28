using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>Shared by the PK expansion: the renown ladder, the bounty board and the Shadow Shrines.</summary>
    internal static partial class PkCommon
    {
        internal const string P_RENOWN = "pk_renown_enabled", P_BOUNTY = "pk_bounty_enabled", P_SHRINES = "pk_shrines_enabled";
        internal const string P_IGNORE_IP = "pk_honor_ignore_ip", P_MIN_LEVEL = "pk_honor_min_level",
            P_RATIO = "pk_honor_level_ratio", P_REPEAT_HOURS = "pk_honor_repeat_hours";

        static readonly (string key, bool def, string desc)[] s_Bools =
        {
            (P_RENOWN, false, "PK expansion: renown ladder, kill streaks, Wanted and season titles (RevivalGuard PkRenown)"),
            (P_BOUNTY, false, "PK expansion: player-posted bounties in pyreals, escrowed (RevivalGuard PkBounty)"),
            (P_SHRINES, false, "PK expansion: capturable Shadow Shrines with an allegiance XP boon (RevivalGuard PkShrines)"),
            (P_IGNORE_IP, false, "PK expansion: count kills between characters on one connection address (a test aid; leave false)"),
            (PkNight.P_ON, false, "PK Night: every other Sunday 2 PM Pacific, hold the hill for 40 minutes (RevivalGuard PkNight; docs/proposals/pk-night.md)"),
            (PkNight.P_DISCORD, false, "PK Night: post the start and the result to Discord (DiscordBridge.Announce)"),
            (PkBattlefield.P_ON, false, "PK battlefields: player corpses in the PK zones (Dark Isle beach, Arwic's lawn, mansions, PK Night spots) never rot on a timer; over a zone's cap the oldest rots (RevivalGuard PkBattlefield)"),
            (PkNight.P_BANNER, false, "PK Night: a Banner of the Held Ground (908120) to the winners: by fellowship, its leader once per player ever; by allegiance, its monarch once per allegiance ever (HeldGroundBanner)"),
        };
        static readonly (string key, long def, string desc)[] s_Longs =
        {
            (P_MIN_LEVEL, DEFAULT_MIN_LEVEL, "PK expansion: a victim under this level is a newcomer; killing one earns infamy, never renown"),
            (PkBounty.P_MIN_TARGET, 50, "PK expansion: lowest level a bounty may be posted on"),
            (PkBounty.P_MIN_AMOUNT, 10000, "PK expansion: smallest bounty, pyreals"),
            (PkBounty.P_MAX_AMOUNT, 2000000, "PK expansion: largest total bounty on one head, pyreals"),
            (PkShrines.P_ATTUNE, 60, "PK expansion: seconds a player killer must hold a Shadow Shrine uncontested to take it"),
            (PkNight.P_RADIUS, PkNight.DEFAULT_RADIUS, "PK Night: metres from the spot's centre within which a player killer holds it"),
            (PkBattlefield.P_CAP, PkBattlefield.DEFAULT_CAP, "PK battlefields: corpses a zone keeps (a density zone keeps more, never fewer)"),
            (PkNight.P_TOKEN, 0, "PK Night: the weenie given to each winner (0 = none; the owner picks one)"),
            (PkNight.P_MARKER, PkNight.MARKER_WCID, "PK Night: the weenie standing every 8 m round the hill's edge (0 = none; default the Tumerok Torch)"),
            (PkNight.P_MARKER_CENTRE, PkNight.MARKER_CENTRE_WCID, "PK Night: the weenie marking the hill's centre (0 = none; default the Gout of Elemental Flame)"),
        };
        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            (P_RATIO, DEFAULT_RATIO, "PK expansion: a victim under this fraction of the killer's level is far beneath them (infamy)"),
            (P_REPEAT_HOURS, DEFAULT_REPEAT_HOURS, "PK expansion: hours over which killing the same victim again earns less, then nothing"),
            (PkBounty.P_FEE, 0.10, "PK expansion: share of a posted bounty the shard keeps (a pyreal sink)"),
            (PkBounty.P_DAYS, 7, "PK expansion: days a bounty stands before half is refunded"),
            (PkShrines.P_BONUS, 0.05, "PK expansion: creature-kill XP bonus per Shadow Shrine an allegiance holds"),
            (PkShrines.P_HOLD_HOURS, 12, "PK expansion: hours a taken shrine stays held without being renewed"),
            (PkShrines.P_LOCK_MINUTES, 30, "PK expansion: minutes after a capture before the shrine can be taken again"),
        };
        static readonly (string key, string def, string desc)[] s_Strings =
        {
            (PkShrines.P_HOURS, "", "PK expansion: UTC hours the shrines can be taken, e.g. 18-23 or 2-4,20-22; empty = always"),
            (PkRenown.P_SEASON, "Season 1", "PK expansion: the name of the current PK season"),
            (PkNight.P_TITLE, "KingofDarktide", "PK Night: the retail CharacterTitle the winners earn (an enum name; none = no title)"),
            (PkNight.P_SIDES, PkNight.SIDES_FELLOWSHIP, "PK Night: what a side is, fellowship (a fellowship, or a player alone; unset means this) or allegiance (the monarch, or a player alone)"),
        };

        /// <summary>Make every key an ordinary ACE property</summary>
        internal static void RegisterProperties()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                foreach (var (k, d, t) in s_Bools) if (!b.ContainsKey(k)) b[k] = new Property<bool>(d, t);
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                foreach (var (k, d, t) in s_Longs) if (!l.ContainsKey(k)) l[k] = new Property<long>(d, t);
                var f = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (k, d, t) in s_Doubles) if (!f.ContainsKey(k)) f[k] = new Property<double>(d, t);
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                foreach (var (k, d, t) in s_Strings) if (!s.ContainsKey(k)) s[k] = new Property<string>(d, t);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PK expansion: properties not registered with @modify* ({e.Message}); they are still read with their defaults"); }
        }

        internal static void RegisterBool(string key, bool def, string desc)
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(key)) b[key] = new Property<bool>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {key}: not registered with @modifybool ({e.Message}); read with its default"); }
        }

        /// <summary>RegisterBool for a long (@modifylong) and a double (@modifydouble): SmallAllegianceBonus's numbers.</summary>
        internal static void RegisterLong(string key, long def, string desc)
        {
            try { var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties); if (!l.ContainsKey(key)) l[key] = new Property<long>(def, desc); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {key}: not registered with @modifylong ({e.Message}); read with its default"); }
        }

        internal static void RegisterDouble(string key, double def, string desc)
        {
            try { var f = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties); if (!f.ContainsKey(key)) f[key] = new Property<double>(def, desc); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {key}: not registered with @modifydouble ({e.Message}); read with its default"); }
        }

        internal static bool On(string key) => PropertyManager.GetBool(key, false).Item;
        internal static long Long(string key, long def) => PropertyManager.GetLong(key, def).Item;
        internal static double Dbl(string key, double def) => PropertyManager.GetDouble(key, def).Item;
        internal static string Str(string key, string def) => PropertyManager.GetString(key, def).Item ?? def;

        internal static bool AnyOn => On(P_RENOWN) || On(P_BOUNTY) || On(P_SHRINES) || PkNight.On || PkArena.On;

        // ------------------------------------------------------------------ the honour check

        // Verdict, Judgement and the rules themselves are in PkRules.cs (pure C#, tested by NetTest).

        /// <summary>How a PK kill counts.</summary>
        internal static Judgement Judge(Player killer, Player victim, int attackers, int priorKills)
        {
            var f = new Facts { NoKiller = killer == null || victim == null || killer == victim };
            if (!f.NoKiller)
            {
                // the Hollow Company only: a Borg is a player here
                f.KillerMerc = Mercenaries.IsHollowCompany(killer);
                f.VictimMerc = Mercenaries.IsHollowCompany(victim);
                f.SameAccount = killer.Account != null && victim.Account != null && killer.Account.AccountId == victim.Account.AccountId;
                f.SameAddress = !On(P_IGNORE_IP) && SameAddress(killer, victim);
                f.Allies = Allies(killer, victim);
                f.KillerLevel = killer.Level ?? 1;
                f.VictimLevel = victim.Level ?? 1;
            }
            return JudgeFacts(f, attackers, priorKills, Long(P_MIN_LEVEL, DEFAULT_MIN_LEVEL), Dbl(P_RATIO, DEFAULT_RATIO));
        }

        internal static bool SameAddress(Player a, Player b)
        {
            try
            {
                var x = Gateway.RealAddress(a.Session?.EndPointC2S); var y = Gateway.RealAddress(b.Session?.EndPointC2S);
                if (x == null || y == null || Gateway.IsUnknown(x) || Gateway.IsUnknown(y)) return false;
                if (x.IsIPv4MappedToIPv6) x = x.MapToIPv4();
                if (y.IsIPv4MappedToIPv6) y = y.MapToIPv4();
                return x.Equals(y);
            }
            catch { return false; }
        }

        /// <summary>One monarch, or one fellowship.</summary>
        internal static bool Allies(Player a, Player b)
        {
            uint ma = a.MonarchId ?? 0, mb = b.MonarchId ?? 0;
            if (ma != 0 && ma == mb) return true;
            if (ma == b.Guid.Full || mb == a.Guid.Full) return true;
            var f = a.Fellowship;
            return f != null && f == b.Fellowship;
        }

        /// <summary>The allegiance key a player fights for: the monarch, or themselves.</summary>
        internal static uint Side(Player p) => p.MonarchId ?? p.Guid.Full;

        internal static string SideLabel(Player p)
        {
            if (p.Allegiance != null && p.Allegiance.TotalMembers > 1) return Mansions.AllegianceLabel(p.Allegiance);
            return p.Name;
        }

        /// <summary>" of &lt;allegiance&gt;" for a player in one, or "" for a lone player.</summary>
        internal static string Of(Player p) =>
            p.Allegiance != null && p.Allegiance.TotalMembers > 1 ? " of " + Mansions.AllegianceLabel(p.Allegiance) : "";

        // ------------------------------------------------------------------ small helpers

        internal static void Say(Player p, string line, ChatMessageType type = ChatMessageType.Broadcast) =>
            p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, type));

        internal static void World(string line) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        internal static long Now() => (long)ACE.Common.Time.GetUnixTime();

        internal static string Q(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        internal static string N(long n, string word) => n.ToString("N0") + " " + word + (n == 1 ? "" : "s");

        internal static string Pyreals(long n) => n.ToString("N0") + (n == 1 ? " pyreal" : " pyreals");
    }
}
