# FileHub changelog

Each version's program is kept in `Versions\Archive` and on the GitHub Releases page; its source is in the git history.

## 0.3.48

### Fixed
- **Content indexing runs at full speed.** Windows was putting FileHub in efficiency mode (it runs from the tray with
  no window), so on a laptop with fast and slow cores the content pass ran on slow cores at low clock speed, about 3x
  slower. FileHub now turns efficiency mode off for itself while a content pass runs, and lets Windows decide again
  afterwards.
- **Bigger, faster saves during a content pass.** Files are saved up to 2,000 at a time (less for big files) instead
  of 500, and the search index skips its tidy-up on every save during the pass, then tidies once at the end. Together
  that's about twice as fast in tests. The progress count now moves in bigger steps.

## 0.3.47

### Fixed
- **Content indexing is much faster while files keep changing.** Every time a file changed during a pass, the pass
  stopped, read the changed files one at a time, then read the batch it had just finished all over again. On a busy
  computer that happened after almost every batch, so it crawled along at about 50-70 files a second on an NVMe drive.
  Changed files are now read in parallel while the pass carries on, the batch isn't read twice, and a file that's
  already indexed as it is now is skipped.
- **The one-time content index upgrade shows its progress.** It used to say only "Upgrading the content index" with
  no count, so it looked stuck. It now counts up ("12,500 / 300,000"). Clearing the content index also says it's
  clearing, then rebuilding, instead of looking like nothing happened.

## 0.3.46

### Fixed
- **The editor's keys keep working after its buttons.** After "Add separator", "Move to top" and the other buttons
  under the list, Delete, the arrow keys and Alt+Up / Alt+Down did nothing until you clicked a row, because the
  button kept the keyboard. The buttons now hand it back to the list.

## 0.3.45

### Fixed
- **Backspace and typing keep working when a folder opens in another view.** Going from a Details folder into a
  Large icons one (or back) rebuilt the list and left keyboard focus nowhere again, so Backspace and type-to-select
  did nothing until a click (0.3.42 only covered a folder with the same view). The list now takes focus back
  whenever it loses it to nothing while its window is active.

## 0.3.44

### Added
- **Set a view for a whole folder tree.** Right-click a folder (or empty space in one) → "Set view for this folder
  and subfolders" → Details, Large icons… FileHub writes that view into the folder's and every subfolder's own
  saved view once. It is a copy, not a rule: change any subfolder's view later and only that folder changes, and
  folders made afterwards start with your default view. Sort order and columns of each folder are kept.
- **Context menu editor (and Sort options): select, drag and move many rows.** Click a row to select it (it's
  highlighted), Ctrl+click and Shift+click (or Shift+arrows, Ctrl+A) to select more. Drag the selected rows to a new
  place; the list scrolls when you drag to its top or bottom edge. Buttons move the selection to the top, up, down or
  to the bottom (Alt+Up / Alt+Down too). Ticking one of several selected rows ticks them all; Space toggles them.
- **Add your own separators** in the context menu editor ("Add separator" puts one below the selected row;
  "Remove separator" or Delete removes ones you added; Windows' own separators are unticked instead).

### Changed
- **Scrollbars keep a thumb you can grab.** In a folder of thousands of files the scrollbar thumb used to shrink to a
  few pixels; it now stays at least about 56 pixels tall (shorter lists keep their larger thumb).
- In the editors, clicking a row's name now selects the row instead of ticking it; tick with the box on the left.

## 0.3.43

### Fixed
- **No more "not responding" when a hard drive is asleep.** Opening a file, launching a Start-menu result or pin,
  and right-clicking used to touch the drive on FileHub's window thread, so the whole window froze while a sleeping
  drive spun up (Windows then offered to end FileHub). Opening and launching now run in the background, so the
  window stays responsive and the program opens once the drive is awake. If a right-click's drive doesn't answer
  within a quarter second, the menu opens at once with FileHub's own entries and a note to right-click again for
  Windows' entries.
- **The Start menu no longer checks pinned items on disk while opening.** Each open checked every pin and folder
  on the window thread; on 2026-11-23 that froze the menu for 13 seconds while the thumbnail pass was reading the
  hard drives. The check now runs in the background and remembers its results, so icons still show straight away.

## 0.3.42

### Added
- **The right-click menu editor ("Context menu…") says where each entry comes from.** A new "From" column names
  the program that added it (WinRAR, 7-Zip, VLC media player, StartIsBack, FileHub, Windows…), with the entry's
  icon beside its name, so "Extract Here" is plainly WinRAR's. Entries a registry tweak added (like Take
  Ownership) show the command they run, such as "Command (cmd.exe)". FileHub finds this by asking each non-Windows
  menu handler which entries it adds (about 20–80 ms when the editor opens).

