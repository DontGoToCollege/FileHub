# FileHub

A Windows 11-style file explorer replacement built on an Everything-style in-memory index, with a
Start-menu replacement and a "hub" window for power search and configuration.

.NET 8 · WPF · single-file self-contained x64 executable · no installer, no admin rights.

## Versions

Current version: see `<Version>` in `src\FileHub\FileHub.csproj` and [CHANGELOG.md](CHANGELOG.md).
Every build is kept: the current one at the top level, and a copy per version in `Versions\Archive` (not in git; each version's program is also on the GitHub Releases page). A version that's already archived is refused, so raise `<Version>` in the csproj first.

## Build & publish

```powershell
.\publish.ps1          # -> dist\FileHub.exe (self-contained, single file)
.\publish.ps1 -Run     # publish and start it
```

or `dotnet build src\FileHub\FileHub.csproj` for a debug build.

## What is in the shell

| Area | What it does today |
|------|--------------------|
| **Index** (`Core/Indexing`) | Crawls every fixed drive with the Win32 large-fetch enumerator into a flat in-memory index (name, parent, size, dates, attributes). Persisted as a binary snapshot (`%LocalAppData%\FileHub\index.fhdb`) so it is searchable instantly on the next start; rebuilt in the background when stale. FileSystemWatchers apply live changes. |
| **Database** (`Core/Database`) | SQLite `meta.db`: FTS5 full-text of file contents (text/code/Office XML), extracted Windows properties (title, author, dimensions, duration, camera…), thumbnail cache (PNG blobs), per-folder view settings. |
| **Search** (`Core/Search`) | Everything-style query language: AND/OR/NOT/groups, wildcards, regex, `ext:` `type:` `size:` `dm:` `dc:` `path:` `infolder:` `parent:` `content:` `meta:` `attrib:` `startwith:` `endwith:` `case:` `ww:` `len:` `empty:`. Parallel scan of the index, live as you type. |
| **Shell integration** (`Core/Shell`) | Real Windows context menu (`IContextMenu`) rendered inside WPF menus, icons/thumbnails via `IShellItemImageFactory`, copy/move/delete/rename through `SHFileOperation` (progress UI, undo, recycle bin), clipboard cut/copy/paste compatible with Explorer, Open-with / Properties dialogs, Windows property store reader. |
| **Explorer window** (`UI/Explorer`) | Tabs, back/forward/up, breadcrumb + editable address bar, navigation pane (Home, user folders, quick access, This PC with lazy tree), Details / List / Small / Medium / Large / Extra-large views with a virtualizing tile panel, columns & sort saved per folder, in-folder search using the index, inline rename, type-ahead, drag & drop, preview pane, command bar, keyboard shortcuts. |
| **File Hub window** (`UI/Hub`) | Big search: filter chips, regex/case/word/path toggles, sort, thumbnail grids, preview pane, syntax help. Settings pages: Index & database, Context menu (custom items + shell-menu mode), File types & open-with overrides, Folder views & Explorer defaults, Start menu, General. |
| **Start menu** (`UI/Start`) | Win-key tap opens FileHub's start menu (low-level keyboard hook; Win+shortcuts keep working), Win+E opens FileHub Explorer. Search box with ranked quick results (apps first) and "See more results in File Hub"; pinned shortcuts and Windows folders are user-configurable; power buttons. |
| **Background** | Tray icon, single instance (second launch forwards its arguments), global hotkey for the Hub (default `Ctrl+Alt+Space`), optional start with Windows, light/dark theme following Windows. |

## Command line

```
FileHub.exe                 open Explorer
FileHub.exe C:\some\folder  open Explorer at a folder
FileHub.exe --hub [query]   open File Hub
FileHub.exe --start         toggle the start menu
FileHub.exe --tray          start in the background only
FileHub.exe --rebuild       rebuild the index
FileHub.exe --exit          stop the running instance
```

## Data locations

`%LocalAppData%\FileHub\` — `settings.json`, `index.fhdb`, `meta.db`, `logs\`.

## Known limitations of this first shell

* Index crawling uses directory enumeration (parallel per volume) rather than the NTFS MFT/USN journal, so a full rebuild of a large drive takes seconds to a minute or two instead of a second. The snapshot makes startup instant; live changes are tracked with watchers.
* Content indexing covers plain text, code and Office XML (docx/xlsx/pptx). PDF text is not extracted yet (PDF metadata is).
* The shell context menu shows Windows' menu items as text (no icons); owner-drawn items appear by verb name.
* The nav pane does not yet show network locations or libraries.

## Download

Download `FileHub.exe` from the [latest release](https://github.com/DontGoToCollege/FileHub/releases/latest). It's a single program: no installer, just run it.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
