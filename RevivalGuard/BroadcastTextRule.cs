using System.Text.RegularExpressions;

namespace RevivalGuard
{
    /// <summary>BroadcastText's rules, pure (no ACE types), so tools/nettest compiles and checks them by path.</summary>
    public static class BroadcastTextRule
    {
        /// <summary>ACE's PK kill line (Player_Death.cs HandlePKDeathBroadcast) misspells retail's. Retail, Darktide
        /// 2015-11-15 (the owner's 05_Screenshots/ScreenShot00035.jpg): "Venom-Lord has defeated Daughter of Athena! The
        /// kill occurred at 84.9N, 57.9E".</summary>
        public const string AceKillAt = " The kill occured at ";
        public const string RetailKillAt = " The kill occurred at ";

        static readonly Regex s_Possessive = new Regex(@"(%(?:tn|mn|n|s)) +'s\b", RegexOptions.Compiled);

        /// <summary>An emote template with a space between a name placeholder and its 's ("%tn 's Fellowship has defeated
        /// Aerbax's Shadow!", Aerbax's Shadow 37378) prints "Mister-one 's Fellowship". Retail printed "Mister-one's
        /// Fellowship has defeated Aerbax's Shadow!" (the owner's Screenshots (MEGA)/Aerbax/1c9a6add....jpg).</summary>
        public static string JoinPossessive(string template) =>
            template == null || template.IndexOf(" 's", System.StringComparison.Ordinal) < 0 ? template : s_Possessive.Replace(template, "$1's");
    }
}
