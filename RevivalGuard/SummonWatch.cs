using System.Collections.Concurrent;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SUMMON WATCH (2026-10-19, docs/findings/FINDINGS-summons-power.md). A player said summons are "OP af"
    /// and that he can exploit them, without saying how. Every rule we could read is retail's and stock ACE
    /// (use requirements, 43 s life, shared 45 s cooldown, one combat pet, ratings up to 20), so this only
    /// WATCHES: it changes nothing a player can feel.
    ///
    /// On each combat-pet summon (ACE PetDevice.SummonCreature, which declares the method) it logs who, the
    /// essence, its Level Req and Activation Req against the player's level and buffed Summoning, and the
    /// essence's ratings. It flags, as a Warn line, what should be impossible:
    ///   REQ      the summon went through below the essence's level or skill requirement
    ///   SECOND   another combat pet of the same owner is still alive (more than one at a time)
    ///   OLD      that earlier pet is older than 60 s (outlived its 43 s lifespan)
    ///
    ///   @summonwatch   (admin) counts, the last 20 summons and every live tracked pet with its age
    /// Grep ACE_Log.txt for "SummonWatch:". Memory only; a restart clears it.
    /// </summary>
    [HarmonyPatch(typeof(PetDevice), nameof(PetDevice.SummonCreature), new[] { typeof(Player), typeof(uint) })]
    static class SummonWatch
    {
        const double OLD_S = 60;
        const int KEEP = 20;

        sealed class Live { public WeakReference<CombatPet> Pet; public DateTime At; public string Owner; public string Name; }

        static readonly ConcurrentDictionary<uint, List<Live>> s_ByOwner = new();
        static readonly ConcurrentQueue<string> s_Recent = new();
        static long s_Summons, s_Req, s_Second, s_Old;
        static bool s_Registered;

        static bool Prepare()
        {
            if (!s_Registered)
            {
                s_Registered = true;
                CommandManager.TryAddCommand(Handle, "summonwatch", AccessLevel.Admin, CommandHandlerFlag.None,
                    "Summon watch (ours): recent combat-pet summons, live pets and their ages, and any flags.", "");
                Mod.Log.Info("[RevivalGuard] SummonWatch: on (logs each combat-pet summon; @summonwatch)");
            }
            return true;
        }

        static void Postfix(PetDevice __instance, Player player, bool? __result)
        {
            try
            {
                if (__result != true || player == null || !(player.CurrentActivePet is CombatPet pet)) return;
                Interlocked.Increment(ref s_Summons);
                var now = DateTime.UtcNow;
                int level = player.Level ?? 1;
                var skill = player.GetCreatureSkill(Skill.Summoning);
                int needLevel = __instance.UseRequiresLevel ?? 0, needSkill = __instance.UseRequiresSkillLevel ?? 0;

                var flags = new List<string>();
                if (level < needLevel || skill.Current < needSkill) { flags.Add("REQ"); Interlocked.Increment(ref s_Req); }

                var list = s_ByOwner.GetOrAdd(player.Guid.Full, _ => new List<Live>());
                lock (list)
                {
                    list.RemoveAll(l => !l.Pet.TryGetTarget(out var p) || p.IsDestroyed || p.IsDead);
                    foreach (var l in list)
                    {
                        if (!l.Pet.TryGetTarget(out var other) || other == pet) continue;
                        double age = (now - l.At).TotalSeconds;
                        flags.Add($"SECOND({other.Name} {age:0}s)"); Interlocked.Increment(ref s_Second);
                        if (age > OLD_S) { flags.Add("OLD"); Interlocked.Increment(ref s_Old); }
                    }
                    list.Add(new Live { Pet = new WeakReference<CombatPet>(pet), At = now, Owner = player.Name, Name = pet.Name });
                }

                string line = $"{player.Name} L{level} summoning {skill.Current} (base {skill.Base}) used {__instance.Name} "
                            + $"[{__instance.WeenieClassId}] req L{needLevel}/{needSkill} -> {pet.Name} L{pet.Level} "
                            + $"ratings D{__instance.GearDamage ?? 0} DR{__instance.GearDamageResist ?? 0} C{__instance.GearCrit ?? 0} "
                            + $"CD{__instance.GearCritDamage ?? 0} uses {__instance.Structure}/{__instance.MaxStructure}"
                            + (flags.Count > 0 ? " FLAGS " + string.Join(",", flags) : "");
                if (flags.Count > 0) Mod.Log.Warn("[RevivalGuard] SummonWatch: " + line);
                else Mod.Log.Info("[RevivalGuard] SummonWatch: " + line);

                s_Recent.Enqueue($"{now:HH:mm:ss} {line}");
                while (s_Recent.Count > KEEP && s_Recent.TryDequeue(out _)) { }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SummonWatch: {e.GetType().Name}: {e.Message}"); }
        }

        static void Say(Session s, string line) => s?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        static void Handle(Session session, params string[] args)
        {
            Say(session, $"[SummonWatch] (ours) {s_Summons} summons since restart; flags: REQ {s_Req}, SECOND {s_Second}, OLD {s_Old}.");
            foreach (var r in s_Recent) Say(session, r);
            var now = DateTime.UtcNow;
            int live = 0;
            foreach (var kv in s_ByOwner)
                lock (kv.Value)
                    foreach (var l in kv.Value)
                        if (l.Pet.TryGetTarget(out var p) && !p.IsDestroyed && !p.IsDead)
                        {
                            live++;
                            double age = (now - l.At).TotalSeconds;
                            Say(session, $"live: {l.Name} ({l.Owner}) {age:0}s{(age > OLD_S ? " OLD" : "")}");
                        }
            if (live == 0) Say(session, "live: no combat pets right now.");
        }
    }
}
