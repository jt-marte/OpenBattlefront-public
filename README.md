# OpenBattlefront

A small launcher for [OpenSWBF2](https://github.com/jt-marte/OpenSWBF2), the open-source rebuild of
Star Wars Battlefront II (2005). Point it at your copy of the game and press **PLAY**.

## What you need

- Windows 10 or 11, 64-bit.
- Star Wars Battlefront II (Classic, 2005), Steam or GOG v1.1. GOG and Steam players play together.
- The VC++ Redistributable (x64) and the DirectX End-User Runtime. The launcher says if either is
  missing.

## Getting started

1. Download `OpenBattlefront.exe` from [Releases](../../releases) and run it. If a friend gave you
   the full zip, unzip all of it first.
2. Pick your game from the list, or drop its folder on the window. The launcher looks in every Steam
   library, GOG and retail installs, and the usual folders on every drive. A GOG copy is in
   `C:\GOG Games\Star Wars - Battlefront 2` unless you chose another folder. Install it with GOG's
   installer first, since the installer itself can't be imported.
3. Press **Import**. The launcher copies what the game needs (about 10 GB) and builds OpenSWBF2's
   core files from your stock ones. Your copy is only read.
4. Press **PLAY**.

To switch to another copy later, drop it on the Play page.

## Features

- **Updates:** the launcher updates itself from this repo's releases. New OpenSWBF2 builds install
  in one click, and the previous build is kept so you can roll back.
- **Mods:** drop a mod folder or `.zip`, switch mods on and off, and remove them to the Recycle Bin.
  The full zip also brings the Battlefront Conversion Pack and the Xbox DLC.
- **Multiplayer:** Direct Connect and a lobby. The mods a server uses download before the match.
- **Server manager:** host a dedicated server, or connect to one as admin, the Docker server too.
  Build a playlist of ground and space battles by era and mode. Start, end, skip or restart
  matches, kick, swap teams and chat.
- **No game files shipped:** releases hold only the launcher. OpenSWBF2 builds hold only its code
  and scripts, FFmpeg and DXVK, each checked against its SHA-256.

## Where things go

Everything lives in `%LOCALAPPDATA%\OpenBattlefront`. You can pick another folder before the first
import.

| Folder | What's in it |
| --- | --- |
| `Game\` | The game as it runs: your data, OpenSWBF2, mods (`AddOn\`) and saves (`SaveGames\`). |
| `Stock\` | Your four stock core lvls, so updates can rebuild without your original folder. |
| `Builds\` | Unpacked OpenSWBF2 releases: the current one and the one before. |
| `Logs\` | The launcher's logs. |

## Building

You need the .NET 8 SDK.

```
dotnet test
.\publish.ps1
```

- `publish.ps1` makes a self-contained `dist\OpenBattlefront.exe`. `-Small` makes a much smaller exe
  that needs the .NET 8 Desktop Runtime.
- A release here carries `OpenBattlefront.exe` and `OpenBattlefront.exe.sha256`
  (`<sha256> *OpenBattlefront.exe`). Every launcher, the full zip's included, updates itself from
  the latest one.
- `-Content [<AddOn dir>]` also makes the full zip, `dist\OpenBattlefront-<version>.zip`: the exe
  plus `Content\AddOn\BF1`, `Content\AddOn\DLC_XBOX` and `Content\content.json` (sizes and
  SHA-256s). It is shared privately, never attached to a release here. Only those two folders are
  taken: never the Xbox Title Update (`UPDATE_XBOX`), the unofficial 1.3 patch, or any `.exe`,
  `.dll` or `.bat`.
- For development, these switches go after `OpenBattlefront.exe`:
  - `--state <file>` uses another `launcher.json`, and so another install.
  - `--screenshot <dir>` saves each page as a PNG and exits.
  - `--drop <folder>` drops a folder on the Play page first.
  - `--fake-server` simulates a server on the Server manager page.

## Not affiliated

OpenBattlefront is a fan project, not affiliated with or endorsed by EA, Disney or Lucasfilm. You
need your own copy of the game.
