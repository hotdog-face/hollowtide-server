using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// SHARED BY THE PK EXPANSION (docs/PK-EXPANSION.md): the renown ladder (PkRenown), the bounty
    /// board (PkBounty) and the Shadow Shrines (PkShrines). Ours.
    ///
    /// EVERY PART IS OFF UNTIL THE OWNER TURNS IT ON. Each has its own server property, registered
    /// here with a default of false, so a deploy that carries this code changes nothing in the game:
    /// no line is said, no data reaches a client, no kill is scored. `@modifybool pk_renown_enabled
    /// true` (and the other two) turns one on, like any ACE property.
    ///
    /// THE HONOUR CHECK is here because the ladder and the bounties must agree on which kills count.
    /// Retail's PK history is the reason for every rule (wiki: Olthoi Play's slag "less likely to drop"
    /// each time the same player is killed; the 2006 dev chat on gank squads; Darktide's lifestone
    /// campers): a kill between two accounts of one player, two allies, or two characters on one
    /// connection is not a kill; a kill of or by a Hollow Company mercenary is no kill at all
    /// (owner 2026-10-22: players only); a kill of a newcomer (under level 15) or of someone far below the killer earns
    /// infamy instead of renown; the same victim again and again earns less and then nothing.
    /// </summary>
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
        };
        static readonly (string key, long def, string desc)[] s_Longs =
        {
            (P_MIN_LEVEL, DEFAULT_MIN_LEVEL, "PK expansion: a victim under this level is a newcomer; killing one earns infamy, never renown"),
            (PkBounty.P_MIN_TARGET, 50, "PK expansion: lowest level a bounty may be posted on"),
            (PkBounty.P_MIN_AMOUNT, 10000, "PK expansion: smallest bounty, pyreals"),
            (PkBounty.P_MAX_AMOUNT, 2000000, "PK expansion: largest total bounty on one head, pyreals"),
            (PkShrines.P_ATTUNE, 60, "PK expansion: seconds a player killer must hold a Shadow Shrine uncontested to take it"),
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
        };

        /// <summary>Make every key an ordinary ACE property (@modifybool / @modifylong / @fetch...):
        /// ModifyX refuses a key that is not in the Default*Properties dictionary, a ReadOnlyDictionary
        /// over a private one, so the key is added to that inner dictionary (MansionPk, EventDirector).</summary>
        internal static void RegisterProperties()
        {
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                foreach (var (k, d, t) in s_Bools) if (!b.ContainsKey(k)) b[k] = new Property<bool>(d, t);
                var l = Inner(DefaultPropertyManager.DefaultLongProperties);
                foreach (var (k, d, t) in s_Longs) if (!l.ContainsKey(k)) l[k] = new Property<long>(d, t);
                var f = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (k, d, t) in s_Doubles) if (!f.ContainsKey(k)) f[k] = new Property<double>(d, t);
                var s = Inner(DefaultPropertyManager.DefaultStringProperties);
                foreach (var (k, d, t) in s_Strings) if (!s.ContainsKey(k)) s[k] = new Property<string>(d, t);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PK expansion: properties not registered with @modify* ({e.Message}); they are still read with their defaults"); }
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        internal static bool On(string key) => PropertyManager.GetBool(key, false).Item;
        internal static long Long(string key, long def) => PropertyManager.GetLong(key, def).Item;
        internal static double Dbl(string key, double def) => PropertyManager.GetDouble(key, def).Item;
        internal static string Str(string key, string def) => PropertyManager.GetString(key, def).Item ?? def;

        internal static bool AnyOn => On(P_RENOWN) || On(P_BOUNTY) || On(P_SHRINES);

        // ------------------------------------------------------------------ the honour check

        // Verdict, Judgement and the rules themselves are in PkRules.cs (pure C#, tested by NetTest).

        /// <summary>How a PK kill counts. `priorKills` is how many times this killer has killed this
        /// victim within pk_honor_repeat_hours, from the renown ledger (0 when the ladder is off).</summary>
        internal static Judgement Judge(Player killer, Player victim, int attackers, int priorKills)
        {
            var f = new Facts { NoKiller = killer == null || victim == null || killer == victim };
            if (!f.NoKiller)
            {
                f.KillerMerc = Mercenaries.IsMerc(killer);
                f.VictimMerc = Mercenaries.IsMerc(victim);
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
                // THE PLAYER'S OWN ADDRESS, NOT THE GATEWAY'S (owner 2026-10-18: the PK board stayed empty). Every browser
                // player reaches ACE from the WebSocket gateway on this machine's loopback, so comparing the endpoint called
                // two strangers in browsers "two characters on one connection": 108 of 161 live PK kills were thrown out
                // for it. Gateway.RealAddress asks the gateway who is behind each port; one it could not answer for (the
                // unknown marker) never counts as the same connection.
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

        /// <summary>" of &lt;allegiance&gt;" for a player in one, or "" for a lone player (whose side is
        /// themselves, so "X of X" would read wrong).</summary>
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
