using System;
using System.Globalization;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>Holiday townsfolk, the glue (HolidayNpcRule holds the dates and lines).</summary>
    static class HolidayNpcs
    {
        internal const string P_ON = "rg_holiday_npcs";
        static DateTime? s_Forced;   // @holidaynpcs date (tests); process-local

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        internal static DateTime PacificToday() => s_Forced ?? HolidayNpcRule.PacificFromUtc(DateTime.UtcNow).Date;

        /// <summary>Today's holiday key when the switch is on, else null.</summary>
        internal static string Today()
        {
            if (!On) return null;
            try { return HolidayNpcRule.On(PacificToday())?.Key; } catch { return null; }
        }

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard HolidayNpcs: on a holiday's date (Pacific) Holtburg's NPCs and Azimar greet and chat about it (needs the owner's yes)");
            CommandManager.TryAddCommand(Cmd, "holidaynpcs", AccessLevel.Developer, CommandHandlerFlag.None,
                "Holiday lines for Holtburg's NPCs: status, force a date for tests, or list a holiday's lines.",
                "[status | date yyyy-mm-dd | date auto | lines <key>]");
            Mod.Log.Info($"[RevivalGuard] HolidayNpcs: {HolidayNpcRule.Holidays.Length} holidays, {HolidayNpcRule.Lines.Values.Sum(v => v.Length)} lines ({P_ON} {(On ? "on" : "off")})");
        }

        static void Cmd(Session session, params string[] a)
        {
            void Out(string s) { if (session != null) session.Network.EnqueueSend(new GameMessageSystemChat(s, ChatMessageType.Broadcast)); else Mod.Log.Info("[RevivalGuard] @holidaynpcs: " + s); }
            try
            {
                var verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
                if (verb == "date" && a.Length > 1)
                {
                    if (a[1].ToLowerInvariant() == "auto") s_Forced = null;
                    else if (DateTime.TryParseExact(a[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) s_Forced = d.Date;
                    else { Out("HolidayNpcs: date yyyy-mm-dd, or date auto."); return; }
                }
                if (verb == "lines" && a.Length > 1)
                {
                    var key = a[1].ToLowerInvariant();
                    foreach (var kv in HolidayNpcRule.Lines.Where(x => x.Key.StartsWith("hol." + key + ".", StringComparison.Ordinal)))
                        foreach (var l in kv.Value) Out($"{kv.Key.Substring(5 + key.Length)}: {l}");
                    return;
                }
                var today = PacificToday();
                var h = HolidayNpcRule.On(today);
                Out($"HolidayNpcs: {(On ? "on" : "OFF")} ({P_ON}), Pacific date {today:yyyy-MM-dd}{(s_Forced != null ? " (forced)" : "")}, holiday {(h != null ? h.Name : "none")}.");
                Out("Next: " + string.Join(", ", HolidayNpcRule.Upcoming(today, 4).Select(x => $"{x.h.Name} {x.at:yyyy-MM-dd}")));
            }
            catch (Exception e) { Out("HolidayNpcs: " + e.Message); }
        }
    }
}
