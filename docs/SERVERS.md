# Music servers in GZJam

GZJam plays music from self-hosted servers that speak the **Subsonic API**: [Navidrome](https://www.navidrome.org/)
(the one it is tested against, and the one to recommend), Gonic, Airsonic-Advanced, Ampache, Nextcloud Music and
others. Nothing in GZJam is particular to one server. See [#184](https://github.com/GemZombie/GZJam/issues/184)
and [#204](https://github.com/GemZombie/GZJam/issues/204).

## The rule

**The room only ever plays files on the laptop.** A song is downloaded whole (the Subsonic `download` call: the
original file, never converted) and then plays like any file on disk: seek, tempo, waveform, BPM and loudness all
work, and a song downloaded once plays again with the server away. Nothing is streamed to the room.

## What it does

- **Several servers.** Settings › Servers takes any number: your own, and a friend's who gives you an account.
  Each has its own name, address and login. Each server's songs appear in a library folder of its name, and its
  playlists in the playlist list (a read-only copy, in blue, that follows the server: *Refresh* reads the servers
  again). A song is kept by the server and its id there, so two servers never mix up their songs or the hearts,
  labels and cues you give them.
- **Songs.** Searched, filtered, hearted and labelled like any other. Only their descriptions are kept in the
  library database.
- **Songs not on this computer.** Settings › Servers › *Songs not on this computer* says what happens to a song
  that is queued or loaded but isn't downloaded yet:
  - *Only songs I download* (the default): nothing is downloaded by itself. Such a song can't be queued or loaded;
    GZJam says how to download it. The room never waits on the network.
  - *Download to play, then remove*: it is downloaded when queued or loaded, into a `temporary` folder inside the
    cache folder. GZJam removes those songs when it next starts or closes. Right-click > *Cache for offline play*
    keeps one for good (it is moved, not downloaded again).
  - *Download and keep*: it is downloaded when queued or loaded, and kept.
- **Downloading on purpose.** Right-click a playlist, a server folder, or some songs > *Cache for offline play*;
  or Play and Add to queue in the Servers window (Ctrl/Cmd+Shift+N), which searches all servers at once. The
  **Downloads** button in the top bar (Ctrl/Cmd+Shift+J) shows the queue; a blue mark by a song's title says it
  is on this computer (the green check is "played tonight").
- **A server that doesn't allow downloads** for your user (`downloadRole`) is said so when you test or save it:
  GZJam can't play songs from it.

## Where things are

| | |
|---|---|
| `src/GZJam.Subsonic` | the Subsonic API client (token + salt login, search, playlists, `download`) |
| `src/GZJam.Audio/RemoteSongs.cs` | the download queue, the cache and the temporary folder |
| `src/GZJam.Library/LibraryDatabase.Remote*.cs` | the servers' songs and playlists in the library (database upgrade 25) |
| `src/GZJam.App/Server*.cs`, `ViewModels/Server*`, `Downloads*` | the servers and their logins, sync, settings, windows |

A song's path is `subsonic://<server>/<id>`, `<server>` being a short id GZJam gave the server when it was added.
Everything the DJ gives a song (hearts, labels, cues, EQ, playlist places, History) is kept by that path, so
renaming a server or moving it to a new address changes nothing.

## Logins

The password is not kept. Subsonic logs in with a salt and a token (md5 of the password and the salt) and the
server takes the same pair every time, so that pair is what `servers.json` holds (beside the settings, never in
`settings.json`: library backups carry that). It can't give back the password or open the server's web page; it can
still be used for the server's API as that user, so on Windows it is encrypted for the Windows user (DPAPI), and on
a Mac or Linux the file is readable only by its owner.

To try GZJam without saving a login, start it with `GZJAM_SUBSONIC_URL`, `GZJAM_SUBSONIC_USER` and
`GZJAM_SUBSONIC_PASSWORD`.

## Not done

Cover art for the server's songs; previewing a song in the headphones without downloading it; stars and ratings
and playlists going back to the server; *Keep a copy in my library*; a cache size limit or a "clear" button; the
Keychain (Mac) and Secret Service (Linux) for the logins; tag edits on server songs staying in the library only
(writing tags into the files is for local files).