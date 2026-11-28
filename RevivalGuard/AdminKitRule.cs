using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>The admin ready kit's plan (pure C#, nettest AdminKitCase; server side AdminKit.cs).</summary>
    public static class AdminKitRule
    {
        public const string P_ON = "rg_admin_kit";

        public static readonly (uint wcid, string name, int target)[] SUPPLIES =
        {
            (9229, "Treated Healing Kit", 3),      // retail's best kit, 50 uses
            (2458, "Health Elixir", 25),
            (2470, "Stamina Elixir", 25),
            (2461, "Mana Elixir", 25),
            (27329, "Massive Mana Charge", 5),     // a charged stone for worn gear (the Self tab's "charges" gives 15)
        };

        public const int COMP_TARGET = 25;         // each scarab, herb, powder, potion, talisman
        public const int TAPER_TARGET = 100;       // tapers: every cast takes one
        public const int AMMO_TARGET = 500;        // half a stack (1000) of the launcher's kind
        public const int DEADLY_SKILL = 230;       // Deadly ammunition's WieldDifficulty (Missile Weapons)

        // ---- ammunition.
        public const int AMMO_ARROW = 1, AMMO_BOLT = 2, AMMO_ATLATL = 4;
        public const int DT_SLASH = 1, DT_PIERCE = 2, DT_BLUDGEON = 4, DT_COLD = 8, DT_FIRE = 16, DT_ACID = 32, DT_ELECTRIC = 64;

        // Deadly families per element: arrow, quarrel, atlatl dart.
        static readonly Dictionary<int, (uint arrow, uint bolt, uint dart)> DEADLY = new Dictionary<int, (uint, uint, uint)>
        {
            { DT_PIERCE,   (15429, 15438, 20964) },   // Deadly Arrow / Quarrel / Atlatl Dart
            { DT_SLASH,    (15433, 15442, 20968) },   // Deadly Broadhead
            { DT_BLUDGEON, (15432, 15441, 20967) },   // Deadly Blunt
            { DT_COLD,     (15437, 15446, 20972) },   // Deadly Frost
            { DT_FIRE,     (15435, 15444, 20970) },   // Deadly Fire
            { DT_ACID,     (15430, 15439, 20965) },   // Deadly Acid
            { DT_ELECTRIC, (15434, 15443, 20969) },   // Deadly Lightning
        };
        static readonly (uint arrow, uint bolt, uint dart) GREATER = (5304, 5313, 15287);

        /// <summary>The ammunition to carry for a launcher, best first</summary>
        public static List<uint> AmmoFor(int ammoType, int damageType, int missileSkill)
        {
            var list = new List<uint>();
            if (ammoType != AMMO_ARROW && ammoType != AMMO_BOLT && ammoType != AMMO_ATLATL) return list;
            int el = DEADLY.ContainsKey(damageType) ? damageType : DT_PIERCE;
            if (missileSkill >= DEADLY_SKILL)
            {
                list.Add(Pick(DEADLY[el], ammoType));
                if (el != DT_PIERCE) list.Add(Pick(DEADLY[DT_PIERCE], ammoType));
            }
            list.Add(Pick(GREATER, ammoType));
            return list;
        }

        static uint Pick((uint arrow, uint bolt, uint dart) f, int ammoType) =>
            ammoType == AMMO_ARROW ? f.arrow : ammoType == AMMO_BOLT ? f.bolt : f.dart;

        /// <summary>How many of a spell component to keep: tapers more (each cast takes one).</summary>
        public static int ComponentTarget(string name) =>
            name != null && name.IndexOf("Taper", StringComparison.OrdinalIgnoreCase) >= 0 ? TAPER_TARGET : COMP_TARGET;

        // ---- the panel's numbers
        public const float CLEAR_DEFAULT = 30f, CLEAR_MIN = 1f, CLEAR_MAX = 200f;
        public const int FREEZE_DEFAULT = 30, FREEZE_MIN = 1, FREEZE_MAX = 600;

        public static float ClearRadius(string arg)
        {
            if (!float.TryParse((arg ?? "").Trim().TrimEnd('m', 'M'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.IsNaN(v))
                return CLEAR_DEFAULT;
            return Math.Clamp(v, CLEAR_MIN, CLEAR_MAX);
        }

        public static int FreezeSeconds(string arg)
        {
            if (!int.TryParse((arg ?? "").Trim().TrimEnd('s', 'S'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return FREEZE_DEFAULT;
            return Math.Clamp(v, FREEZE_MIN, FREEZE_MAX);
        }

        /// <summary>"42.1N 33.6E", "42.1n,33.6w" or "-42.1 33.6" (north and east positive) to map coordinates.</summary>
        public static bool ParseCoords(string text, out float ns, out float ew)
        {
            ns = ew = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            if (!One(parts[0], 'N', 'S', out ns) || !One(parts[1], 'E', 'W', out ew)) return false;
            return ns >= -101.95f && ns <= 102f && ew >= -101.95f && ew <= 102f;
        }

        static bool One(string s, char pos, char neg, out float v)
        {
            v = 0f;
            s = s.Trim();
            float sign = 1f;
            char last = char.ToUpperInvariant(s[s.Length - 1]);
            if (last == pos || last == neg) { sign = last == neg ? -1f : 1f; s = s.Substring(0, s.Length - 1); }
            else if (char.IsLetter(last)) return false;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || float.IsNaN(v)) return false;
            v *= sign;
            return true;
        }

        /// <summary>Map coordinates to an outdoor cell and its local x, y: the client's map teleport run on the server.</summary>
        public static void CellOf(float ns, float ew, out uint cell, out float x, out float y)
        {
            float gx = (ew + 101.95f) * 240f, gy = (ns + 101.95f) * 240f;
            int lbx = Math.Clamp((int)Math.Floor(gx / 192f), 0, 254), lby = Math.Clamp((int)Math.Floor(gy / 192f), 0, 254);
            x = Math.Clamp(gx - lbx * 192f, 0.5f, 191.5f);
            y = Math.Clamp(gy - lby * 192f, 0.5f, 191.5f);
            cell = (uint)(lbx << 24 | lby << 16) | (uint)((int)Math.Floor(x / 24f) * 8 + (int)Math.Floor(y / 24f) + 1);
        }

        /// <summary>The status line the panel reads its orbs from: "[AdminKit] (ours) status: revive=on nofall=off ...".</summary>
        public static string StatusLine(bool on, bool revive, bool nofall, bool ignore, bool run, bool see, bool pk) =>
            $"[AdminKit] (ours) status: kit={Word(on)} revive={Word(revive)} nofall={Word(nofall)} ignore={Word(ignore)} run={Word(run)} see={Word(see)} pk={Word(pk)}";

        static string Word(bool b) => b ? "on" : "off";
    }
}
