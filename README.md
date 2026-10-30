# OpenBattlefront

A small launcher for [OpenSWBF2](https://github.com/jt-marte/OpenSWBF2), the open-source rebuild of
Star Wars Battlefront II (2005). Point it at your copy of the game and press **PLAY**.

- **Finds your game.** It spots an installed Steam copy by itself. Or drop the game folder on the
  window.
- **Never touches your copy.** It copies what the game needs (about 10 GB) into its own folder, then
  builds OpenSWBF2's core files from your stock ones, on your PC.
- **Ships no game files.** Releases hold only OpenSWBF2's own code and scripts, FFmpeg and DXVK.
  Every file is checked against its SHA-256 before it is used.
- **One-click updates** from OpenSWBF2's GitHub releases, keeping the previous build so you can roll
  back.
- **Mods:** drop a mod folder or `.zip`, switch mods on and off, remove them to the Recycle Bin.
- **Server manager:** coming soon.

## What you need

- Windows 10 or 11, 64-bit.
- Star Wars Battlefront II (Classic, 2005), Steam v1.1. GOG support is planned.
- The Microsoft Visual C++ Redistributable (x64) and the DirectX End-User Runtime. The launcher tells
  you if either is missing.

## Where things go

Everything lives in `%LOCALAPPDATA%\OpenBattlefront` (you can pick another folder before the first
import):

| Folder | What's in it |
| --- | --- |
| `Game\` | The game as it runs: your data, OpenSWBF2, mods (`AddOn\`), saves (`SaveGames\`). |
| `Stock\` | Your four stock core lvls, so updates can rebuild without your original folder. |
| `Builds\` | Unpacked OpenSWBF2 releases: the current one and the one before. |
| `Logs\` | The launcher's logs. |

## Building

You need the .NET 8 SDK.

```
dotnet test
.\publish.ps1
```

`publish.ps1` makes one self-contained `dist\OpenBattlefront.exe`. `.\publish.ps1 -Small` makes a
much smaller exe that needs the .NET 8 Desktop Runtime.

For development, `OpenBattlefront.exe --state <file>` uses another `launcher.json` (and so another
install), and `--screenshot <dir>` saves each page as a PNG and exits.

## Not affiliated

OpenBattlefront is a fan project. It is not affiliated with or endorsed by EA, Disney or Lucasfilm.
You need your own copy of the game.
