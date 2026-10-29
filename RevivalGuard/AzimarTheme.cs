using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AZIMAR DRESSES FOR THE FESTIVALS (owner 2026-10-25: "any other seasonal events that don't have a buff bot he goes
    /// there but themed for the event"). NOT RETAIL. Festival Season has its own buffer (the Pumpkin Buffer), and Azimar
    /// stands in its spots the rest of the year (azimar-seasons.sql, SeasonalCalendar's AzimarYearRound row). While
    /// Frostfell, the Spring Festival, the Royal Wedding anniversary, the Anniversary or the New Year runs, he wears that
    /// festival's theme:
    ///   * his palette (this rule, server side, so every client sees it): the creature's own palette rows on the live
    ///     object, then an ObjDesc update. Frostfell icy blue, the Royal Wedding and the New Year silver; the others
    ///     keep his gold.
    ///   * hats, flowers and sparkles (our client): mod channel "azimar" with the theme key; AcAzimar adds the theme's
    ///     attaches from expansion/models/attach.json ("901631:frostfell") and the Anniversary's firework sparkles.
    ///   * festival greetings (AzimarVoice): pools greet.fest.&lt;theme&gt; and idle.fest.&lt;theme&gt;.
    /// Staff preview (the admin panel's Azimar row): "@azimar theme &lt;key|auto&gt;" sets the theme on THIS server for 30
    /// minutes (everyone on it sees it; meant for the box test shard), "@azimar" says what is showing and why.
    /// </summary>
    static class AzimarTheme
    {
        internal const uint WCID = 901631;
        internal const string CHANNEL = "azimar";
        const double PREVIEW_SECONDS = 30 * 60;

        internal static string[] Themes => AzimarThemeRule.Themes;
        internal static (uint pal, ushort off, ushort len)[] PaletteFor(string theme) => AzimarThemeRule.PaletteFor(theme);

        static readonly object s_Lock = new();
        static readonly List<WeakReference<WorldObject>> s_Seen = new();
        static readonly Dictionary<uint, List<PropertiesPalette>> s_Own = new();   // guid -> his rows as spawned
        static string s_Preview; static double s_PreviewUntil; static string s_PreviewBy;
        static string s_Applied = "";

        static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        internal static string Current()
        {
            string preview;
            lock (s_Lock) { if (s_Preview != null && Now >= s_PreviewUntil) s_Preview = null; preview = s_Preview; }
            return AzimarThemeRule.Resolve(SeasonalCalendar.IsFestivalActive, preview);
        }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Command, "azimar", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Azimar's festival theme: what is showing, or preview one on this server for 30 minutes.",
                "[theme frostfell|spring|wedding|anniversary|newyear|none|auto]");
            Mod.Log.Info("[RevivalGuard] AzimarTheme: Azimar dresses for Frostfell, Spring, the Royal Wedding, the Anniversary and the New Year (@azimar)");
        }

        /// <summary>Once a minute from SeasonalCalendar's tick: a festival turning changes his theme.</summary>
        internal static void Tick()
        {
            var t = Current();
            if (t != s_Applied) Apply(t);
        }

        /// <summary>After the mod channel's hello: the theme showing now.</summary>
        internal static void Hello(Session session)
        {
            try { ModChannel.Send(session, CHANNEL, Current()); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarTheme hello: {e.Message}"); }
        }

        static void Apply(string theme)
        {
            s_Applied = theme;
            List<WorldObject> live = new();
            lock (s_Lock)
            {
                s_Seen.RemoveAll(w => !w.TryGetTarget(out var o) || o.IsDestroyed);
                foreach (var w in s_Seen) if (w.TryGetTarget(out var o)) live.Add(o);
            }
            foreach (var wo in live)
            {
                try
                {
                    SetPalette(wo, theme);
                    if (wo.CurrentLandblock != null) wo.EnqueueBroadcast(new GameMessageObjDescEvent(wo));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarTheme: {wo.Name} 0x{wo.Guid.Full:X8}: {e.Message}"); }
            }
            foreach (var p in PlayerManager.GetAllOnline())
                if (p?.Session != null && ModChannel.Capable(p.Session)) ModChannel.Send(p.Session, CHANNEL, theme);
            Mod.Log.Info($"[RevivalGuard] AzimarTheme: {(theme.Length > 0 ? theme : "no festival theme")} on {live.Count} Azimar(s)");
        }

        static void SetPalette(WorldObject wo, string theme)
        {
            var rows = PaletteFor(theme);
            wo.BiotaDatabaseLock.EnterWriteLock();
            try
            {
                var list = wo.Biota.PropertiesPalette ??= new List<PropertiesPalette>();
                lock (s_Lock)
                    if (!s_Own.ContainsKey(wo.Guid.Full)) s_Own[wo.Guid.Full] = list.Select(r => r.Clone()).ToList();
                List<PropertiesPalette> own;
                lock (s_Lock) own = s_Own[wo.Guid.Full];
                list.Clear();
                if (rows == null) foreach (var r in own) list.Add(r.Clone());
                else foreach (var (pal, off, len) in rows) list.Add(new PropertiesPalette { SubPaletteId = pal, Offset = off, Length = len });
            }
            finally { wo.BiotaDatabaseLock.ExitWriteLock(); }
        }

        /// <summary>EnterWorld prefix: remember him and dress him before his create goes out.</summary>
        internal static void Entering(WorldObject wo)
        {
            if (wo?.WeenieClassId != WCID) return;
            lock (s_Lock) { s_Seen.RemoveAll(w => !w.TryGetTarget(out var o) || o.IsDestroyed); s_Seen.Add(new WeakReference<WorldObject>(wo)); }
            var t = Current();
            if (t.Length > 0) SetPalette(wo, t);
        }

        static void Command(Session session, params string[] args)
        {
            var a = (args ?? Array.Empty<string>()).Select(s => s.ToLowerInvariant()).ToArray();
            string reply;
            if (a.Length >= 2 && a[0] == "theme")
            {
                string k = a[1];
                if (k == "auto")
                {
                    lock (s_Lock) s_Preview = null;
                    reply = "Azimar follows the calendar again.";
                }
                else if (k == "none" || Themes.Contains(k))
                {
                    lock (s_Lock) { s_Preview = k; s_PreviewUntil = Now + PREVIEW_SECONDS; s_PreviewBy = session?.Player?.Name ?? "console"; }
                    reply = $"Azimar shows the {k} theme on this server for 30 minutes (everyone here sees it). '@azimar theme auto' ends it.";
                }
                else reply = "Themes: " + string.Join(", ", Themes) + ", none, auto.";
                Apply(Current());
            }
            else
            {
                string t = Current(), why;
                lock (s_Lock) why = s_Preview != null ? $"preview by {s_PreviewBy}, {Math.Max(0, (s_PreviewUntil - Now) / 60):0} min left" : "the calendar";
                int n; lock (s_Lock) n = s_Seen.Count(w => w.TryGetTarget(out var o) && !o.IsDestroyed);
                reply = $"Azimar: {(t.Length > 0 ? t : "no festival theme")} ({why}); {n} in the world. @azimar theme <key|none|auto>";
            }
            if (session != null) session.Network.EnqueueSend(new GameMessageSystemChat(reply, ChatMessageType.Broadcast));
            else Console.WriteLine(reply);
        }
    }

    /// <summary>WorldObject declares EnterWorld (Creature does not override it).</summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.EnterWorld))]
    static class AzimarThemeEnter
    {
        static void Prefix(WorldObject __instance)
        {
            try { AzimarTheme.Entering(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarTheme enter: {e.Message}"); }
        }
    }
}
