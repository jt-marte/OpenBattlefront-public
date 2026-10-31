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
- **Included content:** the launcher package carries the Battlefront Conversion Pack and the Xbox
  DLC, put into the game copy once it is imported (no download, every file SHA-256 checked). They
  show as "Included" on the Mods page: switch them off if you like.
- **Finds your game:** every Steam library, GOG and retail installs, and the usual folders on every
  drive (C:, D:, X: …). Or drop the folder, or a folder above it.
- **Server manager:** host a dedicated server with a lobby. Pick maps from ground or space battles,
  filter by era and mode, drag them into a playlist, then start, end, skip or restart matches, send
  players back to the lobby, kick, swap and chat. AI heroes: a bot takes the hero when no player
  does.

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

`.\publish.ps1 -Content [<AddOn dir>]` also makes `dist\OpenBattlefront-<version>.zip`: the exe plus
`Content\AddOn\BF1`, `Content\AddOn\DLC_XBOX` and `Content\content.json` (each file's size and
SHA-256). The AddOn dir defaults to the launcher's own game copy. Only those two folders are taken:
never the Xbox Title Update (`UPDATE_XBOX`), the unofficial 1.3 patch or any `.exe`, `.dll` or `.bat`.
The launcher installs the content again whenever the content version changes.

For development, `OpenBattlefront.exe --state <file>` uses another `launcher.json` (and so another
install), `--screenshot <dir>` saves each page as a PNG and exits, `--drop <folder>` drops a
folder on the Play page first, and `--fake-server` simulates a server on the Server manager page.

## Not affiliated

OpenBattlefront is a fan project. It is not affiliated with or endorsed by EA, Disney or Lucasfilm.
You need your own copy of the game.
