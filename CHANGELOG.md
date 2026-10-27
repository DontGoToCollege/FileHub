# FileHub changelog

Each version's program is kept in `Versions\Archive` and on the GitHub Releases page; its source is in the git history.

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
