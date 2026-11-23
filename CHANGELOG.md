# FileHub changelog

Each version's program is kept in `Versions\Archive` and on the GitHub Releases page; its source is in the git history.

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
