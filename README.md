# Xbox Avatar for CastleForge

Your Xbox Original Avatar as your character in Castle Miner Z, loaded as a
[CastleForge](https://github.com/RussDev7/CastleForge) mod. One DLL in `!Mods`,
no patched executable.

- Your avatar replaces the stock character, for you and for everyone in the
  session running this mod.
- In first person you get your own hands and glove, posed by the game's own
  held-item animation.
- Held items sit in the avatar's real grip, computed from its finger bones, so
  short, tall, slim and heavy builds all hold a pickaxe correctly.
- Players without the mod are unaffected and see the stock character.

## Installing

1. Install CastleForge.
2. Put `XboxAvatar.dll` in the game's `!Mods` folder.
3. Start the game once. The mod unpacks its importer into
   `!Mods/XboxAvatar/`.
4. Open Xbox Original Avatars, leave the avatar you want on screen, and run
   `!Mods/XboxAvatar/Import Xbox Avatar.exe`.

Removing it is deleting `XboxAvatar.dll` and the `!Mods/XboxAvatar` folder.

## Settings

`!Mods/XboxAvatar/item-tuning.txt`, re-read within a second of saving:

| Setting | Meaning |
| --- | --- |
| `grip 1` | How far the first-person hand follows the game's finger pose. `1` is the game's own pose, `0` the open hand third person shows. |
| `hands mesh` | Draw the avatar's own hand. |
| `mode hand` | How the held item is anchored. |

## Why this exists as a mod rather than a patcher

This started as an add-on that rewrote five call sites in `CastleMinerZ.exe`
with dnlib, kept a byte-exact backup, and put it all back whenever the game
updated. That machinery is gone here. CastleForge boots a loader through the
game's own `.config`, so:

| Then | Now |
| --- | --- |
| Rewrite the `newobj` in `Player..ctor` | Harmony postfix |
| Insert a call after `CommonAssembly.Initalize` | register late and rebuild the message table |
| Insert into `CastleMinerZGame.OnMessage` | Harmony prefix |
| Insert into `CastleMinerZGame.OnGamerJoined` | Harmony prefix |
| Insert before every `ret` in `Game.Update` | `ModBase.Tick`, which the loader already calls |

Nothing is written to the game executable, so a game update cannot break the
install and there is no backup to restore.

The one wrinkle CastleForge introduces: mods load after the engine has built its
network message table, and message IDs are positional. The mod registers itself
and rebuilds that table at load. Its packet sorts after every stock type, so
stock IDs do not move — `AvatarMessageIdSmoke` guards exactly that — and a peer
advertising a different ID is left on the stock character rather than sent
packets its game would misread.

## Building

Requires Windows, Visual Studio (for the Roslyn compiler), a Castle Miner Z
install, and CastleForge.

```powershell
./build.ps1 -GameDirectory "C:\...\common\CastleMiner Z" `
            -CastleForgeDirectory "<CastleForge install or release>" `
            -SampleAvatar "<any .ocavatar>"
```

`-CastleForgeDirectory` takes more than one path, which is useful with a working
checkout where the loader assemblies and Harmony live in different places.

`-BridgeDirectory` embeds a built capture bridge for in-game avatar capture. It
is optional: without it, importing an existing `.ocavatar` still works.

`-SampleAvatar` is a test fixture and is never shipped. Without one the
avatar-dependent tests report `SKIP` — read those lines before releasing.

The build runs four smoke tests: the avatar format and network protocol, stock
packet IDs, the third-person grip, and the first-person hand geometry.

## Repository boundary

This repository contains **no binaries at all** — no game executable, engine
library, decompiled source, Xbox app metadata, personal avatar, or even
Harmony. Everything binary is supplied at build time from your own install.
The `Source boundary audit` workflow enforces this on every push.

## Licence

GPL-3.0-or-later, because the mod links CastleForge's loader, which is
GPL-3.0-or-later.

The avatar runtime — the renderer, `.ocavatar` format, first-person hand and
multiplayer protocol — comes from
[KikoTs/openclassic-xbox-avatar](https://github.com/KikoTs/openclassic-xbox-avatar)
and [KikoTs/castleminerz-xbox-avatar](https://github.com/KikoTs/castleminerz-xbox-avatar),
which are MIT, and is included here under GPL-3.0 as MIT permits.

`Embedded/EmbeddedExporter.cs` and `Embedded/EmbeddedResolver.cs` are by
RussDev7, from CastleForge, GPL-3.0-or-later.

Castle Miner Z, CastleForge, Microsoft XNA, Xbox Original Avatars and their
assets remain the property of their respective owners and are not included.
