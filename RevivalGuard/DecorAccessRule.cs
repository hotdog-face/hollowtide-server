namespace RevivalGuard
{
    /// <summary>Who may decorate where, pure (no ACE types), so tools/nettest checks it by path ("decorgate").</summary>
    public static class DecorAccessRule
    {
        public enum Mode { Off, TestShard, Live }

        // ACE AccessLevel: Player 0, Advocate 1, Sentinel 2, Envoy 3, Developer 4, Admin 5.
        public const int DEVELOPER = 4, ADMIN = 5;

        public const string OFF = "Decorating is switched off on this server.";
        public const string LIVE_ADMINS = "Decorating on the live shard is for Admins only.";
        public const string STAFF = "Decorating needs a Developer or Admin account.";
        public const string WAY_WAITS = "A way into a new island or dungeon waits for the client release that carries its land: "
            + "players without it would arrive nowhere. Place it after that release.";

        /// <summary>The test shard's file wins (a test shard copied from live settings still decorates as a test shard).</summary>
        public static Mode ModeOf(bool enabledFile, bool liveSwitch) =>
            enabledFile ? Mode.TestShard : liveSwitch ? Mode.Live : Mode.Off;

        public static int Need(Mode m) => m == Mode.Live ? ADMIN : DEVELOPER;

        /// <summary>Why `access` may not run `verb` here; null = it may.</summary>
        public static string Refuse(Mode m, int access, string verb)
        {
            if (m == Mode.Off) return OFF;
            if (access < Need(m)) return m == Mode.Live ? LIVE_ADMINS : STAFF;
            return null;
        }

        public static string WhyNotWay(Mode m, bool newGround) => m == Mode.Live && newGround ? WAY_WAITS : null;

        /// <summary>Every verb Decor.Cmd answers (Decor.cs, DecorWorld.cs, DecorEncounters.cs), for the gate's test.</summary>
        public static readonly string[] Verbs =
        {
            "state", "info", "infos", "audit", "undo", "redo", "lock", "kitsave", "enctest", "area", "newarea", "goto", "publish", "pub",
            "part", "scale", "piecescale", "move", "remove", "restore", "add", "unadd", "static", "staticadd", "batch", "revert", "way",
            "encspawn", "encboss", "encchest", "encset", "encmove", "encpatrol", "encremove", "newisland", "newdungeon", "worldbin",
            "dropworld", "bye",
        };
    }
}
