using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THREE QUESTIONS, EACH ASKED ONCE (docs/PLAYER-DATA-PLAN.md phase 2, owner 2026-10-03 "go phase 2").
    /// At three milestones the developer asks one short question in chat; the player answers with
    /// `/answer <text>` (the client sends it to the ticket desk as kind "answer", and TicketDesk files it
    /// with the question it answers) or simply ignores it. Nothing is ever asked twice.
    ///
    ///   academy  out of the Training Academy (RecallsDisabled cleared) within the first 20 hours played
    ///   lvl20    level 20 or more
    ///   10h      10 hours played
    ///
    /// At most one question per session, never in the first 3 minutes of one, never to a staff or bot
    /// account. Who was asked what is ~/ace/reports/feedback/asked.tsv (guid, key, UTC), so a restart
    /// does not ask again. Not quest flags: QuestLog shows every flag in the client's journal.
    /// </summary>
    static class Feedback
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "feedback");
        static readonly object s_Lock = new object();
        static HashSet<string> s_Asked;

        sealed class SessionQ { public DateTime Start = DateTime.UtcNow; public string Question; public bool AskedThisSession; }
        static readonly ConditionalWeakTable<Player, SessionQ> s_Session = new ConditionalWeakTable<Player, SessionQ>();

        static readonly (string key, string question)[] Q =
        {
            ("academy", "How was the Training Academy? Was anything confusing, or missing?"),
            ("lvl20",   "What almost made you stop playing so far, if anything?"),
            ("10h",     "What should we add or fix next?"),
        };

        internal static string LastQuestion(Player p) => p != null && s_Session.TryGetValue(p, out var s) ? s.Question : null;

        static bool Asked(Player p, string key)
        {
            lock (s_Lock)
            {
                if (s_Asked == null)
                {
                    s_Asked = new HashSet<string>();
                    try
                    {
                        string f = Path.Combine(Dir, "asked.tsv");
                        if (File.Exists(f))
                            foreach (var line in File.ReadAllLines(f))
                            {
                                var c = line.Split('\t');
                                if (c.Length >= 2) s_Asked.Add(c[0] + "/" + c[1]);
                            }
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] feedback: could not read asked.tsv: {e.Message}"); }
                }
                return s_Asked.Contains($"{p.Guid.Full:X8}/{key}");
            }
        }

        static void MarkAsked(Player p, string key)
        {
            lock (s_Lock)
            {
                s_Asked.Add($"{p.Guid.Full:X8}/{key}");
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "asked.tsv"), $"{p.Guid.Full:X8}\t{key}\t{DateTime.UtcNow:o}\n");
            }
        }

        static bool Due(Player p, string key)
        {
            int age = p.Age ?? 0;
            switch (key)
            {
                case "academy": return !(p.GetProperty(PropertyBool.RecallsDisabled) ?? false) && age < 20 * 3600;
                case "lvl20": return (p.Level ?? 1) >= 20;
                case "10h": return age >= 10 * 3600;
            }
            return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat))]
        static class Ask
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p?.Session == null || (p.Account?.AccessLevel ?? 0) > 0) return;
                    var s = s_Session.GetValue(p, _ => new SessionQ());
                    if (s.AskedThisSession || (DateTime.UtcNow - s.Start).TotalMinutes < 3) return;
                    if (p.IsBusy || p.Teleporting || p.CombatMode != CombatMode.NonCombat) return;
                    foreach (var (key, question) in Q)
                    {
                        if (!Due(p, key) || Asked(p, key)) continue;
                        MarkAsked(p, key);
                        s.AskedThisSession = true;
                        s.Question = question;
                        p.Session.Network.EnqueueSend(new GameMessageSystemChat(
                            $"A quick question from the developer: {question} Type /answer and your reply, or just ignore this.",
                            ChatMessageType.Broadcast));
                        Telemetry.Emit("asked", p, ",\"q\":" + Telemetry.J(key));
                        return;
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] feedback: {e.Message}"); }
            }
        }
    }
}
