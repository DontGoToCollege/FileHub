# FileHub: notes for Claude

Build, test and release recipes, decisions and gotchas, kept in the repo so every computer has them.

FileHub lives at `%USERPROFILE%\Music\Claude\Projects\FileHub` (solution root; project `src\FileHub`). Started 2026-09-15 as a "shell" the user will test and give feedback on.

- Target `net8.0-windows`, WPF + WinForms (tray icon only; WinForms/Drawing implicit usings are removed in the csproj to avoid type ambiguity).
- Publish with `.\publish.ps1` → `dist\FileHub.exe` (self-contained single file, ~68 MB, ReadyToRun). Machine has SDK 10 with the 8.0 runtime pack cached.
- Data in `%LocalAppData%\FileHub` (settings.json, index.fhdb snapshot, meta.db SQLite with FTS5, logs\).
- Index of this machine: ~618k entries, full crawl ≈23 s, snapshot load ≈0.3 s.
- Default hub hotkey Ctrl+Alt+Space fails to register on this machine (taken by another app); user can change it in Settings.
- Smoke-test technique that works: launch exe, then capture windows with `PrintWindow(hwnd, hdc, 2)` via PowerShell Add-Type (CopyFromScreen gives garbage for the WPF window). Kill with `taskkill /IM FileHub.exe /F` before republishing.

- Versioning now follows the global CLAUDE.md setup (since 2026-10-27): bump `<Version>` in the csproj (+ app.manifest where present) and add a CHANGELOG entry, then `.\publish.ps1`, which refuses an already-archived version and keeps `<Exe>.exe` at the project root plus `Versions\Archive\<Exe>-<version>.exe`; commit, push, and `gh release create v<version> <Exe>.exe`. `archive-version.ps1` is gone (git holds the source history; old snapshot folders sit in `Versions\Archive`).
- User wants minimal, targeted changes per iteration; preserve existing design/behavior.
- User runs StartIsBack; Claude desktop owns Ctrl+Alt+Space (FileHub's LL keyboard hook now captures it first).
- `Slow folder load` lines in the log (>400 ms) identify slow Explorer navigations.
- Never open the live `meta.db` read-write from a script while FileHub runs (a Bash-tool read on 2026-11-22 likely corrupted it; the Bash view of `%LocalAppData%FileHub` can also be stale). Inspect a copy, or use `?mode=ro&immutable=1`. Since 0.3.0 the FTS rowid equals the `content_meta`/`props` rowid (`PRAGMA user_version` 1).

**Why:** the user wants feedback-driven iteration on this app; knowing the layout and the validation recipe avoids re-deriving it.
**How to apply:** for follow-up requests, edit under `src\FileHub`, rebuild with `dotnet build`, republish with publish.ps1, smoke-test with PrintWindow captures. See user-testing-preference.
