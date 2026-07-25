# ATLAS

Three diagnostic subsystems in one plugin:

1. **Log Archive** - keeps a copy of each session's log, named after the error that broke it.
2. **Mod Scanner** - reports patch conflicts and missing dependencies across installed mods.
3. **Drift** - records what the game looked like when things worked, and reports which of your
   patches are now standing on different ground after a game update.

All three are read-mostly: the scanner and drift never write to game or save state, the archive
only writes its own copies of log lines, and drift writes only its own baseline files.

## Log Archive

BepInEx overwrites `LogOutput.log` on every launch, so the log from the session that
actually broke is gone by the time you go looking for it. ATLAS keeps a live copy of each
session in `BepInEx/ATLAS/LogArchive/`, and on exit either names it after its first error or
prunes it as a routine clean run.

## Build

Requires BepInEx installed in the game folder. Edit `<GameDir>` in `ATLAS.csproj` if your
install is not at `C:\Steam\steamapps\common\The Planet Crafter`.

Building copies `ATLAS.dll` to:

```
C:\Steam\steamapps\common\The Planet Crafter\BepInEx\plugins\Davemuziek - ATLAS
```

The copy runs with `ContinueOnError`, so having the game open produces a warning rather
than a failed build. Watch for `ATLAS BUILD ACTIVE v0.1.1` in the log to confirm the DLL
that loaded is the one you just built.

## Output

```
2026-07-22_1934_CRASH_NullReference_BlueprintManager.Update.log
2026-07-22_2011_ERR_ArgumentOutOfRange_DeploymentManager.Apply.log
2026-07-22_2115_OK.log
```

Naming uses the *best* error in a session, not the first. Startup noise almost always
comes first and is almost never what you want the file called.

`CRASH` means the session never reached its exit hook. Those are the interesting ones.

## How it works

A custom `ILogListener` writes each session live to a `.active` file, so nothing depends
on BepInEx's own flush timer or on an exit hook running. Errors are classified as they
arrive, so no parsing is needed at exit.

Any `.active` file still present at startup belongs to a session that died without
finalizing. Those get scanned, tagged `CRASH`, and renamed. A file still held open by a
second running instance of the game is detected by an exclusive-open probe and left alone.


## Mod Scanner

On each save load, ATLAS enumerates every installed plugin, walks Harmony's patched methods,
and writes a timestamped report to `BepInEx/ATLAS/Scans/`. Two files are written per scan: a
plain-text `ModScan_<timestamp>.txt` and, alongside it, a self-contained
`ModScan_<timestamp>.html`. The text file is best for pasting into a Nexus or Discord bug
report; the HTML file leads with a verdict — *is anything wrong?* — and keeps the evidence
collapsed until you drill in. The HTML report is a static file: open it in any browser, no
network needed. Both rings honour `Scanner.MaxReports` independently. It flags:

- **Conflicts** - methods patched by two or more mods, graded by how likely they are to break:
  - *High* - multiple transpilers rewriting the same method's IL
  - *Medium* - a transpiler mixed with other patches, or multiple prefixes (order-dependent)
  - *Low* - postfix-only stacking, which normally coexists cleanly
- **Missing dependencies** - declared `BepInDependency` GUIDs that are not loaded (hard first)

The scanner is read-only. It cannot change load order at runtime - BepInEx settles that at
chainload - so it reports and advises rather than resolving.

## Decisions (ignore / approve / delete)

Not everything the scanner flags is a problem. A mod author might leave a placeholder in a
controller bind on purpose (not everyone wants a face button opening a command console); two mods
might share a key by design because they are active in different contexts. The HTML report lets
you set those aside.

**Ignore / Approve** buttons sit on malformed bindings, keybind overlaps, and patch conflicts.
Clicking one hides the item, moves it to the **Ignored** tab, and recomputes the verdict live so
it stops weighing in on the status. **Delete** buttons on leftover configs queue an abandoned
`.cfg` for removal.

Because the report is a static file with no network, it cannot touch your disk - it is only the
UI. Your clicks batch into a **decisions bar**:

