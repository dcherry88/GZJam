# What's new in GZJam

What changed in each release, newest first. Downloads are on the
[Releases page](https://github.com/GemZombie/GZJam/releases).

<!--
Each pull request adds what a DJ would notice to "## Unreleased", under "New", "Changed" and
"Fixed" as needed. Releasing renames it "## <version> (<date>)" and starts a new, empty one. The
release workflow refuses a version that has no section, and uses the section as the release's
notes (tools/release-notes.sh). Bring ROADMAP.md up to date at the same time: it's the big
picture, this is what changed. The whole routine: docs/RELEASING.md.
-->

## Unreleased

New:

- **Music from a Navidrome server:** Settings > Navidrome takes the server's address and login, and its songs appear in a *Navidrome* folder of the library, searched, hearted and labelled like any other. Its playlists come into the playlist list (*Refresh* reads the server again). Songs are downloaded whole to a folder on the computer (you choose it) and play like files, so the dance never depends on the Wi-Fi; right-click a playlist, folder or songs > *Cache for offline play* (or turn on *Get songs by themselves* to download as songs are queued or loaded), and the *Downloads* button in the top bar shows the queue ([#184](https://github.com/GemZombie/GZJam/issues/184), [#204](https://github.com/GemZombie/GZJam/issues/204)). How it fits together: [docs/NAVIDROME.md](docs/NAVIDROME.md).

- **Fade a song out where you choose:** for a song with a long or repetitive outro, right-click
  the waveform › *Song ends* › *Start fade-out here* (or *at the playhead*, found by ear). The
  song plays on from there, fading out over your fade-out length (Settings › Decks), rather than
  stopping dead; auto-play's gap comes after the fade. The waveform shows the fade as a slope after
  the song's end, and the time left counts to the end of the fade. Remembered for the song, like
  its start and end.
- **The room's song from the taskbar (Windows) or the Dock (Mac):** pause or play the song the
  room hears, fade it out (with auto-play on, that's the next song), and turn auto-play on or off,
  without switching to GZJam. On Windows, point at GZJam's taskbar button: the three buttons are
  under its preview. On a Mac, right-click (or Control-click) GZJam's Dock icon. With two taps to
  pause on air (Settings › Decks), these take two clicks too, and say so.

## 1.2.0-beta.1 (2026-11-04)

A beta of 1.2, for trying it before everyone gets it. GZJam's update notice doesn't offer betas:
you only get one from this page. The first time it opens your library, it adds what 1.2 needs and
reads every song again, once; 1.1.0 can still open the library after that, so you can go back to
it at any time. Writing tag edits into your song files is off unless you turn it on.

New:

- **Every tag in your songs:** GZJam reads all the tags in your song files now, not only the usual
  ones: the composer, a mood, the dance in a tag of your own. Right-click a column header to show
  any of them as a column (under *Tags in your songs*), and search them: `composer:ellington`,
  `dance:waltz`, or just type the words. The first scan after updating reads every song again, once.
- **Song info:** right-click a song › *Song info…*, or press Ctrl+I (Cmd+I on a Mac), to see
  everything about it in one window: its tags, every other tag in the file, the file itself (where
  it is, MP3 or FLAC, the bitrate, the length) and what GZJam knows (BPM, rating, energy, labels,
  plays, cues, EQ, loudness, the playlists it's in). *Show all tags* adds the ones only other
  programs use, like MusicBrainz IDs and ReplayGain. In the song list, the queue and History.
- **Edit your songs' tags:** in a song's info, type over the title, artist, genre, BPM or any other
  tag, add one (a Dance tag of your own), change its rating, energy and labels, and *Save*. Select
  several songs first to change them all at once: what they share shows, a field where they differ
  says *(several)* and stays as each song has it unless you type over it. A search and *Select all*
  fixes an artist's spelling everywhere. GZJam says how many songs before changing them, and
  *Undo* puts everything back. Your edits are kept in GZJam's library, searched and backed up,
  and your files aren't changed, unless you turn on *Write tag edits into the song files*
  (Settings › Library, or in the window), so other players see them too. Only the tags are
  written, never the audio, and never while the song is on a deck.

## 1.1.0 (2026-11-04)

Playing music for a class, a practice night or between sets got easier: a playlist played in one
step, shuffled if you like, and Repeat. It reads the same library as 1.0.0, so you can go back to
1.0.0 at any time.

New:

- **Play a playlist shuffled:** a playlist's right-click menu (and the ⋯ beside *Add to queue*) has
  *Add to queue shuffled*, and the queue's menu has *Shuffle queue*, with Undo. The songs go into
  the queue in a random order, so you still see what's next and can change it.
- **Repeat:** a new auto-play option (the ⋯ beside the gap). *The queue*: each song goes back to
  the end of the queue as it's loaded, so a playlist plays over and over, for a class, a practice
  night or music between sets. *This song*: the song playing plays again and again, and the queue
  waits for when you switch back. Like *Wait for me* and *Stop at*, it's for tonight only.
- **Play a playlist:** *Play* (and *Play shuffled*) on a playlist puts it in place of the queue and
  turns auto-play on, in one step. A song playing finishes first. Undo puts the queue back.
- **Double-click a song** to put it on the deck that plays next, never the one on air. With
  auto-play on, the song that was ready there goes back to the top of the queue.

Changed:

- **With auto-play on, changing the very next song keeps the one you had ready:** load another song
  onto the deck waiting to play and the song that was there goes back to the top of the queue,
  with Undo.

## 1.0.0 (2026-11-02)

GZJam is 1.0: ready for your next dance, on Windows, Mac and Linux. From here on, fixes come as
1.0.1, 1.0.2 and so on, and new features as 1.1. What's next:
[the roadmap](https://github.com/GemZombie/GZJam/blob/main/ROADMAP.md).

New:

- **Linux packages:** each release now has a `.deb` (Ubuntu, Mint, Debian), an `.rpm` (Fedora,
  openSUSE) and an Arch package (Arch, Manjaro), alongside the `.tar.gz`. Install it as your system
  installs any package, and remove it the same way; your settings and library stay. Installing a
  newer one over it upgrades GZJam. If you used `./install.sh` before, run `./install.sh --uninstall`
  from that folder first, so the menu has one GZJam.

Fixed:

- **On a new Linux account, GZJam keeps its library where it belongs:** if `~/.local/share` didn't
  exist yet, GZJam kept its library, settings and log in a `GZJam` folder wherever it was started
  from, and could seem to lose them the next time. Now it makes the usual folders.

## 0.9.14 (2026-11-01)

New:

- **Search the file name:** `file:` finds words in the file name alone. File names were already
  searched along with everything else; the guide now says so.
- **Words in quotes find a phrase:** `"duke ellington"` finds those words together, not one in
  the title and the other in a comment. Works in field filters too: `artist:"count basie"`.
- **Search filters you don't have to memorize:** start typing a filter's name and the search box
  suggests it; after `label:`, `artist:`, `genre:` and others it lists what's in your library, with
  how many songs each. A new **Filter** button beside BPM and Label taps in energy, hearts, when
  the room heard a song, the decade, or which field to search (*Search only in* › Artist). And the
  **?** in the empty search box shows how to search.
- **"Did you mean...?"** When a typo finds nothing (`duke elington`), the empty list offers the
  search corrected from words really in your library, with how many songs it finds. One tap
  searches that. Searching itself stays exact, so results never fill with near-misses.
- **OR in a search:** `label:balboa OR label:charleston` finds songs with either label;
  `basie OR webb`, `energy:high OR rating:yes` work too. Type it in capitals: a lowercase "or"
  is still an ordinary word.
- **Playlists take songs from anywhere:** drop songs onto a playlist from your file manager, even
  from outside your music folders. They join the
  playlist, not your library, so your collection stays as you've curated it. Importing playlists
  brings those songs along too, when they're on this computer. A song whose file has been moved
  or deleted stays in the playlist, shown as missing, rather than disappearing.

Changed:

- **Best matches first:** a search with words in it lists the songs whose title or artist has
  them first, then the ones that have them somewhere else, and last the ones that only mention
  them in a comment, so "worked with Duke Ellington" no longer sits among Ellington's own songs.
  Each group is in folder order, as before. A search by filters alone (`bpm:130-150`) and a
  playlist keep their order.
- **A check for played tonight, gray for missing:** songs played tonight were grayed out, which
  looked like a missing file. Now they have a green check before the title, and gray (with a "!"
  mark) means the file is missing.

Fixed:

- **A button's tooltip no longer gets in the way of clicking it:** near the bottom of the screen
  (*+ New* under PLAYLISTS, say), a tooltip could move up over the pointer, so a click closed the
  tooltip instead of pressing the button, and it took two or three tries. Now a button's tooltip
  sits just below it, or just above it where there's no room below.

## 0.9.12 (2026-10-31)

Fixed:

- **Right-click menus really no longer come out cut off:** 0.9.11's fix for this didn't reach the
  cause. On Windows the menu's window could take its size before the menu had worked out how wide
  it needed to be, and keep it, so the first right-click after starting (or after a new kind of
  menu, such as the first one in a playlist) cut off the right edge. Now a menu that comes out too
  small opens again straight away, the right size.

## 0.9.11 (2026-10-31)

New:

- **WMA files play on Windows:** GZJam left `.wma` files out of the library altogether. Now they
  appear and play on Windows, with their tags, and **Ratings in the song files** reads the stars
  Windows Media Player and MediaMonkey keep in them. On a Mac or Linux they're listed, and say
  they play only on Windows. A WMA bought online years ago may be copy-protected: GZJam says so,
  since only the player it was bought for can play it.

- **Drag songs from your file manager onto a playlist:** files and whole folders from Explorer,
  Finder or a Linux file manager went onto a deck or into the queue, but not onto a playlist. Now
  they can go onto a playlist's name, or into an open playlist where the line shows. A playlist
  holds songs from your music folders, so one from elsewhere (a USB stick) is left out, and GZJam
  says so.

Fixed:

- **Right-click menus no longer come out cut off:** the first time the song list's menu opened
  (in a playlist, say), its right edge could be cut off, hiding key hints such as *Del* and the
  arrows of its submenus. Opening it again showed it right. Now it's right the first time.

## 0.9.10 (2026-10-30)

Changed:

- **Load the next song onto the deck you've crossfaded away from:** GZJam protects the deck the
  room hears, so a song isn't loaded over it by accident. It protected a deck playing OFF AIR too,
  so after crossfading over you had to pause the old deck first. Now only the deck on air is
  protected: the other one takes the next song straight away, while it plays on quietly. In a
  blend, both decks are on air, so both stay protected.

Fixed:

- **A damaged .m4a no longer freezes GZJam:** when the start of an .m4a file is damaged, the
  part of GZJam that plays sound spent over two minutes trying to read it, and everything else
  waited: GZJam stopped responding, as if it had crashed. Now GZJam sees the damage at once and
  says so ("the file is damaged") when you load or preview the song.

## 0.9.9 (2026-10-30)

New:

- **iTunes playlists saved as text import too:** iTunes' *File › Library › Export Playlist* saves
  plain text unless you choose M3U. Settings › Library › **Import playlists** (renamed from
  *Import playlists (M3U)*) now takes those `.txt` files as well, including ones from another
  computer.

Fixed:

- **MP3s with a damaged tag play:** some MP3s have a broken block of tag data (often the album
  art) in front of the music. GZJam said it couldn't play them and listed them by file name only,
  with a length of 0:00. They now play, with their title and artist, and their length once
  they've been analyzed. The first time this version looks through your music it reads the tags
  again, once.
- **Playlists from another computer, more of their songs found:** a song moved deeper than the
  rest of its folder (`Music\Blues` now `Music\Archive\Blues`) is found, the counts before
  importing are right when two files have the same name, and playlists made on Windows work on a
  Mac or Linux.
- **The startup screen's logo is twice the size,** so it can be read.

## 0.9.8 (2026-10-30)

New:

- **Add a song to a playlist from a deck or the queue:** right-click the song on a deck (or open
  its ⋯ menu with four decks), or right-click songs in the queue, › *Add to playlist*. No more
  finding the song again in the library first.

Changed:

- **Other music programs keep playing while GZJam is open (Windows), on new installs:**
  *Speakers for GZJam alone* is now off to start with. On, it keeps notification sounds out of
  the room, but it also stops a music player or editor using the same speakers, with only a
  puzzling error there. Turn it on in Settings › Sound for a laptop kept for gigs. If you already
  have GZJam, your setting stays as it is.

Fixed:

- **The playlists stay where you scrolled them:** adding a song to a playlist sent the list of
  playlists back to the top, and with a playlist open, the song list too. Now both stay put, so
  adding songs to several playlists one after another is easy.
- **BPMs of 0 or 1 are gone:** some songs carry a junk BPM in their tags, and GZJam showed it
  instead of the BPM it detected. Those tags are now ignored. The first time this version looks
  through your music it reads the tags again, once, so the right BPMs appear.

## 0.9.7 (2026-10-29)

New:

- **Playlists from another computer import with their songs:** an M3U made where the music was
  somewhere else (`D:\Music\Swing` then, `C:\Music\Swing` now, or an old laptop's playlists from
  years ago) used to find none of its songs. GZJam now finds each one by its file name and the
  folder it's in, and says where it found them before importing. There's nothing to edit in the
  playlist files, and a song whose name only matches another album's is left out, not guessed.

## 0.9.6 (2026-10-29)

Fixed:

- **Resizing the queue's columns works:** dragging the line beside album, genre, year, played, BPM
  or time didn't follow the mouse. The column grew the other way, and the line was nowhere to be
  seen. Now each one shows a thin line, lit when the mouse is over it, on the side toward the
  title (before the column, unless you've moved it in front of the title), and it stays under the
  mouse as you drag. A column also stops short of pushing others out of sight.

## 0.9.5 (2026-10-28)

New:

- **EQ a song without loading it:** right-click it in the song list, a playlist, the queue or
  History › *EQ…*. The same EQ panel as a deck's opens beside it, with *Preview in headphones* to
  hear it while you set it. It's used when the song is loaded later, and a deck already holding
  it follows at once.
- **Export a playlist, the queue or a night as files:** right-click a playlist, the queue › *Export
  as files…*, or a History night's ⋯. Pick where (a USB stick, say): the songs are copied there in
  order into a new folder, as *01 - Artist - Title.mp3*, with a playlist file beside them that plays
  from wherever the folder goes. A spare copy of the night's set, or one to hand to another DJ.
  GZJam says first how many songs, how big, and the room there. It only copies: your files are
  never moved or changed, and nothing is overwritten.
- **Restore a backup on another computer, with the music somewhere else:** a new laptop, another
  drive, or from Windows to Linux. If the backup's music folders aren't there, GZJam asks where
  each one is now, offering the likeliest (a folder of the same name with the songs in it) and
  saying how many of its songs it found. Everything you gave your songs comes along.
- **With the decks hidden, their rows atop the queue show what the queue shows:** labels, the ♥,
  energy, album and the rest, as you've chosen in the queue's menu › *Show*, lined up with the
  queue's. The BPM is the one the room hears, with the deck's pitch.
- **Column names over the queue:** drag a name to put its column elsewhere (BPM before the title,
  say), and drag the line after album, genre, year, played, BPM or time to make it wider or
  narrower. Remembered, and the decks' rows atop the queue follow. They don't sort: the queue's
  order is the running order. Switch them off, or put the columns back as they came, from the
  queue's menu › *Show*.

Changed:

- **The headphone preview plays a song as a deck will:** with its EQ, its evening out and loudness
  matching. Before, previews played every file as it was.

Fixed:

- **Narrow song list columns keep their names and their text.** A column made thin lost its name
  long before it had to (BPM and Year went blank at about 50 pixels) and showed only a digit or two
  of the BPM. Now the name and the text show while they fit, and end in "…" when they don't.
- **A narrow queue no longer overlaps.** With the queue panel made thin, a song's title ran over its
  number and the BPM and time ran off the edge. Now what the queue shows gives way a part at a time,
  album first and BPM and time last, and the title and artist shorten with "…". The auto-play row
  and the Queue and History tabs fit too.

## 0.9.4 (2026-10-27)

New:

- **A queue as big as the song list: Ctrl+L** (Cmd+L on a Mac), or the arrow beside the queue's
  fold arrow. The queue takes the room in the middle, with the song list narrowed beside it, for
  nights spent reordering a long queue. The same key gives the song list the room back, and each
  way keeps its divider where you left it.
- **Choose what the queue shows:** right-click the queue › *Show*. Album, genre, year and when a
  song was last played can join the title, artist, BPM and time, and album art, the ♥, energy and
  labels can be left out. Remembered.
- **Grouping and Comment columns** in the song list (right-click a header to show them), read from
  the song files, where iTunes, Music and many other players keep them: the dance, the feel, the
  era. Long text is cut off, and the whole of it shows when you point at it. Grouping is searchable
  too, by plain words or `grouping:balboa`. The first scan after updating reads every song again,
  once, which takes a few minutes in a big library.

Changed:

- **Hidden decks sit at the top of the queue,** in the order the room will hear them: the deck it
  hears now, with its play key, time left and filling red bar, then the other deck, then the queue,
  so the running order is in one column. Drop a song on a deck's row to load it. With the queue
  folded away or History showing, the line under the top bar stands in for them as before.

Fixed:

- **The Energy column resizes** like the others: the line between Energy and Played wouldn't move.

## 0.9.3 (2026-10-27)

New:

- **A key for Settings: Ctrl+,** (the usual one, Cmd+, on a Mac) **or Ctrl+O,** O for Options.
  The same key again, or Escape, closes it.
- **The red on-air bar fills up as the song plays,** along the top of the deck the room hears:
  bright for what's played, dim for what's left, on every size of deck. It's full when the time
  left reaches 0:00.
- **Hide the decks** for the most songs on screen. One line under the top bar takes their place: the
  song the room hears with its play key, time left and on-air bar, and what's next.
- **A deck-size switch in the top bar:** full, compact or hidden, one click each, the size now lit.
  Also Ctrl+D (compact) and Ctrl+Shift+D (hidden), Cmd on a Mac, Settings › Decks › *Deck size*,
  and the grip under the decks: a little up for compact, further for hidden.
- **Fold the library or the queue panel away** with the arrow in its header, or by dragging its
  divider almost to the edge. It becomes a slim strip with its name, and a tap anywhere on the strip
  opens it again: no hunting for a divider at the screen's edge, where touch screens take swipes for
  themselves. The panels' widths are remembered too.
- **More room for playlists:** the arrow beside LIBRARY folds the folders down to your music folder,
  and the playlists take the rest of the column. Or drag the grip on the PLAYLISTS header to make
  the list longer or shorter. Both are remembered.

## 0.9.2 (2026-10-26)

New:

- **Full screen: F11,** and F11 again to go back (on a Mac, also Ctrl+Cmd+F). While it's on, a key
  at the right end of the top bar leaves it too, for a touch screen with no keyboard. Next time
  GZJam opens, it's maximized rather than full screen.
- **Keys for the decks, the crossfader and auto-play** (Cmd instead of Ctrl on a Mac):
  **Ctrl+1 / 2** play or pause a deck (two presses on air, if you've chosen two taps), **Ctrl+Shift+1 / 2**
  its CUE, **Ctrl+Alt+1 / 2** load the selected song onto it (protected as always),
  **Ctrl+← / →** glide the crossfader, **Ctrl+P** auto-play on or off. With four decks, 3 and 4 too.
  The buttons' tooltips say their keys. FADE OUT has no key on purpose. If a key doesn't work on your
  keyboard (a Mac, or a layout other than US English), please say so.

Changed:

- **Tapping or dragging along a position bar moves the song when you let go,** in the preview bar
  and on a deck's whole-song waveform. Until then it plays on, and the playhead follows your finger.
  A finger held on the bar made the sound stutter, since every tiny movement moved the song.

## 0.9.1 (2026-10-26)

New:

- **QUIT GZJAM in Settings,** at the bottom of the side column: a way to close GZJam on Linux
  desktops that draw no title bar (Hyprland), so no close button. It asks first while the room hears
  music.
- **A song added to the queue or a playlist it's in already** still goes in, but the top bar says
  so (*"Shiny Stockings" is in the queue already (5th)*), with **Undo** to take the new copy out
  again.
- **MAIN and PHONES volume from the keyboard:** Ctrl+↑ and Ctrl+↓ (Cmd on a Mac) turn MAIN up or
  down a little each press; with Shift, PHONES. They work from anywhere, the search box too.
- **Choose the loudness target:** Settings › Sound › *Loudness target*, under *Match loudness*.
  -16 LUFS as before, or -18, -20 or -23 for more headroom on a mixer or PA that clips easily (turn
  its channel up to make up for it). Quiet, peaky old transfers also come up closer to the rest.
- **The user guide: plugging into the venue's sound** (under *At a gig*): which input and cable,
  why mono, setting the levels so nothing distorts or hisses, a powered speaker with no mixer, and
  what to try for hum.
- **Auto-play can fade each song out after a set time** (1:00 to 5:00), for classes and practice
  nights: in auto-play's new **⋯** key, beside the gap. Each song fades out, then the gap, then the
  next; the deck's time counts down to the end of the fade. Songs shorter than that play to their
  end. What's set in the **⋯** key shows under AUTO-PLAY.
- **Auto-play can wait for you after each song** (in the **⋯** key): the next song is lined up and
  the crossfader moves over, but it waits for you (or the teacher) to press play.
- **Auto-play can stop at a set time** (in the **⋯** key): the song playing then is the last, and
  auto-play turns itself off. Waiting and the stop time are for tonight: they're off again the next
  time you open GZJam.

Changed:

- **New installs: the deck the room hears takes two taps to pause or fade out,** so a stray touch
  or click can't stop the dance. The first tap says *Tap again to pause* (or *to fade out*) and
  lights the key red. If you already use GZJam, your setting stays as it is; to switch, choose *Tap
  twice* in Settings › Decks › *Pausing or fading out the live deck*.
- **The top bar says the analysis of your songs is a one-time job.** On a new library it says
  *First-time analysis…*, then roughly how long is left; pointing at it explains what's happening
  and that GZJam is fine to use meanwhile. The Mixxx import says so too: the import takes seconds,
  the analysis after it is separate.

## 0.9.0 (2026-10-26)

New:

- **Compact decks,** for laptops: smaller decks so the library and queue show more songs. Switch
  with the arrows beside the gear, Ctrl+D (Cmd+D), or the grip under the mixer, as often as you like
  during a night.
- **Import playlists (M3U)** from MediaMonkey, foobar2000, iTunes, VirtualDJ and most other
  players: Settings › Library. Each file becomes a playlist of the same name, in order.
- **Ratings saved in the song files** (by MediaMonkey, foobar2000, Windows Media Player and
  others) come over as hearts for your unrated songs: 4-5 stars ♥, 1-2 a broken heart. Settings ›
  Library › *Ratings in the song files*. The files are only read.

Changed:

- **Fade out on air takes two taps too,** when you've chosen two taps to pause the deck the room
  hears (Settings › Decks › *Pausing or fading out the live deck*): a fade ends the dance as surely
  as a pause.
- **Space previews in the queue too,** as in the library.
- **The decks come back after a normal close too,** not just the queue: each with its song, tempo
  and cue button, paused at the start of the song. Closing GZJam no longer loses what was loaded.
  After a crash they still come back exactly where they were.
- **American spelling throughout:** *color*, *center*, *gray*, *license* and the rest, in GZJam
  itself (*Full license texts*, *Maximize*) and in the guide.

Fixed:

- **Delete in the queue** left nothing selected, so the arrow keys stopped working until you
  clicked in the queue again. The next song is selected now.
- **A song deleted beside a copy of itself** (same size and date) could be taken as moved onto
  the copy, which then got its hearts, cues and playlist places. With more than one music folder only.
- **Settings › About's links say where they go:** *User guide · GZJam on GitHub*. The project's
  address beside *User guide* read as the guide's own address, so it was clicked instead.

## 0.8.0 (2026-10-25)

New:

- **GZJam says when a new version is out.** At startup it asks GitHub for the newest release, and
  if there's one, says so quietly in the top bar, with a link to what's new and the downloads.
  Never during a set: it goes as soon as the room hears music. Close it and that version isn't
  mentioned again. Nothing about you or your music is sent, and nothing is downloaded or installed
  by itself. Don't want it? Settings › About › *Tell me when a new version is out*; off, GZJam
  never goes online by itself. To look whenever you like, even with it off: Settings › About ›
  *Check now*.

- **The night comes back after a crash.** If GZJam closes without warning, or the laptop dies or
  restarts mid-dance, the next start puts back the queue and the decks as they were: each song
  paused where it was, at its tempo, with its cue button. Nothing plays until you press play, and
  auto-play stays off until you turn it on. A song that's gone since (a drive not plugged in) is
  left out, with a note.
- **The queue is still there the next time you open GZJam,** after a normal close too, so it can
  be built ahead of a night. The decks start empty.
- **Back up your library, and restore it** (Settings › Library): hearts, labels, cues, trims,
  EQ, BPMs, playlists, History and your Settings, in one file to keep on a USB stick or in a
  cloud folder (your music files aren't in it). Restoring says what's in the backup and asks
  first, then GZJam opens again with it; the library it replaces is kept, not deleted. A backup
  from a newer GZJam is refused. GZJam also keeps a copy of its own, once a day it's opened, the
  last five beside the library.
- **A user guide:** getting started, the outputs, the decks, the library, auto-play, History,
  Settings, a checklist for the night and what to do if the sound stops. In
  [the user guide](https://github.com/GemZombie/GZJam/blob/main/docs/GUIDE.md), and linked from Settings › About.

Fixed:

- **Headphones unplugged during a night go quiet, and come back by themselves** when plugged in
  again, with no trip to Settings. The preview never moves to the speakers or anywhere else, and
  the room plays on untouched. A notice says so both ways, and CUE in the top bar says *Unplugged*
  meanwhile. The booth does the same.
- **With two taps to pause, a quick tap while the screen is frozen no longer pauses the room.**
  If the screen stalled for a moment during a tap, the tap could count as a press-and-hold and
  pause the deck on one tap. GZJam now goes by when the tap really started and ended.
- **A song moved to another music folder keeps its marks** (hearts, labels, cues, playlists,
  plays), as a song renamed within one folder already did.
- **A song in the queue or on a deck follows when it's renamed or moved:** it loads from its new
  name, and a deck that's playing plays on.
- **Settings › Sound warns about headphones on *Default*,** which follows the computer's own choice:
  unplugged, the computer may move the preview to another output, even the room's.

## 0.7.3 (2026-10-24)

New:

- **Play a song straight from the file manager.** Drag a file from Explorer, Finder or your Linux
  file manager onto a deck, or any number of files and folders into the queue. A song from
  outside your music folders, say one a dancer brings on a USB stick, plays like any other and
  shows in History, without joining your library. A song from your library keeps its marks.
- **Start over with an empty library:** Settings › Library › *Start over…*. GZJam closes and opens
  again with no music folders, for a fresh start or to add your music again from scratch. Your
  music files aren't touched and your Settings stay; the old library isn't deleted, but kept in
  a "previous libraries" folder beside it.

Fixed:

- **On a Mac, the menu bar says GZJam,** not "Avalonia Application".
- **The activity log says what the library is doing:** each scan of a music folder (songs found,
  read, added and removed, how long it took, and any error) and each time the library's list is
  rebuilt. On one Mac, adding a large folder left the library empty until GZJam was restarted;
  the log will now show why, if it happens again.

## 0.7.2 (2026-10-24)

Changed:

- **One output for both is a switch of its own,** easy to find: Settings › Sound › *One output for
  both (splitter cable)*, which says what to plug in. On, the Speakers and Headphones rows say
  which side each is on, and the top bar starts with *Left* and *Right*, where a long output name
  used to hide it. It used to be an entry in the Headphones list.

## 0.7.1 (2026-10-24)

Changed:

- **GZJam says who it's for:** Settings › About, and the app menu on Linux, now call it a simple
  DJ app for social dance nights.

Nothing else a DJ would notice. This is the release to try with the new
[testing checklist](https://github.com/GemZombie/GZJam/blob/main/docs/TESTING.md), on Windows, a
Mac or Linux.

## 0.7.0 (2026-10-24)

New:

- **Playlists: plan a set before the night.** Under the folder tree, *+ New* makes one; drag songs
  onto its name, or right-click songs › *Add to playlist*. Tap a playlist to open it in the song
  list, numbered, in its order: drag rows to reorder them, and Delete (or right-click) takes a song
  out of it, never out of your library. A song can be in a playlist twice. *Add to queue* puts
  the whole playlist on the end of the queue (right-click a song for *Add to queue from here*),
  and auto-play can run it. The queue's menu and a night in History can each be saved as a
  playlist too. Import from Mixxx brings your Mixxx playlists across, in their order.
- **A booth output:** a monitor speaker for you at the DJ table, for a big room where the
  speakers face away from you. Choose it in Settings › Sound › Booth: it plays what the room
  hears, at its own volume, and never the headphones' preview. Off unless you choose one.
- **Speakers and headphones from one output,** for a laptop with only a headphone socket: with a
  splitter cable, the room plays from the left side and your headphones from the right, each in
  mono. Choose it in Settings › Sound › Headphones › *Speakers' output, right side*.

Changed:

- **Menus stand out from the panels behind them:** a step lighter, with a brighter edge, so on a
  dim screen it's clear where a menu ends and the song list begins.
- **Faster searches in very large libraries.** With 100,000+ songs, the whole library lists about
  a third faster, BPM searches about a third faster, and `rating:`, `energy:` and `played:` are
  instant. Smaller libraries are a little faster too.

Fixed:

- **A song renamed or moved keeps its heart, energy, labels, cues and the rest.** Renaming a
  file (or its folder), in a tag editor or anywhere else, used to make GZJam treat it as a new
  song: its marks were lost from view, it dropped out of your playlists, and its Played column
  started over. Now they all go with it, whether GZJam was open or not, as long as it stays in
  the same music folder you added.
- **The BPM and Label menus by the search open with their choices again.** A click opened them
  empty, just a thin gray line under the button. A compact deck's ⋯ menu (with four decks) had
  the same fault.
- **A library a newer GZJam has updated no longer makes GZJam close without a word.** Going back
  to an older GZJam (or from a test build) used to make it vanish as it started; now it says
  your library is from a newer GZJam, and offers the newest one. Nothing is changed or lost.
  And from this version on, an older GZJam opens a library a newer one has updated, as long as
  the newer one only added to it (as most updates do).

## 0.6.0 (2026-10-24)

New:

- **Album art on the decks and queue,** small, to see at a glance that the right record is on the
  right deck. It's the picture in the song's file, or failing that the cover in its folder
  (`cover.jpg`, `folder.jpg`, `front.jpg`, or the folder's only picture). A song without one shows
  a dim record in the deck's color. The library has an Art column too, hidden to start with
  (right-click a column heading to show it): an album's songs all share one picture, so in a long
  list it doesn't tell them apart. GZJam keeps small copies of the pictures in its own folder;
  your music files are never changed, and nothing is downloaded.
- **Even out a recording.** For an old record whose quiet passages are hard to hear and loud ones
  too loud: the EVEN fader in a deck's EQ panel brings them closer together (a compressor). It
  works around the song's own loudness, so the same setting suits a quiet transfer and a loud
  one; at the top, passages 20 dB apart end up about 7 dB apart. Off at the bottom, and
  remembered for the song with its EQ.
- **Move the mid band.** Under the MID fader, the arrows move the band a third of an octave at a
  time, from 200 Hz to 5 kHz, to find just the frequency that's boomy or harsh. Tap the
  frequency for 1 kHz again. Remembered for the song.
- **Auto-play's gap shows what's coming.** Through the gap of silence between songs, the
  crossfader slides over to the next deck (the room is silent, so nobody hears it move) and that
  deck says *STARTS IN 3*, counting down, its light blinking, where ON AIR shows. Grab the
  crossfader during the gap and it's yours: auto-play leaves it where you put it.
- **The time readout blinks in a song's last 30 seconds,** as on CDJs: on the deck the room
  hears, counted to where its music ends (after trimmed silence, with the tempo and armed jumps
  taken into account). Time to have the next song ready.

Changed:

- **The dividers between the folders, library and queue have a grip** to show where to drag
  them, and in touch mode a finger needn't land exactly in the gap.
- **A deck's number is printed above its title,** as *DECK 1* in the deck's color, with ON AIR
  beside it while the room hears it, rather than in a colored square beside the title. The album
  art has the corner instead, and the title and artist have more room. With four decks it's just
  the number, and ON AIR sits just before the EQ key.
- **The close-up scrolls more smoothly zoomed in tight.** A deck reports where it is in uneven
  steps; the close-up used to make each small correction in one frame, a visible jerk when a
  couple of seconds fill the screen. Now each is spread over the next moments, and no single
  report can pull it far, so the music glides past (about five times steadier, measured), and
  it stays as close to what the room hears. Zoomed right in, the shape is smoother too.
- **Flat** in the EQ panel now puts everything back: the bands and gain at 0 dB, the mid at
  1 kHz, and evening out off.

## 0.5.0 (2026-10-23)

New:

- **A close-up of the song on each deck.** Above the whole song, a few seconds around the
  playhead scroll past it, beat by beat, so you can see the end of a phrase coming before you tap
  a cue key, and find the exact spot for a cue, a jump or where a song starts. The box on the whole
  song shows what the close-up covers. The mouse wheel or a pinch shows more or less (2 to 30
  seconds; 8 to start with). On a deck the room can't hear, drag the close-up to move through the
  song like a record, or click a spot to go there. Right-click or long-press it for the same menu
  as the whole song, with "here" just where you pressed. With four decks there's no room for both:
  the mouse wheel or a pinch on a deck's waveform zooms it in, and out again.
- **Drag the markers.** A song's start and end, either end of a jump, and its cues can be dragged
  along the close-up (for precision) or the whole song (for big moves), by mouse or finger. Let go
  and it's saved for the song. It works on air too, since moving a point changes nothing the room
  hears until the music gets there; somewhere a point can't go (a cue on top of another, say), it
  goes back and says why. A deck waiting at its start follows the start when you move it.
- **Tap tempo.** For a song whose BPM is wrong: on a deck, BPM menu › *Tap tempo…*, then tap the
  pad (or press Space) along to the beat. The BPM shows after four taps and settles the longer you
  tap; stop for two seconds to start again. SET makes it the song's BPM, saved in GZJam, not the
  file. With the deck's tempo changed, you tap what you hear and it works out the song's own BPM.
  It covers only that deck's waveform, so you can keep mixing while you tap a song in your
  headphones.
- **Labels on the decks and queue, and renaming them.** A song's labels now show after the artist
  on its deck and its queue row, as in the library, in whatever room the title and artist leave.
  Rename a label from Labels › *Rename a label* (right-click songs) or the Label button by the
  search; a search for it follows the new name. Rename it to another label's name and the two
  become one.
- **Choose how near `~bpm:` searches reach.** Settings › Library › *Near a BPM*: ±8% to start with
  (as far as the tempo slider goes), from ±1% to ±20%. The BPM button's *Near Deck* choices follow
  it. For one search, type `~bpm:140/5`.
- **A startup screen.** The logo shows the moment GZJam starts, just where its window will be,
  while it opens your library and outputs; then the decks appear. It's there only as long as that
  takes (about a second), never longer. On Windows and Mac; on Linux GZJam starts as before, for now.

Changed:

- **GZJam is ready about a second sooner** from when you start it: the downloads now come
  compiled ahead of time, rather than compiling as GZJam starts.
- **A new logo and app icon:** a waveform wrapped into a G, and the name in letters cut into bars
  like a waveform. It's in the top bar and on Settings › About too.

Fixed:

- **A hint or the "Analyzing…" label never makes a deck taller.** With four decks in touch mode,
  they used to push the deck down a few pixels while they showed.

## 0.4.0 (2026-10-23)

New:

- **A level meter on each deck,** along its edge beside the mixer. It shows the deck's level
  before the crossfader, so you can see a deck you're cueing is too quiet or too loud before the
  room hears it. The top light means as loud as the speakers go.
- **Cue keys: go to a point in the song at a tap.** Right-click (or long-press) the waveform ›
  *Cue 1 here* or *at the playhead*, up to four per song. A numbered key for each appears over the
  waveform, and a numbered flag marks the spot: tap the key and the deck carries on from there at
  once, on air too, at the moment you choose (wait for the end of a phrase, then go to the outro).
  With four decks, they're in the deck's ⋯ menu. Saved for the song in GZJam, never in the file.
  Import from Mixxx brings over your hot cues 1 to 4 as cue keys 1 to 4, for songs that don't have
  cues in GZJam yet.
- **Phones mix: hear the room in the headphones too.** A new fader under MAIN and PHONES blends
  what the headphones hear, from what's cued (CUE, where it starts: as before) to what the room
  hears (MAIN), with both at full in the middle. Keep one ear on the dancers while you preview
  the next song.

Changed:

- **One bar at the top on Windows and Mac:** GZJam's top bar is now also the window's title bar,
  giving the decks and library about 50 more pixels. Drag its empty space to move the window, and
  double-click it to maximize; minimize, maximize and close sit at its end. Linux keeps its own
  title bar where it has one.

Fixed:

- **Nothing on a deck moves when it starts or goes on air.** The ON AIR line used to appear above
  the title and push it, the waveform and (with four decks) the title down or sideways; its space
  is now kept.
- **The JUMP key fits a four-deck waveform** in touch mode, instead of making that deck taller.
- **With four decks, all four are the same height,** lined up with the mixer beside them. In
  touch mode the upper decks used to be taller than the lower ones.

## 0.3.0 (2026-10-22)

New:

- **Windows: the speakers are GZJam's alone.** While GZJam is open, no other program's sound (a
  notification, a browser) can reach the room through the speaker output. Settings › Sound ›
  *Speakers for GZJam alone*, on by default, for an output chosen by name (not *Default*). The
  headphones stay shared. If the card is unplugged mid-set, the music moves to the computer's
  default output and GZJam says so. When those speakers are also Windows' default output, Settings
  says other programs may refuse to play, and how to avoid it.
- **GZJam asks before closing while the room is hearing music**, so a stray click on the close
  button can't stop the set. *Keep playing* is the default (Enter and Escape choose it). Music only
  in the headphones doesn't count, and GZJam never holds up the computer shutting down.
- **Extra protection against crackles** (Settings › Sound): for a computer where the sound crackles
  or drops out. Play and the crossfader respond a little later. The activity log notes when the
  speakers' sound runs dry, and the README has a short troubleshooting list.
- **Read all tags again** (right-click a music folder in the library): for tags edited with a
  program set to keep the file's date, which GZJam wouldn't otherwise notice. GZJam also rereads
  every song by itself whenever it gets better at reading tags.

Fixed:

- **The speakers always open, even if the headphones can't** (they're silent, and GZJam says why).
  Before, both stayed shut and the decks played to nothing.
- **Headphones never end up in the room after an unplug.** When the speakers fall back to the
  computer's default output and that's where the headphones are (a laptop taken off its dock), the
  headphones are switched off, with a notice.
- **BPMs over 1000** (Pink Panther Theme showed 1180) for songs whose tags store the BPM ten times
  over, in libraries first scanned before 0.2.1. They're corrected once when GZJam starts, in the
  library and in History.

## 0.2.1 (2026-10-22)

The first public release. (0.2.0 was never released: a test failed on the Linux build machine.)

- **Your music folders as the library:** a folder tree, search that covers everything under the
  folder you pick, and filters like `bpm:130-150`, `~bpm:140`, `artist:`, `year:`, `label:`,
  `rating:`, `energy:` and `played:`.
- **Two or four decks:** tempo with key lock, one-button fade-out, waveforms, and protection against
  loading over a song the room is hearing.
- **Queue and auto-play** with a gap of real silence between songs (silence at the start and end of
  each file is found automatically).
- **Headphones:** a cue button per deck and a preview player on every song. The crossfader only
  affects the room.
- **Sound:** mono for speakers and headphones (on by default), loudness matching, per-song EQ,
  speaker and headphone volume, a level meter and a safety limiter.
- **Marking songs:** a heart or broken-heart rating, an energy mark, and labels.
- **History:** every night saved as a session, with set lists to export as text or a spreadsheet.
- **Track prep:** BPM detection with easy fixes, start and end points set by hand, and jumps (skip
  ahead or repeat a part at the end of a phrase).
- **Import from Mixxx:** ratings, crates (as labels), history and locked BPMs. Mixxx's own files are
  never changed.
- **Touch:** everything works by finger, with a touch mode for bigger keys and rows.
- **Outputs:** speakers and headphones on separate outputs. On Linux, a sound card can be used
  directly so other programs can't reach the PA, and outputs have clear names ("Built-in: HDMI 1",
  "Cable Creation (USB)").
- **Settings** in one place, by section: Sound, Decks, Screen, Library and About.

Plays MP3, FLAC, WAV, AIFF, OGG Vorbis, Opus and Apple Lossless everywhere, and AAC on Windows and
Mac.
