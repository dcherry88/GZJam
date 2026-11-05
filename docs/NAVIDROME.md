# Navidrome in GZJam

GZJam plays music from a self-hosted [Navidrome](https://www.navidrome.org/) server (or another
Subsonic/OpenSubsonic one): [#184](https://github.com/GemZombie/GZJam/issues/184),
[#204](https://github.com/GemZombie/GZJam/issues/204).

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
| `src/GZJam.Library/LibraryDatabase.Remote*.cs` | the server's songs and playlists in the library (database upgrade 25) |
| `src/GZJam.App/Navidrome*.cs`, `ViewModels/Navidrome*`, `Downloads*` | login storage, sync, settings, windows |

A song's path is `navidrome://<id>`. Everything the DJ gives a song (hearts, labels, cues, EQ,
playlist places, History) is kept by that path.

The login is kept in `navidrome.json` beside the settings, never in `settings.json` (library
backups carry that). On Windows the password is encrypted for the Windows user (DPAPI); on a Mac
or Linux it is in a file only the user can read, not encrypted.

## Not done

Cover art for the server's songs; writing playlists back to the server; a cache size limit or a
"clear" button; Keychain storage for the password on a Mac.
