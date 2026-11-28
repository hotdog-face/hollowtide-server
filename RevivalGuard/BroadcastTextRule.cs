using System.Text.RegularExpressions;

namespace RevivalGuard
{
    /// <summary>BroadcastText's rules, pure (no ACE types), so tools/nettest compiles and checks them by path.</summary>
    public static class BroadcastTextRule
    {
        public const string AceKillAt = " The kill occured at ";
        public const string RetailKillAt = " The kill occurred at ";

        static readonly Regex s_Possessive = new Regex(@"(%(?:tn|mn|n|s)) +'s\b", RegexOptions.Compiled);

        /// <summary>An emote template with a space between a name placeholder and its 's prints "Mister-one 's Fellowship".</summary>
        public static string JoinPossessive(string template) =>
            template == null || template.IndexOf(" 's", System.StringComparison.Ordinal) < 0 ? template : s_Possessive.Replace(template, "$1's");
    }
}
