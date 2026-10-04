using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// @SETHERITAGE: CORRECT AN OFFLINE CHARACTER'S HERITAGE (AND LOOK) THE WAY CREATION WOULD HAVE SET IT.
    ///
    /// Heritage is chosen at creation and never changes in retail, so there is no retail procedure to
    /// copy. This exists for one case: a character re-made on this shard with the wrong heritage
    /// (an admin character, restored 2026-09-20 as Aluvian on a loadout file's word; her own 2016-17
    /// character sheet says Female Sho). It is a database correction of our own error, and it says
    /// so in the Audit channel.
    ///
    /// What heritage actually is, in ACE (Factories/PlayerFactory.cs, Create): PropertyInt.HeritageGroup
    /// and PropertyString.HeritageGroup; the sex's block from the CharGen table in the portal DAT
    /// (Setup, PaletteBase, MotionTable, SoundTable, PhysicsEffectTable, CombatTable, DefaultScale);
    /// the hair style's HeadObject and the character row's HairTexture / DefaultHairTexture; the eye,
    /// nose and mouth textures and their defaults; the skin, hair and eye palettes resolved from THAT
    /// heritage's palette sets; and the melee/ranged masteries (PlayerFactory.GetMasteries). The
    /// attribute budget, skill credits, templates and skill-cost overrides are read from the same
    /// table and are identical for the four human heritages (checked in the DAT, 2026-09-22), so
    /// nothing numeric follows from the change; the innate augmentation (JackOfAllTrades) is the same
    /// for all four as well (PlayerFactory.SetInnateAugmentations).
    ///
    ///   @setheritage [dry] &lt;character name&gt; | &lt;heritage&gt; &lt;sex&gt; [hair=n] [haircolor=n] [hairpal=n | hairhue=0..1]
    ///                                                      [eyecolor=n] [skinhue=0..1] [eyes=n] [nose=n] [mouth=n]
    ///
    /// Every index is an index into the NEW heritage-and-sex's own CharGen lists, exactly what the
    /// character generator sends: hair = HairStyleList, haircolor = HairColorList (a palette set each),
    /// hairpal = the n-th palette inside that set (or hairhue, the 0..1 slider PalSet::GetPaletteID
    /// takes), eyecolor = EyeColorList (extended indices resolve through ExtendedEyeColors as at
    /// creation), skinhue = the slider over SkinPalSet, eyes/nose/mouth = the strip lists. Anything not
    /// given is kept where the new lists still contain the current value (same head object, same
    /// texture, same palette) and otherwise falls back to index 0 and says so; a hair colour that the
    /// new heritage does not offer is refused rather than guessed, because a value the generator would
    /// never produce is exactly what this verb exists to undo.
    ///
    /// Limits, all deliberate: the four human heritages only (the others change the body, the innate
    /// augmentation and the clothing rules); the target sex's Setup must equal the character's current
    /// Setup (ClothingBases are keyed by setup, and the character's worn and packed gear was resolved
    /// against the body they have); offline characters only, Admin only. `dry` prints the full plan
    /// and changes nothing. The write happens on the database thread, in order with any queued save,
    /// through the OfflinePlayer's own setters so the in-memory biota (which WorldManager.PlayerEnterWorld
    /// takes in preference to the database) and the rows agree. Admin command feedback is ours.
    /// </summary>
    public static class SetHeritage
    {
        static readonly HeritageGroup[] HUMANS = { HeritageGroup.Aluvian, HeritageGroup.Gharundim, HeritageGroup.Sho, HeritageGroup.Viamontian };

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "setheritage", AccessLevel.Admin, CommandHandlerFlag.None,
                "Correct an offline character's heritage (human heritages only), re-resolving their look from the new heritage's CharGen lists.",
                "[dry] <character name> | <heritage> <sex> [hair=n] [haircolor=n] [hairpal=n|hairhue=0..1] [eyecolor=n] [skinhue=0..1] [eyes=n] [nose=n] [mouth=n]");
            Mod.Log.Info("[RevivalGuard] SetHeritage: @setheritage registered (Admin; console or in game; offline characters only; dry mode prints the plan)");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Who(Session s) => s?.Player?.Name ?? "console";

        const string USAGE = "Usage: @setheritage [dry] <character name> | <heritage> <sex> [hair=n] [haircolor=n] [hairpal=n|hairhue=0..1] [eyecolor=n] [skinhue=0..1] [eyes=n] [nose=n] [mouth=n]";

        /// <summary>One property in the plan: what it is, what it was, what it becomes.</summary>
        sealed class Change { public string Label; public string Old; public string New; public bool Same => Old == New; public override string ToString() => Same ? $"{Label} {Old} (unchanged)" : $"{Label} {Old} -> {New}"; }

        static void Handle(Session session, params string[] parameters)
        {
            if (session != null && session.AccessLevel < AccessLevel.Admin) { Say(session, "@setheritage is Admin only."); return; }
            var line = string.Join(" ", parameters).Trim();
            bool dry = false;
            if (line.StartsWith("dry ", StringComparison.OrdinalIgnoreCase)) { dry = true; line = line.Substring(4).Trim(); }
            var bar = line.IndexOf('|');
            if (bar < 0) { Say(session, USAGE); return; }
            var name = line.Substring(0, bar).Trim();
            var words = line.Substring(bar + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length == 0 || words.Length < 2) { Say(session, USAGE); return; }

            // The heritage and sex, by name or number, as the enums spell them.
            if (!TryHeritage(words[0], out var heritage)) { Say(session, $"'{words[0]}' is not a heritage. Human heritages: Aluvian, Gharundim, Sho, Viamontian. Nothing was changed."); return; }
            if (!HUMANS.Contains(heritage)) { Say(session, $"{heritage} is not one of the four human heritages; the others change the body, the innate augmentation and the clothing rules, and this verb does not do that. Nothing was changed."); return; }
            if (!TrySex(words[1], out var gender)) { Say(session, $"'{words[1]}' is not a sex. Male or Female (1 or 2). Nothing was changed."); return; }

            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in words.Skip(2))
            {
                var eq = w.IndexOf('=');
                if (eq <= 0) { Say(session, $"'{w}' is not key=value. {USAGE}"); return; }
                opts[w.Substring(0, eq)] = w.Substring(eq + 1);
            }
            var known = new[] { "hair", "haircolor", "hairpal", "hairhue", "eyecolor", "skinhue", "eyes", "nose", "mouth" };
            var unknown = opts.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0) { Say(session, $"Unknown option(s) {string.Join(", ", unknown)}. {USAGE}"); return; }

            var cg = DatManager.PortalDat.CharGen;
            if (cg == null || !cg.HeritageGroups.TryGetValue((uint)heritage, out var hg)) { Say(session, $"The portal DAT's CharGen table has no heritage {(int)heritage}. Nothing was changed."); return; }
            if (!hg.Genders.TryGetValue((int)gender, out var sex)) { Say(session, $"{hg.Name} has no {gender} block in the CharGen table. Nothing was changed."); return; }

            var target = PlayerManager.FindByName(name, out bool online);
            if (target == null) { Say(session, $"No character named '{name}'. Nothing was changed."); return; }
            if (online) { Say(session, $"{target.Name} is online; @setheritage writes the biota under an offline character. Have them log out first. Nothing was changed."); return; }
            if (!(target is OfflinePlayer op)) { Say(session, $"{target.Name} is not an offline player object. Nothing was changed."); return; }
            try { if (op.IsDeleted || op.IsPendingDeletion) { Say(session, $"{target.Name} is deleted or pending deletion. Nothing was changed."); return; } }
            catch (Exception e) { Say(session, $"Could not read {target.Name}'s deletion state ({e.GetType().Name}). Nothing was changed."); return; }

            // THE BODY MUST NOT CHANGE. ClothingBases are keyed by setup id, and everything the
            // character wears or carries was resolved against the body they have now.
            uint curSetup = op.GetProperty(PropertyDataId.Setup) ?? 0;
            if (sex.SetupID != curSetup)
            {
                Say(session, $"{hg.Name} {gender} uses body setup 0x{sex.SetupID:X8}; {target.Name} is on 0x{curSetup:X8}. Their worn and packed ClothingBases were resolved against that body, so this verb refuses to change it. Nothing was changed.");
                return;
            }

            var notes = new List<string>();
            string why;

            // Hair style: given, else the index whose head object is the one they have.
            uint curHead = op.GetProperty(PropertyDataId.HeadObject) ?? 0;
            int hairIdx;
            if (opts.TryGetValue("hair", out var hairS)) { if (!TryIndex(hairS, sex.HairStyleList.Count, out hairIdx, out why)) { Say(session, $"hair: {why}. Nothing was changed."); return; } }
            else
            {
                hairIdx = IndexOf(sex.HairStyleList.Count, i => sex.GetHeadObject((uint)i) == curHead);
                if (hairIdx < 0) { hairIdx = 0; notes.Add($"current head object 0x{curHead:X8} is not a {hg.Name} {gender} hair style; style 0 used"); }
            }
            var hairstyle = sex.HairStyleList[hairIdx];
            uint? newHead = sex.GetHeadObject((uint)hairIdx);

            // Hair colour: a palette set from the new heritage's list, then a palette inside it.
            uint curHairPal = op.GetProperty(PropertyDataId.HairPalette) ?? 0;
            int colorIdx = -1, palIdx = -1; double hairHue = -1;
            if (opts.TryGetValue("haircolor", out var hcS) && !TryIndex(hcS, sex.HairColorList.Count, out colorIdx, out why)) { Say(session, $"haircolor: {why} ({hg.Name} {gender} offers {sex.HairColorList.Count} hair colour sets). Nothing was changed."); return; }
            if (opts.TryGetValue("hairhue", out var hhS) && (!double.TryParse(hhS, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hairHue) || hairHue < 0 || hairHue > 1)) { Say(session, "hairhue must be 0..1. Nothing was changed."); return; }
            if (colorIdx < 0 && (hairHue >= 0 || opts.ContainsKey("hairpal"))) { Say(session, "hairpal and hairhue need haircolor=<set index> as well. Nothing was changed."); return; }
            uint newHairPal;
            if (colorIdx >= 0)
            {
                var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.HairColorList[colorIdx]);
                if (set == null || set.PaletteList.Count == 0) { Say(session, $"Hair colour set {colorIdx} (0x{sex.HairColorList[colorIdx]:X8}) is empty in the DAT. Nothing was changed."); return; }
                if (opts.TryGetValue("hairpal", out var hpS))
                {
                    if (!TryIndex(hpS, set.PaletteList.Count, out palIdx, out why)) { Say(session, $"hairpal: {why} (set {colorIdx} holds {set.PaletteList.Count} palettes). Nothing was changed."); return; }
                    hairHue = (palIdx + 0.5) / set.PaletteList.Count;   // the slider position whose GetPaletteID lands on that palette
                }
                else if (hairHue < 0) hairHue = 0.5;
                newHairPal = set.GetPaletteID(hairHue);
                palIdx = set.PaletteList.IndexOf(newHairPal);
            }
            else
            {
                // Nothing given: keep the palette they have if the new heritage offers it, otherwise refuse.
                for (int s = 0; s < sex.HairColorList.Count && colorIdx < 0; s++)
                {
                    var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.HairColorList[s]);
                    int p = set?.PaletteList.IndexOf(curHairPal) ?? -1;
                    if (p >= 0) { colorIdx = s; palIdx = p; hairHue = (p + 0.5) / set.PaletteList.Count; }
                }
                if (colorIdx < 0)
                {
                    Say(session, $"{target.Name}'s hair palette 0x{curHairPal:X8} is not one {hg.Name} {gender} can be created with. Give haircolor=<0..{sex.HairColorList.Count - 1}> and hairpal=<n> (sets: {string.Join(", ", sex.HairColorList.Select((id, i) => $"{i}=0x{id:X8}"))}). Nothing was changed.");
                    return;
                }
                newHairPal = curHairPal;
            }

            // Eye colour: given, else the index whose palette they already have, else 0.
            uint curEyesPal = op.GetProperty(PropertyDataId.EyesPalette) ?? 0;
            int eyeColorIdx;
            if (opts.TryGetValue("eyecolor", out var ecS)) { if (!int.TryParse(ecS, out eyeColorIdx) || eyeColorIdx < 0) { Say(session, "eyecolor must be a non-negative index. Nothing was changed."); return; } }
            else
            {
                eyeColorIdx = sex.EyeColorList.IndexOf(curEyesPal);
                if (eyeColorIdx < 0) { eyeColorIdx = 0; notes.Add($"current eye palette 0x{curEyesPal:X8} is not a {hg.Name} {gender} eye colour; colour 0 used"); }
            }
            uint newEyesPal = ExtendedEyeColors.Resolve(sex, (uint)heritage, (uint)eyeColorIdx);

            // Skin: the slider over the new heritage's skin set; kept where they had it.
            double skinHue = op.GetProperty(PropertyFloat.Shade) ?? 0.5;
            if (opts.TryGetValue("skinhue", out var shS) && (!double.TryParse(shS, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out skinHue) || skinHue < 0 || skinHue > 1)) { Say(session, "skinhue must be 0..1. Nothing was changed."); return; }
            var skinSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.SkinPalSet);
            if (skinSet == null || skinSet.PaletteList.Count == 0) { Say(session, $"Skin set 0x{sex.SkinPalSet:X8} is empty in the DAT. Nothing was changed."); return; }
            uint newSkinPal = skinSet.GetPaletteID(skinHue);

            // Face strips: given, else the index whose texture they already have, else 0.
            uint curEyesTex = op.GetProperty(PropertyDataId.EyesTexture) ?? 0, curNoseTex = op.GetProperty(PropertyDataId.NoseTexture) ?? 0, curMouthTex = op.GetProperty(PropertyDataId.MouthTexture) ?? 0;
            int eyesIdx, noseIdx, mouthIdx;
            if (opts.TryGetValue("eyes", out var eyS)) { if (!TryIndex(eyS, sex.EyeStripList.Count, out eyesIdx, out why)) { Say(session, $"eyes: {why}. Nothing was changed."); return; } }
            else { eyesIdx = IndexOf(sex.EyeStripList.Count, i => sex.GetEyeTexture((uint)i, hairstyle.Bald) == curEyesTex); if (eyesIdx < 0) { eyesIdx = 0; notes.Add($"current eye texture 0x{curEyesTex:X8} is not in the {hg.Name} {gender} list; eyes 0 used"); } }
            if (opts.TryGetValue("nose", out var noS)) { if (!TryIndex(noS, sex.NoseStripList.Count, out noseIdx, out why)) { Say(session, $"nose: {why}. Nothing was changed."); return; } }
            else { noseIdx = IndexOf(sex.NoseStripList.Count, i => sex.GetNoseTexture((uint)i) == curNoseTex); if (noseIdx < 0) { noseIdx = 0; notes.Add($"current nose texture 0x{curNoseTex:X8} is not in the {hg.Name} {gender} list; nose 0 used"); } }
            if (opts.TryGetValue("mouth", out var moS)) { if (!TryIndex(moS, sex.MouthStripList.Count, out mouthIdx, out why)) { Say(session, $"mouth: {why}. Nothing was changed."); return; } }
            else { mouthIdx = IndexOf(sex.MouthStripList.Count, i => sex.GetMouthTexture((uint)i) == curMouthTex); if (mouthIdx < 0) { mouthIdx = 0; notes.Add($"current mouth texture 0x{curMouthTex:X8} is not in the {hg.Name} {gender} list; mouth 0 used"); } }

            Masteries(heritage, out var melee, out var ranged);
            bool multiPart = hairstyle.ObjDesc.AnimPartChanges.Count > 1;
            uint newSetup = hairstyle.AlternateSetup > 0 ? hairstyle.AlternateSetup : sex.SetupID;
            uint hairTex = sex.GetHairTexture((uint)hairIdx) ?? 0, defHairTex = sex.GetDefaultHairTexture((uint)hairIdx) ?? 0;

            // The plan, every property named, old and new.
            var ints = new List<(PropertyInt p, int v)> { (PropertyInt.HeritageGroup, (int)heritage), (PropertyInt.Gender, (int)gender), (PropertyInt.MeleeMastery, (int)melee), (PropertyInt.RangedMastery, (int)ranged) };
            var strings = new List<(PropertyString p, string v)> { (PropertyString.HeritageGroup, hg.Name), (PropertyString.Sex, gender == Gender.Male ? "Male" : "Female") };
            var dids = new List<(PropertyDataId p, uint v)>
            {
                (PropertyDataId.MotionTable, sex.MotionTable), (PropertyDataId.SoundTable, sex.SoundTable), (PropertyDataId.PhysicsEffectTable, sex.PhysicsTable),
                (PropertyDataId.Setup, newSetup), (PropertyDataId.PaletteBase, sex.BasePalette), (PropertyDataId.CombatTable, sex.CombatTable),
                (PropertyDataId.EyesTexture, sex.GetEyeTexture((uint)eyesIdx, hairstyle.Bald)), (PropertyDataId.DefaultEyesTexture, sex.GetDefaultEyeTexture((uint)eyesIdx, hairstyle.Bald)),
                (PropertyDataId.NoseTexture, sex.GetNoseTexture((uint)noseIdx)), (PropertyDataId.DefaultNoseTexture, sex.GetDefaultNoseTexture((uint)noseIdx)),
                (PropertyDataId.MouthTexture, sex.GetMouthTexture((uint)mouthIdx)), (PropertyDataId.DefaultMouthTexture, sex.GetDefaultMouthTexture((uint)mouthIdx)),
                (PropertyDataId.SkinPalette, newSkinPal), (PropertyDataId.HairPalette, newHairPal), (PropertyDataId.EyesPalette, newEyesPal),
            };
            if (newHead != null) dids.Add((PropertyDataId.HeadObject, newHead.Value));
            var floats = new List<(PropertyFloat p, double v)> { (PropertyFloat.Shade, skinHue) };
            if (sex.Scale != 100) floats.Add((PropertyFloat.DefaultScale, sex.Scale / 100.0));

            var changes = new List<Change>();
            foreach (var (p, v) in ints) changes.Add(new Change { Label = p.ToString(), Old = (op.GetProperty(p)?.ToString() ?? "none"), New = v.ToString() });
            foreach (var (p, v) in strings) changes.Add(new Change { Label = p.ToString(), Old = op.GetProperty(p) ?? "none", New = v });
            foreach (var (p, v) in dids) changes.Add(new Change { Label = p.ToString(), Old = Hex(op.GetProperty(p)), New = $"0x{v:X8}" });
            foreach (var (p, v) in floats) changes.Add(new Change { Label = p.ToString(), Old = op.GetProperty(p)?.ToString("0.###") ?? "none", New = v.ToString("0.###") });
            changes.Add(new Change { Label = "Hairstyle(int)", Old = op.GetProperty(PropertyInt.Hairstyle)?.ToString() ?? "none", New = multiPart ? hairIdx.ToString() : "none" });
            if (sex.Scale == 100) changes.Add(new Change { Label = "DefaultScale", Old = op.GetProperty(PropertyFloat.DefaultScale)?.ToString("0.###") ?? "none", New = "none" });
            var curCharacter = DatabaseManager.Shard.BaseDatabase.GetCharacterStubByGuid(target.Guid.Full);
            changes.Add(new Change { Label = "character.HairTexture", Old = Hex(curCharacter?.HairTexture), New = $"0x{hairTex:X8}" });
            changes.Add(new Change { Label = "character.DefaultHairTexture", Old = Hex(curCharacter?.DefaultHairTexture), New = $"0x{defHairTex:X8}" });

            var changed = changes.Where(c => !c.Same).ToList();
            var same = changes.Where(c => c.Same).Select(c => c.Label).ToList();
            var chosen = $"hair style {hairIdx}, hair colour set {colorIdx} (0x{sex.HairColorList[colorIdx]:X8}) palette {palIdx} (hue {hairHue:0.###}), eye colour {eyeColorIdx}, skin hue {skinHue:0.###} (of {skinSet.PaletteList.Count} skins), eyes {eyesIdx}, nose {noseIdx}, mouth {mouthIdx}";
            Say(session, $"{(dry ? "Dry run" : "Plan")} for {target.Name} (0x{target.Guid.Full:X8}, offline): {op.GetProperty(PropertyString.HeritageGroup) ?? "?"} {op.GetProperty(PropertyString.Sex) ?? "?"} -> {hg.Name} {gender}; {chosen}.");
            Say(session, $"Changes ({changed.Count}): {(changed.Count == 0 ? "none" : string.Join("; ", changed))}.");
            Say(session, $"Unchanged ({same.Count}): {string.Join(", ", same)}.");
            foreach (var n in notes) Say(session, $"Note: {n}.");
            if (dry) { Say(session, "Dry run; nothing was changed."); return; }
            if (changed.Count == 0) { Say(session, "Nothing to do; nothing was changed."); return; }

            uint guid = target.Guid.Full;
            var by = Who(session);
            var summaryChanges = string.Join("; ", changed);
            DatabaseManager.Shard.GetPossessedBiotasInParallel(guid, _ =>
            {
                string step = "re-checking";
                try
                {
                    // Re-checked on the database thread: a login between the command and here would take
                    // the old biota into the world and save it back over these rows later.
                    var again = PlayerManager.FindByGuid(guid, out bool onlineNow);
                    if (again == null || onlineNow || !(again is OfflinePlayer opNow)) { Say(session, $"{name} {(again == null ? "vanished" : "logged in")} before the write. Nothing was changed."); return; }

                    step = "writing the biota";
                    foreach (var (p, v) in ints) opNow.SetProperty(p, v);
                    foreach (var (p, v) in strings) opNow.SetProperty(p, v);
                    foreach (var (p, v) in dids) opNow.SetProperty(p, v);
                    foreach (var (p, v) in floats) opNow.SetProperty(p, v);
                    if (multiPart) opNow.SetProperty(PropertyInt.Hairstyle, hairIdx); else opNow.Biota.TryRemoveProperty(PropertyInt.Hairstyle, opNow.BiotaDatabaseLock);
                    if (sex.Scale == 100) opNow.Biota.TryRemoveProperty(PropertyFloat.DefaultScale, opNow.BiotaDatabaseLock);
                    var db = DatabaseManager.Shard.BaseDatabase;
                    var failed = new List<string>();
                    if (!db.SaveBiota(opNow.Biota, opNow.BiotaDatabaseLock)) failed.Add("player biota (SaveBiota false)");
                    opNow.ChangesDetected = false;   // saved here, in full; the hourly pass need not repeat it

                    step = "writing the character row";
                    var character = db.GetCharacter(guid);
                    if (character == null) failed.Add("character row (not found)");
                    else
                    {
                        character.HairTexture = hairTex;
                        character.DefaultHairTexture = defHairTex;
                        if (!db.SaveCharacter(character, new ReaderWriterLockSlim())) failed.Add("character row (SaveCharacter false)");
                    }

                    var summary = $"{by} set {target.Name} (0x{guid:X8}, offline) to {hg.Name} {gender}: {summaryChanges}";
                    if (failed.Count > 0) { summary += $"; FAILED: {string.Join(", ", failed)} (see the server log)"; Mod.Log.Error($"[RevivalGuard] {summary}"); }
                    else Mod.Log.Info($"[RevivalGuard] {summary}");
                    Say(session, $"{(failed.Count > 0 ? "Partly done" : "Done")}: {summary.Substring(by.Length + 1)}. It shows at the next login.");
                    PlayerManager.BroadcastToAuditChannel(session?.Player, summary + " (a correction of this shard's own record; heritage does not change in retail)");
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] SetHeritage: {name} threw while {step}", e);
                    Say(session, $"@setheritage threw while {step} ({e.GetType().Name}: {e.Message}). Steps before that one stand; see the server log.");
                }
            });
        }

        /// <summary>PlayerFactory.GetMasteries, for the four human heritages this verb accepts.</summary>
        static void Masteries(HeritageGroup h, out WeaponType melee, out WeaponType ranged)
        {
            switch (h)
            {
                case HeritageGroup.Aluvian: melee = WeaponType.Dagger; ranged = WeaponType.Bow; break;
                case HeritageGroup.Gharundim: melee = WeaponType.Staff; ranged = WeaponType.Magic; break;
                case HeritageGroup.Sho: melee = WeaponType.Unarmed; ranged = WeaponType.Bow; break;
                case HeritageGroup.Viamontian: melee = WeaponType.Sword; ranged = WeaponType.Crossbow; break;
                default: melee = WeaponType.Undef; ranged = WeaponType.Undef; break;
            }
        }

        static bool TryHeritage(string s, out HeritageGroup h)
        {
            if (int.TryParse(s, out int n) && Enum.IsDefined(typeof(HeritageGroup), n)) { h = (HeritageGroup)n; return true; }
            return Enum.TryParse(s, true, out h) && Enum.IsDefined(typeof(HeritageGroup), h) && h != HeritageGroup.Invalid;
        }

        static bool TrySex(string s, out Gender g)
        {
            if (s == "1" || s.Equals("male", StringComparison.OrdinalIgnoreCase)) { g = Gender.Male; return true; }
            if (s == "2" || s.Equals("female", StringComparison.OrdinalIgnoreCase)) { g = Gender.Female; return true; }
            g = Gender.Invalid; return false;
        }

        static bool TryIndex(string s, int count, out int idx, out string why)
        {
            why = null;
            if (!int.TryParse(s, out idx) || idx < 0 || idx >= count) { why = $"'{s}' is not an index in 0..{count - 1}"; return false; }
            return true;
        }

        static int IndexOf(int count, Func<int, bool> pred) { for (int i = 0; i < count; i++) if (pred(i)) return i; return -1; }

        static string Hex(uint? v) => v == null ? "none" : $"0x{v.Value:X8}";
    }
}
