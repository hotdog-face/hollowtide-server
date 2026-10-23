using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// WHAT KEPT A PLAYER BUSY, for the bug report (261023-0014, "Spell hanging entirely too long... Should be included in
    /// bug report data"). Per player, the last few uses, casts, UseDones, "You're too busy!" refusals and watchdog rescues
    /// with their times; TicketDesk writes them, with the cast/use state and footing at that moment, as server_busy.
    /// Notes only: nothing here changes what ACE does.
    /// </summary>
    static class BusyLedger
    {
        const int MAX = 16;
        static readonly TimeSpan KEEP = TimeSpan.FromMinutes(10);
        sealed class Entry { public DateTime At; public string Text; public int Count; }
        static readonly Dictionary<uint, List<Entry>> s_By = new Dictionary<uint, List<Entry>>();
        static readonly object s_Lock = new object();

        public static void Note(Player p, string text)
        {
            if (p == null || text == null) return;
            var now = DateTime.UtcNow;
            lock (s_Lock)
            {
                if (!s_By.TryGetValue(p.Guid.Full, out var l))
                {
                    if (s_By.Count > 500) s_By.Clear();
                    s_By[p.Guid.Full] = l = new List<Entry>();
                }
                var last = l.Count > 0 ? l[l.Count - 1] : null;
                if (last != null && last.Text == text && (now - last.At).TotalSeconds < 30) { last.Count++; last.At = now; return; }
                l.Add(new Entry { At = now, Text = text, Count = 1 });
                if (l.Count > MAX) l.RemoveAt(0);
            }
        }

        /// <summary>"now: busy True, casting Nether Arc VII 3.2s (...); footing ...; recent: 10:03:50 use Condensed Dispel
        /// Potion; 10:04:00 chug watchdog: ..." (UTC times).</summary>
        public static string Describe(Player p)
        {
            if (p == null) return "";
            var now = DateTime.UtcNow;
            var parts = new List<string> { $"busy {p.IsBusy}" };
            try
            {
                var ms = p.MagicState;
                if (ms != null && ms.IsCasting)
                    parts.Add($"casting {ms.CastSpellParams?.Spell?.Name ?? "?"} for {(now - ms.StartTime).TotalSeconds:F1}s (gesture {ms.CastGesture}, "
                            + $"motion done {ms.CastMotionDone}, turning {ms.IsTurning}, turn release {ms.PendingTurnRelease})");
                var fs = p.FoodState;
                if (fs != null && fs.IsChugging) parts.Add($"using, waiting on {fs.UseMotion} (anim {fs.UseAnimTime:F1}s)");
                parts.Add(ServerFooting.Describe(p));
            }
            catch (Exception e) { parts.Add("state ? " + e.GetType().Name); }
            string recent = "";
            lock (s_Lock)
            {
                if (s_By.TryGetValue(p.Guid.Full, out var l))
                    recent = string.Join("; ", l.Where(e => now - e.At < KEEP)
                        .Select(e => $"{e.At:HH:mm:ss} {e.Text}{(e.Count > 1 ? $" x{e.Count}" : "")}"));
            }
            return "now: " + string.Join(", ", parts) + (recent.Length > 0 ? "; recent (UTC): " + recent : "");
        }

        static string Name(Player p, uint guid)
        {
            try { return p.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems)?.Name ?? $"0x{guid:X8}"; }
            catch { return $"0x{guid:X8}"; }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem))]
        static class OnUse
        {
            static void Prefix(Player __instance, uint itemGuid) { try { Note(__instance, "use " + Name(__instance, itemGuid)); } catch { } }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ApplyConsumableWithAnimationCallbacks))]
        static class OnChug
        {
            static void Postfix(Player __instance, MotionCommand useMotion) { try { Note(__instance, $"{useMotion} started (fast chug)"); } catch { } }
        }

        [HarmonyPatch(typeof(MagicState), nameof(MagicState.SetCastParams))]
        static class OnCast
        {
            static void Postfix(MagicState __instance)
            {
                try
                {
                    var c = __instance.CastSpellParams;
                    Note(__instance.Player, $"cast {c?.Spell?.Name ?? "?"}{(c?.Target != null && c.Target != __instance.Player ? " on " + c.Target.Name : "")}");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SendWeenieError))]
        static class OnError
        {
            static void Postfix(Player __instance, WeenieError error) { if (error == WeenieError.YoureTooBusy) Note(__instance, "You're too busy!"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SendUseDoneEvent))]
        static class OnUseDone
        {
            static void Postfix(Player __instance, WeenieError errorType) { Note(__instance, "UseDone " + errorType); }
        }
    }
}
