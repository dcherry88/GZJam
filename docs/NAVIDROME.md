# Navidrome in GZJam (private fork)

This fork plays music from a self-hosted [Navidrome](https://www.navidrome.org/) server. It is not
part of upstream GZJam (github.com/GemZombie/GZJam) and isn't meant to be sent there.

## What it does

- **Songs.** The server's songs appear in a **Navidrome** folder of the library: searched,
  filtered, hearted and labelled like any other. Only their descriptions are kept in the library
  database; a song is fetched when it is first queued or loaded.
- **Playlists.** Each server playlist becomes one of GZJam's (blue icon), a one-way copy that
  follows the server. **Refresh** in the playlists header reads the server again.
- **Playing.** A song is downloaded whole into a folder on this computer, then plays like a file
  (seek, tempo, waveform, BPM, loudness all work). A song fetched once plays again with no network.
- **Caching ahead.** Right-click a playlist, a Navidrome folder, or some songs > *Cache for offline
  play*. The **Downloads** button in the top bar (Ctrl/Cmd+Shift+J) shows the queue; a blue mark by a
  song's title says it is on this computer (the green check is "played tonight").
- **Settings > Navidrome.** Address, user, password, TEST and SAVE; where the downloaded songs
  are kept.

## Where things are

| | |
|---|---|
| `src/GZJam.Navidrome` | the Subsonic/OpenSubsonic client (token + salt login, search, playlists) |
| `src/GZJam.Audio/RemoteSongs.cs` | the download queue and the cache of fetched songs |
| `src/GZJam.Library/LibraryDatabase.Remote*.cs` | the server's songs and playlists in the library (database upgrade 23) |
| `src/GZJam.App/Navidrome*.cs`, `ViewModels/Navidrome*`, `Downloads*` | login storage, sync, settings, windows |

A song's path is `navidrome://<id>`. Everything the DJ gives a song (hearts, labels, cues, EQ,
playlist places, History) is kept by that path.

The login is kept in `navidrome.json` beside the settings, never in `settings.json` (library
backups carry that). On Windows the password is encrypted for the Windows user (DPAPI); on a Mac
or Linux it is in a file only the user can read, not encrypted.

## Building

- `dotnet build GZJam.slnx -c Release -p:TreatWarningsAsErrors=true`, then `dotnet test`.
- On a Windows computer with .NET SDK 10.0.403, **add `-p:ApplicationIcon=`** to every build, run
  and test: the app icon makes `GZJam.dll` invalid there ("Bad IL format"), upstream's `main`
  included. It only drops the .exe's icon.
- While GZJam is running, a Release build can't overwrite its files: build `-c Debug` or close it.
- **Mac (Apple Silicon):** `packaging/macos/build-local.sh` makes the `.dmg`. On the Mac, with the
  .NET 10 SDK: `bash packaging/macos/build-local.sh`. Without the SDK, publish on another
  computer (`dotnet publish src/GZJam.App -c Release --self-contained -r osx-arm64`) and run
  `bash packaging/macos/build-local.sh --from <that folder> <version>` on the Mac. It is signed ad
  hoc, so the first launch needs right-click > Open.

## Not done

Cover art for the server's songs; writing playlists back to the server; a cache size limit or a
"clear" button; Keychain storage for the password on a Mac.
