# MP4 Levels — custom songs for UNBEATABLE [white label]

A BepInEx 5 plugin that turns your own music into playable levels. Drop `.mp3` (or `.mp4`, `.wav`, `.ogg`, `.flac`, `.m4a`, ...) files into the `CustomSongs` folder next to the game and they show up in the song list with five auto-generated difficulties (Beginner → UNBEATABLE). Video files can play behind the notes.

Optional: copy a YouTube link, press **F6** in game, and the song is downloaded with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and charted automatically.

## Install (players)

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64, Mono build — `BepInEx_win_x64_5.4.x.zip`) into the game folder and start the game once.
2. Copy `MP4Levels.dll` to `<game>\BepInEx\plugins\MP4Levels\`.
3. Open a terminal and run `winget install yt-dlp` — this installs yt-dlp **and FFmpeg** (FFmpeg is required to read audio; yt-dlp is for F6 downloads).
4. Start the game. A `CustomSongs` folder is created; put songs there and press **F5** in game to rescan.

Settings live in `BepInEx\config\dotmoth.unbeatable.mp4levels.cfg` (folder, keys, chart offset/density, holds, side flips, video mode).

## How it works

- `ChartGenerator.cs` — beat tracking (STFT spectral flux, autocorrelation tempo, dynamic-programming beat tracker, tempo segmentation) and note placement; writes the osu!mania-style charts the game already uses.
- `Plugin.cs` — scans the folder, converts audio to WAV with ffmpeg, caches charts in `CustomSongs\_cache`, and uses Harmony patches to add the songs to the beatmap index / song list and to play the WAV through the game's FMOD tracker.
- `VideoOverlay.cs` — plays the source video behind the gameplay, synced to the song timeline.
- `Downloader.cs` — runs yt-dlp in the background when F6 is pressed with a link in the clipboard.

No game files are modified or redistributed; everything happens at runtime.

## Build

Requirements: the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer), the game, and BepInEx 5 installed in the game folder (the build references the game's own `Managed\*.dll` plus `BepInEx.dll` / `0Harmony.dll`, which are not included in this repo).

```
git clone <this repo>
cd <repo>
dotnet build -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\UNBEATABLE [white label]"
```

`GamePath` defaults to the Steam path above, so it can be omitted for a default install. The result is `bin\Release\MP4Levels.dll`, which is also copied into `<game>\BepInEx\plugins\MP4Levels\`.

## License

MIT — see [LICENSE](LICENSE). UNBEATABLE is © D-CELL GAMES; this is an unofficial fan mod.
