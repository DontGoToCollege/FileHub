# FileHub changelog

Each version's program is kept in `Versions\Archive` and on the GitHub Releases page; its source is in the git history.

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
  started well to the right of the "Search…" hint (Start menu, Explorer search, and other boxes). Text now starts
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