### Fixed
- **Backspace goes back right after you open a folder.** Opening a folder (Enter or double-click) replaced the
  list along with the row that had keyboard focus, so focus was left nowhere and Backspace / Alt+Left did nothing
  until you clicked. The list now keeps the keyboard when its folder changes.
- **Typing a name jumps to it.** Windows' built-in type-search in the list caught every letter (and compared it
  with full paths), so FileHub's own "type the first letters to select" never ran. It now works: type "A", Enter.

## 0.3.41

### Fixed
- **The Start menu stays open when the Win key opens it.** In 0.3.40 it took the keyboard but hid itself at once:
  taking the foreground sends the menu a stray "deactivated" message, which it took for a click elsewhere. It now
  closes only when Windows really puts another window in front (watched while the menu is open), so clicking any
  other window, the desktop or the taskbar still closes it.
- **More right-click entries show their icons.** Windows hands many icons over with handles that read as negative
  numbers, and 0.3.40 skipped those (VLC, MediaInfo and Take Ownership showed theirs only some of the time). Handlers
  that give their icon as the old "unchecked" bitmap (Windows' Share) now show it too.

## 0.3.40

### Added
- **Program icons in the right-click menu.** Windows' own entries (VLC, 7-Zip, WinRAR, Take Ownership and the like) show
  the icon their program gives them, as in Windows' menu.
- **Default-program badge on thumbnails.** In the icon views (medium and up), a file's thumbnail carries its type's
  small icon in the bottom-right corner, such as VLC's cone on videos, as Explorer draws it. Types whose registration
  asks for no badge (Explorer leaves those bare too) stay bare.

### Fixed
- **The Start menu takes the keyboard when the Win key opens it.** Typing goes straight to its search box instead of
  the window that was in front, and clicking anywhere else closes it, as Windows' and StartIsBack's do.
- **Checkboxes are readable in dark mode.** Ticks were invisible until the mouse was over the box (the Context menu
  window was the worst); checkboxes now use the theme, with a filled accent box when ticked.
- **A folder can't be dropped onto itself or into one of its own subfolders.** The drop is refused (no-drop cursor)
  instead of Windows' "The destination folder is a subfolder of the source folder".

### Testing
- `--test` mode has `--snap <title part> <file.png>`, which saves a window as WPF draws it (PrintWindow comes back blank
  while the screen is locked or off).

## 0.3.39

### Added
- **Drives are recognised by their partition, not their letter.** Each drive is known by its GPT partition ID (or, on
  older MBR disks and exFAT/FAT drives, its 64-bit volume serial), together with its file system and size, all kept in
  `drives.json` in the data folder. Reading these needs no admin rights and writes nothing to the drives. A drive under
  a new letter, or a portable drive moved to another PC, keeps its file list, its file contents and its thumbnails: the
  saved data moves to the new letter (500 rows at a time, shown on the Index status line), and only that drive is
  re-read for changes. Files that haven't changed are never read again.
- **Unplugged drives show as offline instead of being wiped.** Their files leave search results until they're back:
  the file list waits in `drives\` in the data folder, and the contents and thumbnails stay in the database.
  Hub → Index & database lists them as "offline · kept · last seen as G: <date>".

### Changed
- A new drive, or a folder added in Settings, is crawled on its own. A drive appearing, disappearing or changing letter
  no longer rebuilds the whole index. Drive letters are checked every 3 seconds.
- The thumbnail pass no longer deletes the thumbnails of drives that aren't plugged in.
- A watcher error from a drive that was just unplugged no longer starts a full rebuild.
- On first start, the data that 0.3.38 and earlier stored by drive letter is taken over by the drive under that letter.
  A drive that's unplugged at that moment can't be identified, so its old data is dropped, as before.

## 0.3.38

### Added
- **Drives table on Hub → Index & database.** One row per indexed drive: letter, name, kind (NVMe, SSD, removable, hard
  drive), and how far each pass has got there: names, contents & metadata, and thumbnails, as a percentage with the file
  counts ("37% (12,345 / 33,000)"). The drive a pass is on says "working", "waiting for contents" or "paused". A
  percentage never shows 100% while files are left; "—" means that pass hasn't run since FileHub started.

## 0.3.37

### Changed
- **The thumbnail status says where it is and why it waits.** While working it names the drive and its kind
  ("Thumbnails 1,200 / 272,588 (G: NVMe)"). While hard-drive thumbnails wait for content indexing (both read every file,
  so they don't run on a hard drive at once) it names those drives and how far content indexing has got
  ("hard drives E:, F: wait for content indexing (3,400 / 52,000)"). The log's "Thumbnails: making" line splits the
  count by drive.

## 0.3.36

### Added
- **The log now catches FileHub freezing.** Any moment its windows are stuck for over 300 ms is logged (`UI thread busy N ms`);
  one over 1 s also saves what every thread was doing to `logs\stall-<time>.txt` (needs dotnet-stack, installed by rules-band).
  Opening a file whose launch takes over 300 ms inside FileHub is logged too (`Slow open`). This is to find why opening a file
  sometimes takes seconds: a quiet test opened an image in about 0.45 s, with FileHub's own part about 30 ms.

## 0.3.35

### Fixed
- **Deleting a big folder could freeze FileHub for seconds.** Removing a folder from the index scanned the whole index once per
  subfolder (a folder with 5,000 subfolders: about 7 s, during which folder listings and searches waited). It now takes one pass
  (5,000 subfolders: a few milliseconds).

## 0.3.34

### Fixed
- **Exit from the tray could leave FileHub running.** A program renaming a file in some ways (Brave does it) made Windows
  report the drive itself (C:\) as the old name. FileHub then re-read every document on that drive in one go: gigabytes of
  memory, a full CPU core for half an hour, and the database held the whole time, so closing windows on Exit waited for it.
  That drive-wide re-read no longer happens.
- **A big folder moved or renamed is re-indexed 500 files at a time**, so its text is never all in memory and the database
  is only ever held briefly.
- **Exit always ends the program.** If something still holds it up, FileHub stops anyway after 15 seconds. Nothing saved
  is lost: an unfinished database write is simply rolled back.
- **Folder views stick when you go back and forth.** Switching a folder to Large icons, opening another folder and coming
  back showed Details again: the view was saved in the background and the save could wait behind the drive-wide re-read
  above. A view now applies the moment it's set, and the newest one is what gets saved.

## 0.3.33

### Changed
- **Window titles show the version.** Explorer reads "Home - FileHub - ver. 0.3.33" and the Hub "File Hub - ver. 0.3.33",
  so it's easy to see which build a PC is running.

## 0.3.32

### Changed
- **The log shows how fast each drive's contents are indexed.** When the content pass finishes a drive it writes one
  line, for example "Drive E: contents done, 12,400 files in 38 min (5 files/s)", so a slow hard drive is easy to spot.
- **The log names the version.** Each start now reads "FileHub 0.3.32 starting", so a sent log shows which build ran.
### Fixed
- **"Idle" no longer shows while indexing is still at work.** Resuming after a pause showed "Content indexing idle" and
  "Thumbnails idle" while the pass was still finishing the files it was reading (minutes on a hard drive); it now reads
  "resuming…" until the pass reports progress. A pass cut short to start over no longer flashes "up to date" either.
  Finished passes read "Content index up to date (…)" and "Thumbnails up to date (…)"; "idle" only means none is running.

## 0.3.31

### Changed
- **Every screen shows the same indexing status.** The Hub's side panel and Hub → Index & database now list
  thumbnails too, next to the name index and file contents, exactly as Explorer's status line does ("Thumbnails
  8,800 / 45,663"). All three now come from one place, so a background task added later appears on all of them at
  once. Each line names its task ("Thumbnails idle", "Content indexing paused at 12,000 / 390,000"). The Start menu
  still shows none.

## 0.3.30

### Fixed
- **Thumbnails are made while content indexing runs.** The thumbnail pass used to wait until content indexing had
  nothing left to do, so on a PC still working through a big first content pass (hours with hard drives) no
  thumbnails were made ahead of time at all, and nothing said why. Now both run together on NVMe, SSD and removable
  drives; only a hard drive's thumbnails wait for content indexing to finish (two passes reading one hard drive at
  once would slow both), and the status line then says "Thumbnails 12,000 / 45,000 (hard drives wait for content
  indexing)".

## 0.3.29

### Fixed
- **Folder previews no longer sit on a white square.** Some of Windows' folder previews (seen at Extra large icons)
  come back with "straight" transparency, where see-through pixels still carry white; FileHub read them as the other
  kind and painted that white on. It now tells the two apart.

## 0.3.28

### New
- **Thumbnails are made ahead of time.** A background pass makes a thumbnail for every picture, video and PDF in your
  folders and elsewhere (not Windows, program or code-library folders: theirs are made when you open such a folder,
  then kept), so icon views show them at once instead of when a folder is first opened. It runs after the content
  pass, in the same drive order (NVMe, SSD, removable, hard drive; a hard drive one file at a time), and files you save
  or change get theirs right away. Explorer's status line shows "Thumbnails 1,200 / 45,000" while it works. On this PC:
  45,663 thumbnails in about 90 seconds.
- **Small icons show thumbnails too**, like Medium, Large and Extra large.

### Changed
- **Thumbnails live in their own file, thumbs.db**, next to meta.db, so making thousands of them never waits on content
  indexing (or the other way round) and they can be cleared on their own. One 256 px thumbnail per file serves every
  view size (scaled while loading, under 1 ms). They're stored as JPEG (about 13 KB for a photo, against 88 KB as
  PNG before), or PNG when the image has see-through parts. 45,663 thumbnails take 0.6 GB. Thumbnails of deleted files
  are dropped, and free space goes back to the disk on its own. The old cache in meta.db is removed.

## 0.3.27

### Faster
- **Content indexing goes drive by drive, fastest drives first, and no longer slows down hard drives.** FileHub now
  asks Windows what each drive is: NVMe drives are indexed first, then other SSDs, then removable drives, then hard
  drives (drives of the same kind in letter order). On each drive, small files go first and the largest last, each
  group A to Z by folder.
- **Hard drives are read one file at a time again**, in folder order, while SSDs and NVMe drives are read on several
  threads. 0.3.25 read every drive with several threads at once, which sent a hard drive's head jumping between
  folders and made it slower than before. A drive Windows can't describe is treated as a hard drive. The log notes
  what each drive was taken for ("Drive D: hard drive …").
- On this PC's NVMe a fresh content index now fills at about 2,000 files a second while it works through the small
  files (it was about 500).

## 0.3.26

### Faster
- **Small files are indexed first.** Within each place (your folders, then elsewhere, then Windows and programs),
  content indexing now does files under 64 KB first, then under 1 MB, then the big ones. Storing a file costs about
  what its text weighs, so the number of searchable files climbs much faster; the few big files fill in last. Files
  in the same folder are still read together, so a hard drive doesn't have to jump around. Photos and other files read
  only for their details count as small. Every file is still indexed in full.

## 0.3.25

### Faster
- **Content indexing reads several files at once.** It used to read one file, store it, then read the next, so on a
  hard drive every file waited for the disk (35–80 files a second). Now 2–8 files are read at the same time while the
  previous 500 are being stored, so a slow drive no longer holds everything up.
- **Your own files are indexed first.** A first run or a repair now starts with your folders (Desktop, Documents,
  Downloads, Music…), then everything else, then Windows, programs and code libraries, so the files you look for
  become searchable within minutes instead of somewhere in the middle of a million.
- The progress count now covers only the files that still need reading.

## 0.3.24

### Changed
- **Explorer's status line shows when file contents are being indexed.** Next to "Index: N items" it now says
  "Indexing content 12,000 / 389,000" (or "Repairing the content index…") while that runs, and goes back to the item
  count when it's done. Before, only Hub → Index showed it, so a content repair or first run looked like nothing
  was happening.

## 0.3.23

### Fixed
- **A damaged content database now repairs itself.** If meta.db gets damaged (SQLite: "database disk image is
  malformed"), content search used to find nothing and the content indexer failed every 5 seconds, for good. FileHub
  now notices on the first failed search or write, replaces meta.db with a fresh one and reads your files again in the
  background. Folder view settings are carried over when they can still be read; thumbnails are simply made again.
  Nothing to click. It happens at most once per run, so damage that keeps coming back can't loop.

## 0.3.22

### Fixed
- **The Start menu searches file contents on every PC.** "Also search file contents" was off unless you ticked it on
  that PC, so on a PC where nobody had, typing a word that's only inside a file found nothing. It's now on by default,
  and switched on once for existing installs (you can still turn it off in Hub → Start menu).

## 0.3.21

### Faster
- **Typing in the Start menu and Hub is quicker once file contents join in** (from the third letter). Putting your
  own documents first among the content matches now runs on every core: "want" 60–88 → 42 ms, "report"
  93 → 67 ms.
- **Sorting by Type or Extension is about twice as fast** (it no longer copies two extensions per comparison):
  10,000 results 44 → 18 ms.
- **Sorting a big folder in Explorer no longer freezes the window.** The sort runs in the background and the list
  updates when it's done (a folder of 81,000 files took 0.3 s, during which the window didn't respond).

## 0.3.20

### Faster
- **Sorting by name, Z to A, is as fast as A to Z.** Names that start with a non-English letter (한국어, Русский, é…)
  come last in A to Z, so they come first in Z to A, and every comparison against them used to take the slow path.
  Now the quick check knows those letters sort after English ones. All 785,000 results for "e", by name, either
  direction: 73–88 ms in the Hub.

## 0.3.19

### Faster
- **Sorting big search results is fast.** The Hub shows the first 5,000 results but used to sort every match: sorting
  all 1.1 million files by name took 7 s, by path several seconds. Now each core keeps only its best 5,000, and name
  sorts compare a short key per result before reading names. Every file sorted by name: 7 s → 0.3 s; by path: 0.3 s
  (100k results took 2.8 s before); by size or date: under 0.1 s. Typing with a sort picked no longer lags.
- **Sorting a folder by name is up to 6× faster.** Names that start the same (common in big folders) used to be
  compared letter by letter from the start; the identical part is now skipped in one step. A folder of 81,000 files:
  2 s → 0.3 s; 25,000 files: 170 → 73 ms. The order is exactly the same as before.
- Sort by Path now keeps each folder's files together (folder by folder), and works on any number of results; it used
  to give up on sorting past 400,000.

## 0.3.18

### Faster
- **Photo thumbnails appear several times faster.** JPEG, PNG, BMP, GIF, TIFF and WebP thumbnails are now decoded
  by FileHub itself on several threads at once, instead of queueing for Windows' thumbnailer, which makes them one
  at a time (~23 ms each). A folder of 210 new photos: 4.9 s → 0.7 s; a screenful of Large icons: all 20 within
  0.1 s. Videos, PDFs and other files still use Windows' thumbnails. Photos taken sideways are turned upright from
  their camera tag, as before.
- Saving a thumbnail to FileHub's cache no longer delays it showing (the save happens afterwards).
- More loader threads on PCs with more cores (half the cores, 3 to 8).

## 0.3.17

### Smaller, and it stays that way
- **meta.db looks after itself.** It had grown to 7.8 GB, of which 5.5 GB was empty space left behind by the 0.3.16
  content-index upgrade and never given back. The first time 0.3.17 runs, it compacts the file once in the background
  (seconds; searches keep working), then switches it to give free space back on its own. After that, every 30 minutes
  (after each content pass) FileHub returns unused space to the disk in small steps and merges the content index into
  one piece when it has split into more than 10. Nothing to click, on every PC.
- Content searches get a little faster from the tidier index (ranked "meeting": 18 → 11 ms).
- Clear thumbnails no longer runs a whole-database VACUUM in memory (on a multi-GB file that needed GBs of RAM).
- Saving a thumbnail to the cache never waits more than 0.3 s on other database work; it skips the cache instead.

## 0.3.16

### Faster
- **Content searches are instant again.** `content:wants` took 27 s with the type tabs (each click re-ran it): matching
  the results to files scanned the whole 1.1-million-entry index once per folder. Now it's a lookup (27 s → 25 ms;
  Documents tab 31 ms).
- **As-you-type content search no longer stalls the Start menu.** The content index now has prefix indexes for 3- and
  4-letter word starts, and half-typed words found in thousands of files skip the costly best-match ranking ("con":
  1.4 s → 0.1 s).
- **Smaller content index.** It no longer keeps a second copy of every file's text (FileHub never reads it back).
  The first start of 0.3.16 converts the existing index in the background (about 5 minutes; search keeps working).
  The database file keeps its size until the freed space is reused.

### Bug fixes
- A typed `content:` search in the Start menu keeps the best-match order instead of re-sorting by name length.
- Files saved the "safe" way (write a temp file, rename it over the original) no longer leave a duplicate entry.

## 0.3.15

### Bug fixes
- **Content search works from a plain search everywhere.** The Hub and Explorer only looked inside files when you
  typed `content:`; now any plain search of 3+ characters also lists files whose contents match, after the file-name
  matches (Explorer still sorts the name matches by the folder's sort). The Start menu (with "Also search file
  contents" on) keeps at least a third of its list for content matches, so file names can't crowd them out, and
  matches word beginnings like everywhere else.
- **Files saved while the content indexer is busy are indexed right away.** During a long pass (the first run, or
  the check every 30 minutes) changed files waited until the whole pass finished, which could take many minutes.
- **Moved, renamed, copied or newly created folders get their contents indexed straight away** instead of at the
  next 30-minute pass.
- **Files an app re-creates while saving are re-read**, and short 8.3 names from Windows (`NEWTEX~1.TXT`) no
  longer add duplicate entries.
- **Content searches are about 25× faster** (e.g. 680 ms → 50 ms): the database no longer loads each matching
  file's full text just to get its path.

## 0.3.14

### Bug fixes
- **Content searches in an Explorer folder keep the best-match order.** In a real folder (not Home or This PC) the
  results were re-sorted by the folder's sort (usually Name), so your own documents ended up wherever their name
  fell among hundreds of matches. They now stay best match first, like Explorer's search results by relevance;
  picking a sort while searching still re-sorts them.

## 0.3.13

### Changed
- **Content searches list your own documents first.** `content:` results used to come in disk order, so a note on
  the Desktop could sit behind 1,500 program and library files that mention the same word. Now (when no sort is
  picked) they're ordered: your folders (Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive) first,
  then elsewhere, then Windows, program folders, app data and code libraries (venv, node_modules…); within each,
  documents (txt, md, docx, pdf…) before code, then best match first.
- Quieter log: the "folder removed from index" line for every temp folder is gone; removals of 10,000+ entries are
  still logged.

## 0.3.12

### Bug fixes
- **The index can no longer wipe itself.** On top of 0.3.11's fix:
  - A "deleted" or "moved away" report for something that's still on disk is ignored (apps that delete and re-create
    a file while saving, late or garbled watcher reports).
  - FileHub refuses to save an index that lost more than half its entries without a rebuild; it keeps the good
    snapshot on disk and rebuilds instead, and logs why.
  - Any single change that removes 10,000+ entries is logged with the folder and the counts.
- **Sort options and Context menu lists:** clicking a row's name, or anywhere in its tick column, ticks it (before,
  only the small box itself reacted), and ticks made with the keyboard or accessibility tools now count.

## 0.3.11

### New
- **Sort by shows the current sort and order** with a tick, like Explorer, in the right-click menu too, and each
  sort's hotkey beside it.
- **Sort hotkeys:** Ctrl+Alt+1 Name, 2 Date, 3 Date modified, 4 Type, 5 Size, 6 Date created (press again to flip the
  order), Ctrl+Alt+PageUp Ascending, Ctrl+Alt+PageDown Descending.
- **Sort by → More…** (also Sort options → "Choose sorts and hotkeys…") opens a large list like Explorer's: tick which
  sorts Sort by shows, put them in order, and set or change each one's hotkey. Besides the basics (now with Location)
  it offers file details FileHub indexes: Title, Author, Tags, Dimensions, Duration, Artist, Album, Genre, Year, Date
  taken, Camera, Rating and more. It warns when two sorts share a hotkey.
- **"Context menu…"** at the bottom of every right-click menu (after Properties): tick which entries show and put
  them in order, for that kind of thing: one file type (e.g. .txt files), all files, folders, files and folders
  together, or empty space in a folder. Windows' own entries are included; "Reset to defaults" undoes it.

### Bug fixes
- **The index could suddenly empty itself.** A file-watcher event that named the drive itself (it happens when Windows
  loses track of a rename) removed the whole drive from the index, and the next save stored an empty index. Such
  events are now ignored, and every folder removed from the index is logged.

## 0.3.10

### New
- **Choose what's in Sort by:** Sort options → "Show in Sort by" ticks which sorts the Sort by menus offer (Name, Date,
  Date modified, Date created, Type, Size, Extension). With "Remember sort for each folder" each folder can have its
  own list (folders you haven't changed use the default list); with "Use the same sort for all folders" it's one list
  everywhere. "Reset this folder to the default sort" also restores the default list.
- **Explorer's view keys:** Ctrl+Shift+1 Extra large icons, 2 Large, 3 Medium, 4 Small, 5 List, 6 Details (shown in
  the View menus).

### Changed
- **Date sorts newest first** when you pick it; going from Date to another sort goes back to A→Z.
- **Details view: the area to the right of the last column is empty space,** like Explorer. Clicking it clears the
  selection or starts a drag-select, and right-clicking it gives the folder menu (View, Sort by, New, Paste…).
  Clicking inside the columns still selects the row.

### Bug fixes
- **Ctrl+Shift+N makes a new folder** again; it opened a new window instead.

## 0.3.9

### New
- **Move data folder…** in Settings → General (next to Open data folder): pick a folder and FileHub closes, moves its
  data and logs into a `FileHub-Data` folder there, and starts again. Works across drives (it copies, then removes the
  old folder; several GB can take a few minutes). It refuses a folder that already has files in it. The chosen
  location is remembered in `FileHub-Data.location` next to `FileHub.exe`; pick the exe's own folder to go back to the
  default. Also available as `FileHub.exe --move-data <folder>`.

## 0.3.8

### New
- **Folders show what's inside them** in Medium, Large and Extra large icons views, like Windows Explorer: pictures and
  videos peek out of the folder.
- **"Date" sort option:** sorts photos by when they were taken and everything else by date modified.
- **Sort options** at the bottom of Sort by (toolbar and right-click menu): remember the sort for each folder (as
  before), or use the same sort for all folders, plus "Reset this folder to the default sort".
- **FileHub keeps its data next to the program**, in a `FileHub-Data` folder beside `FileHub.exe`, instead of
  `%LocalAppData%\FileHub`. The old folder is moved there the first time this version starts. (If the program's folder
  can't be written to, or the move fails, it keeps using the old folder.)

### Bug fixes
- **Picking the current sort again flips its order** (A→Z ↔ Z→A), like clicking a column header, instead of needing
  Ascending/Descending.

## 0.3.7

### Bug fixes
- **Search no longer finds text in files that were moved, renamed or deleted along with their folder.** Renaming or
  deleting a folder left its files' text and details in the content index under the old location; renaming a file
  left its old name there too. They're now removed right away (the new location is picked up by the next scan).
- **Cut and paste into the same folder does nothing,** like Explorer, instead of showing a Windows error.
- **Saving the index retries for a moment** if another program (such as a virus scanner) briefly holds its temp file,
  instead of failing.
- Fixed an internal mix-up when a file or folder was moved to a different folder while FileHub was watching.

## 0.3.6

### Bug fixes
- **Drop-down boxes are readable in dark mode.** They kept Windows' light look with light text (and the open list was
  white), so settings like Position, Style, Tile size and Search box were nearly invisible. They now follow the
  theme: dark box, readable text, themed list with hover and selection highlight, and an arrow like the rest of the app.

### Other
- `--page <name>` opens the Hub on a page (e.g. `--page startmenu`).

## 0.3.5

### Bug fixes
- **Clicking a folder inside a multi-selection now selects just that folder.** It used to keep the whole selection
  when the clicked item was the first selected one (usually a folder, since folders sort first), while clicking a file
  worked. The same fix applies to clicking a row's blank area, type-ahead, drag start and right-click.

## 0.3.4

### Other
- **Background test mode** (`--test`, for automated checks): windows open off-screen without ever taking focus or
  staying on top, the start menu stays open while unfocused, and `--set-text [Title:]Name "text"` fills a text box
  without focusing it. This lets FileHub be checked while you keep using the PC. Normal launches are unaffected.
- Verified with it: typed text in the Start menu and Explorer search boxes now starts exactly at the hint (0.3.3 fix).

## 0.3.3

### Bug fixes
- **Typed text in search boxes was shifted right.** Every text box applied its padding twice, so text and the cursor
  started well to the right of the "Searchâ€¦" hint (Start menu, Explorer search, and other boxes). Text now starts
  exactly where the hint does.

## 0.3.2

### Enhancements
- **Drag-select (rubber band) in Explorer.** Drag from empty space, or from a row's blank area right of the Name
  column in Details view, to select everything under the box. Ctrl toggles and Shift adds to the earlier selection;
  dragging past the top or bottom edge scrolls and keeps selecting. Works in Details, List and icon views.
- **Start menu customization** (Hub > Start menu > Layout), with the original look kept as the default:
  - Style: Tiles (icon grid + folder chips) or List (two columns: pinned list and folders, like classic start menus).
  - Search box at the top or the bottom; tile size small / medium / large; menu width and height; show or hide the
    Windows folders section; "Reset layout to defaults".
- **Pin and unpin from anywhere.** "Pin to FileHub Start" / "Unpin from FileHub Start" in the right-click menu of any
  file or folder (Explorer, Hub, Start search results). In the start menu, right-click a tile to unpin it, move it
  (to front, earlier, later) or open its location, or drag tiles onto each other to reorder; right-click a folder
  shortcut to remove it.

### Bug fixes
- **Explorer search box tooltip** opened as one very wide line pushed away from the box; it is now wrapped and
  anchored directly under the search box, and waits a little longer before appearing.
- **Start menu position** is recalculated once the window exists, so the first opening can't use a wrong DPI guess.
  Search result paths in the start menu are trimmed instead of showing a horizontal scrollbar.

## 0.3.1

### Bug fixes
- **File changes are no longer lost when FileHub closes.** The name index is now saved on exit when real-time
  indexing changed it since the last save (before, up to 10 minutes of new, renamed or deleted files were forgotten,
  and files made just before closing could not be found until the next scheduled rebuild).
- **Changes made during a cancelled rebuild are kept.** They are now applied to the current index instead of dropped.
- **Sorted searches no longer crash when a file is deleted mid-search.** Sorting by name, type or extension and the
  Start menu ranking now skip files the live indexer removed while the search was running.

## 0.3.0

### Bug fixes
- **Content and metadata indexing no longer takes days.** Saving each file's text used to scan every document already
  stored (143 ms per file with only 2,300 documents, and slower as the database grew). Text rows are now found by row ID,
  so a save takes well under 1 ms no matter how big the database gets. Existing indexed content is carried over
  automatically on the first start (a one-time pause of a few seconds).
- **Content search works again.** Every content search failed with a SQL error ("LIMIT clause should come after UNION")
  and silently returned nothing, in the Hub search and in Start menu content search.

## 0.2.0

### Enhancements
- **Hub hotkey takes priority over other apps.** Ctrl+Alt+Space (or whatever is set) is now captured by FileHub's
  low-level keyboard hook before apps that registered the same hotkey (e.g. Claude desktop) can see it.
  RegisterHotKey is only used as a fallback if the hook cannot be installed.
- **Win key takes precedence over StartIsBack and the Windows Start menu.** The physical Win press is now fully
  held back, so no other start-menu program ever sees a bare Win tap. When another key is pressed with Win,
  FileHub replays "Win + that key", so Win+R, Win+D, Win+Shift+S, Win+Tab etc. keep working. Win+L locks directly.
  The hook runs on its own high-priority thread and re-installs itself every 30 s so it stays first in line.
- **Alt+Left / Alt+Right / Alt+Up** now navigate back / forward / up (WPF reports Alt combos as a "system" key;
  that was not being translated). Alt+D, Alt+P and Alt+Enter are fixed by the same change.
- **Modern scrollbars.** Thin, rounded, theme-aware scrollbars (light/dark) that widen on hover, everywhere in the app.
- **Pause indexing** toggle (Explorer status bar and Hub sidebar): pauses crawling, live change processing
  (changes queue up and are applied on resume) and content/metadata extraction.
- **Real-time indexing** toggle (Explorer status bar and Hub sidebar, remembered across restarts): new, changed,
  renamed and deleted files are applied to the index as they happen without a rebuild. Off = the index only
  changes on a rebuild.

### Bug fixes
- **Slow folder opening in Explorer.** Navigation now works in layers:
  1. the folder opens instantly from the in-memory index (no disk access);
  2. the real disk listing is read in the background and swapped in *in place* only if something differs
     (selection and scroll position are kept);
  3. the saved per-folder view, sorting and file-type names are all prepared off the UI thread.
  Also: SQLite now uses a private cache with WAL so reads never wait behind the content indexer's writes;
  the file watcher ignores FileHub's own data folder (its database/log writes were feeding back into the
  indexer); duplicate change events are coalesced; per-folder child lookups in the index are cached instead of
  scanning every entry; watcher-overflow rebuilds are limited to one per 30 minutes; F5 refresh keeps selection
  and scroll. Folders that still take more than 400 ms are logged as "Slow folder load" in the log file.

## 0.1.0
- Initial shell: indexed Explorer, File Hub, Start menu replacement, context menus, settings pages.
