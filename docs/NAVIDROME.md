# Navidrome in GZJam

GZJam plays music from a self-hosted [Navidrome](https://www.navidrome.org/) server (or another
Subsonic/OpenSubsonic one): [#184](https://github.com/GemZombie/GZJam/issues/184),
[#204](https://github.com/GemZombie/GZJam/issues/204).

## What it does

- **Songs.** The server's songs appear in a **Navidrome** folder of the library: searched,
  filtered, hearted and labelled like any other. Only their descriptions are kept in the library
  database; a song is fetched when it is cached (below).
- **Playlists.** Each server playlist becomes one of GZJam's (blue icon), a one-way copy that
  follows the server. **Refresh** in the playlists header reads the server again.
- **Playing.** A song has to be on this computer to play: it is downloaded whole into a folder
  here, then plays like a file (seek, tempo, waveform, BPM, loudness all work). A song fetched once
  plays again with no network.
- **Getting songs.** Only the songs the DJ asks for are downloaded: right-click a playlist, a
  Navidrome folder, or some songs > *Cache for offline play*, or Play / Add to queue in the
  Navidrome window. A song that is not here yet will not load onto a deck. Turn on *Get songs by
  themselves* in Settings > Navidrome and songs are downloaded when they are queued or loaded, and
  kept. The **Downloads** button in the top bar (Ctrl/Cmd+Shift+J) shows the queue; a blue mark by
  a song's title says it is on this computer (the green check is "played tonight").
- **Settings > Navidrome.** Address, user, password, TEST and SAVE; whether songs are fetched by themselves; where the downloaded songs
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

The password is not kept. Subsonic logs in with a salt and a token (md5 of the password and the salt)
and the server takes the same pair every time, so that pair is what `navidrome.json` holds (beside
the settings, never in `settings.json`: library backups carry that). It can't give back the password or open
the server's web page; it can still be used for the server's API as that user, so on Windows it is
encrypted for the Windows user (DPAPI), and on a Mac or Linux the file is readable only by its owner.
Files saved by an earlier version with the password in them are read and rewritten this way.

## Not done

Cover art for the server's songs; writing playlists back to the server; a cache size limit or a
"clear" button; the Keychain (Mac) and Secret Service (Linux) for the login.
