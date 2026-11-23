# FileHub changelog

Each version's program is kept in `Versions\Archive` and on the GitHub Releases page; its source is in the git history.

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