- **Save exceptions** writes a `decisions.tsv` - the complete set, your earlier selections plus the
  new clicks. On Chromium browsers (Chrome, Edge) it opens a Save-As dialog so you choose the
  folder: point it at `BepInEx/ATLAS` and, after that first pick, it writes there directly for the
  rest of the session. On Firefox/Safari it falls back to a download you drop in yourself.
- **Reset exceptions** writes an empty file to the same place. ATLAS deletes `decisions.tsv` on the
  next launch whenever it holds no exceptions, so this clears everything.

(If you would rather be prompted for every download the plain way, most browsers have an "Ask where
to save each file before downloading" setting that does the same for the fallback path.)

ATLAS reads the file back on each scan:

- `ignore <key>` items are set aside - kept in the Ignored tab, never counted toward the verdict.
  Restore one from the Ignored tab and Save to bring it back.
- `delete-config <guid>` deletes that abandoned config on the next launch, then clears itself. This
  is the only file ATLAS ever deletes, and only ever a `.cfg` whose owning mod is not loaded.

The file is plain, hand-editable TSV; you can maintain it by hand instead of through the report if
you prefer.

## In-game panel

Press **F3** (configurable) at the main menu or in-game to open the ATLAS overlay - a summon-key
window that shows the latest scan and lets you set exceptions right there. Because ATLAS runs in
the game with file access, a decision is written to `decisions.tsv` the instant you click, so there
is no browser, download, or file to move; a rescan reflects it fully.

The panel deliberately draws its own window rather than adding a button to the game's menus. An
overlay is scene-agnostic, so it works at the menu (good for pre-play housekeeping - reviewing
conflicts, deleting abandoned configs) and in-game (the full report, including keybinds and content
drift, which need a loaded save). And nothing it does hooks the game's UI, so a game update cannot
break how it opens - which would be a poor look for the mod that exists to catch exactly that.

There is no on-screen button by design; a small corner hint names the toggle key (turn it off with
`Panel.ShowHint`). ATLAS picks a sensible default key and, dog-fooding its own keybind scanner,
warns in the panel if your toggle key is also bound by the game or another mod.

The window is draggable (title bar) and resizable (drag the bottom-right grip). Opening it suspends
the game's own input so clicks and keys do not leak through to the world behind it, and text size is
adjustable for high-resolution displays.

| Key | Default | Notes |
|---|---|---|
| `Panel.Enabled` | `true` | Show the overlay at all. |
| `Panel.ToggleKey` | `F3` | Open/close key (New Input System). Avoid F10/F12. |
| `Panel.ShowHint` | `true` | Small corner hint naming the key. |
| `Panel.BlockGameInput` | `true` | Suspend the game's input while the overlay is open. |
| `Panel.FontSize` | `16` | Overlay text size in points. Raise it on high-DPI displays. |

Note while developing: `TestHarness.cs` binds F3/F11/F4 for its error tests, so it overlaps the
default panel key until you delete it before release.

## Drift (update-breakage detection)

The scanner answers *what overlaps* and the archive answers *what threw*. Neither notices when
the **ground moves** - when a game update renames a field, changes a method body a transpiler
was pinned to, or recategorises a construction item. Those failures are quiet or silent: they
do not surface until a player hits them, and the log then points at a symptom, not the update.

Drift closes that by diffing the game against a recorded baseline across two surfaces:

- **Code surface** - method existence, signatures, and normalised IL of the methods your mods
  actually patch or reflect into, read from `Assembly-CSharp.dll` with Mono.Cecil at startup.
  Catches the quiet (transpiler pattern no longer matches) and silent (reflected field renamed)
  grades.
- **Content surface** - the `GroupsHandler` roster and selected `GroupData` fields, snapshotted
  after a save loads. Catches additions, removals, and recategorisations - what a content update
  actually consists of. Also standing-asserts against a null `craftableInList`, a known landmine
  that takes down every crafter screen, on the very first run.

Baselines live in `BepInEx/ATLAS/Baseline/` as hand-readable TSV. The findings lead the scan
report, under **UPDATE IMPACT**.

Each Update-impact finding in the HTML report carries a **Fix brief** export, in two formats:
**HTML** (opens in any browser with full formatting - no editor or extension association needed)
and **.txt** (a fuss-free open in any editor, and copied to the clipboard for pasting into an AI
coder). A brief covers one finding - what changed, which mods patch it and how, the
baseline-vs-current build context, and a concrete plan of attack chosen by the kind of change -
written to be read cold by an experienced or AI coder. Both are generated client-side and are
ASCII-clean, so a download works from a `file://` page with no network and no encoding prompt.

**A clean drift result is not a promise that nothing broke** - only that nothing broke in the
ways static comparison can see. The report prints what it cannot see, in the section itself.

**Live status - findings that heal themselves.** Detection stays baseline-anchored (a finding is
raised the moment the game moves), but every finding is then re-verified against your *current*
mods on each scan rather than replaying a saved verdict:

- **Active** - re-checked and still broken in the current game + mods.
- **Review** - the ground moved in a way static analysis cannot confirm a mod-side fix for (a
  method body or signature change, a content change). These never auto-clear.
- **Resolved** - it was open last scan and is gone now: the fix took. Shown once as confirmation,
  then dropped.

So a reflection you re-point, or a `[HarmonyPatch]` you aim at the renamed method, clears on its
own the next time you scan - fix the mod, load a save, watch it flip to **Resolved**. What cannot
be verified this way (Review) never disappears silently.

A changed build still does **not** overwrite the baseline. The Review band, and anything static
comparison cannot confirm a fix for, persists across sessions until you explicitly accept the new
build by setting `Drift.AcceptCurrentBuild = true` and loading a save - so an update cannot quietly
stop being reported. Accepting is a clean slate: it also clears the resolved-once state.

Drift makes no network calls, attempts no repair, and never locks a game assembly.

## Config

| Key | Default | Notes |
|---|---|---|
| `General.MinLevelToArchive` | `Info` | `Debug` produces very large files. |
| `Retention.MaxErrorLogs` | `20` | Pruned oldest-first at startup. |
| `Retention.MaxHealthyLogs` | `3` | Rolling backups of clean sessions. |
| `Retention.KeepHealthyLogs` | `true` | Off deletes clean sessions on exit. |
| `Sessions.RollOnReturnToMenu` | `true` | One archive per world, not per process. |
| `Sessions.SeedFromExistingLog` | `true` | Captures preloader and earlier plugins. |
| `Capture.CaptureUnityDirect` | `false` | Redundant second channel. Rarely needed. |
| `Capture.InterestingNamespaces` | see config | Frame prefixes preferred when naming. Case-insensitive. |
| `Capture.FrameworkNamespaces` | see config | Frame prefixes treated as noise when naming. |
| `Capture.IgnoreErrorsContaining` | see config | Pipe-separated. Archived, but excluded from tag and name. |
| `Scanner.Enabled` | `true` | Master toggle for the scanner half. |
| `Scanner.ScanOnGameLoad` | `true` | Re-scan on each save load. |
| `Scanner.WriteReportFile` | `true` | Write the timestamped text report. |
| `Scanner.WriteHtmlReport` | `true` | Also write a browser-readable HTML report alongside the text one. |
| `Scanner.MaxReports` | `10` | Rolling report backups; oldest pruned. Applies to `.txt` and `.html` independently. |
| `Drift.Enabled` | `true` | Master toggle for the drift half. Read-only. |
| `Drift.AcceptCurrentBuild` | `false` | One-shot. Accept the current build as the new baseline, then resets itself. |
| `Drift.ScanReflection` | `true` | Also verify reflected member names (AccessTools etc.) against the game. |
| `Drift.ScanContent` | `true` | Snapshot the group roster and key GroupData fields. |
| `Drift.GameNamespaces` | `SpaceCraft.` | Comma-separated prefixes treated as game code for reflection checks. |
