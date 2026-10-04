# Hollowtide server source

The source of the server side of **Hollowtide**, an Asheron's Call revival. Published as the GNU Affero
General Public License requires: the Hollowtide server runs a modified [ACE](https://github.com/ACEmulator/ACE)
(ACEmulator, AGPL-3.0) for players over the network, so its source is offered to them here.

Hollowtide is not affiliated with or endorsed by Turbine, WB Games or the original game's owners. No game
data is included; Asheron's Call's own files are the players' own.

## What is here

| Folder | What it is |
|---|---|
| `ace/` | Our changes to ACE as one patch, `hollowtide-ace.patch`, against the upstream commit named in `ace/UPSTREAM_COMMIT`. |
| `RevivalGuard/` | The server mod: a Harmony plugin loaded into ACE's process. It holds the gameplay guards, events, world bosses, the ticket desk and the rest of Hollowtide's server features. |
| `world-sql/` | The content changes applied to ACE's world database: new creatures, items, quests, places and fixes. |

## Building

1. Check out ACE at the commit in `ace/UPSTREAM_COMMIT` and apply the patch:
   `git apply ../ace/hollowtide-ace.patch`
2. Build ACE: `dotnet build -c Release` in `Source/ACE.Server`.
3. Build the mod against that ACE build: `dotnet build -c Release -p:ACEBIN=<ACE's bin/Release/net10.0>`
   in `RevivalGuard/`. Install the dll and `Meta.json` in ACE's `Mods/RevivalGuard/` folder.
4. Apply the `world-sql/` files to an ACE world database.

Some limits the mod enforces (its anti-cheat thresholds) are read at startup from a `tunables.json`
beside the mod's dll. The live server's file is not published; without it, the built-in (more lenient)
defaults apply.

## What is not here

The Hollowtide game client is separate, closed-source software that contains no ACE code. It is
distributed on itch.io.

## License

GNU Affero General Public License v3.0, as ACE's. See `LICENSE`.
