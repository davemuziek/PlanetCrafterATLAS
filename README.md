# ATLAS

A read-mostly diagnostic mod for a Planet Crafter BepInEx setup.

Something in your mod list broke the game. ATLAS works out what — patch conflicts, keybind clashes,
mods that failed to load, and code the last update moved out from under them. It runs in-game, makes
no network calls, and never touches game or save state.

It is strongest in a mod author's hands, but it is built for anyone with a load order: a player can
find their own keybind collision, narrow down which of twelve mods is the culprit, and hand an author
a complete diagnostic history instead of a pasted log fragment.

**Download:** [Nexus Mods](https://www.nexusmods.com/planetcrafter/mods/233) ·
[latest release](https://github.com/davemuziek/PlanetCrafterATLAS/releases/latest)
**Docs:** [Reading a report](docs/READING_A_REPORT.md) ·
[Bundle format](BUNDLE_FORMAT.md) ·
[Support block for mod authors](SUPPORT_BLOCK.md)

> **A finding is a lead, not a verdict.** ATLAS reports what it can see by reading code and logs. It
> cannot see everything, and a mod named in a finding is not necessarily at fault.

## The four breakage axes

ATLAS asks four separate questions and keeps the answers visibly apart, because a clean answer to
one says nothing about the others. Each is honest about its own blind spot; none replaces another.

| | Question | Needs a baseline? | Uniquely catches |
|---|---|---|---|
| **Drift** | Did the game change under my accepted baseline? | Yes | A silent method-body change |
| **Mod compatibility** | Do the game members each mod hooks still exist? | No | An out-of-date mod on a fresh install |
| **Runtime patch verification** | Did the declared patches actually apply, and did every mod load? | No | A patch that silently did nothing |
| **Analysis coverage** | How much of each mod could ATLAS actually see? | No | Its own blind spots (trust calibration) |

Compatibility and coverage run at `Awake` (static). Patch verification runs at scan time — Harmony's
applied registry is only complete after every mod's `Awake`.

## Subsystems

**Log archive.** BepInEx overwrites `LogOutput.log` on every launch, so the log from the session that
actually broke is gone by the time you look. ATLAS keeps a live copy of each session in
`BepInEx/ATLAS/LogArchive/`, named after the *best* error in that session rather than the first —
startup noise comes first and is almost never what you want the file called.

```
2026-07-22_1934_CRASH_NullReference_BlueprintManager.Update.log
2026-07-22_2011_ERR_ArgumentOutOfRange_DeploymentManager.Apply.log
2026-07-22_2115_OK.log
```

`CRASH` means the session never reached its exit hook. Those are the interesting ones.

A custom `ILogListener` writes each session live to a `.active` file, so nothing depends on BepInEx's
flush timer or on an exit hook running. Any `.active` file still present at startup belongs to a
session that died without finalizing: it gets scanned, tagged `CRASH`, and renamed. A file still held
open by a second running instance is detected by an exclusive-open probe and left alone.

**Mod scanner.** Enumerates every installed plugin, walks Harmony's patched methods, and reports:

- **Conflicts** — methods patched by two or more mods, graded by topology and prefix return type, with
  the applied execution order shown. Common and usually harmless; what raises severity is the shape of
  the overlap. A prefix that can return `false` skips everything downstream of it.
- **Missing dependencies** — a declared `BepInDependency` GUID not loaded, plus version satisfaction.
- **Keybinds** — overlaps, unused keys and buttons, and — most usefully — collisions ATLAS *watched
  actually fire together* during play.

The scanner is read-only. It cannot change load order at runtime — BepInEx settles that at chainload —
so it reports and advises rather than resolving.

**Drift.** Diffs the game against a recorded baseline across two surfaces: the **code surface**
(method existence, signatures, and normalised IL of the methods your mods actually patch or reflect
into, read from `Assembly-CSharp.dll` with Mono.Cecil at startup) and the **content surface** (the
`GroupsHandler` roster and selected `GroupData` fields, snapshotted after a save loads). Baselines
live in `BepInEx/ATLAS/Baseline/` as hand-readable TSV.

Findings are re-verified against your *current* mods on each scan rather than replaying a saved
verdict, so they heal themselves: **Active** (still broken), **Review** (the ground moved in a way
static analysis cannot confirm a fix for — never auto-clears), **Resolved** (open last scan, gone
now; shown once as confirmation). A changed build does not overwrite the baseline — accept it
explicitly with `Drift.AcceptCurrentBuild = true`, so an update cannot quietly stop being reported.

**Log activity.** Steps back from individual errors to the pattern: every Error/Warning/Fatal event
across the kept logs, grouped by signature and split into **consistently firing** (seen in two or more
sessions — a standing issue) and **situational** (one session). Alongside, the **noisiest sources**,
attributed by the BepInEx `[Source]` tag every line carries, which separates the engine firehose from
actual mod chatter. Diagnostic context only — it never feeds the verdict.

**Support bundle.** One scrubbed, self-describing `ATLAS_support_bundle.zip` written beside the
reports, carrying the scans, session logs, mod configs and a `manifest.json`, with a
`READ_ME_FIRST.html` that explains itself to a reader who has never installed ATLAS. Usernames,
profile paths, machine name and Steam/join IDs are stripped by default — best-effort by design, so
review before sharing. The format is documented as a contract in [BUNDLE_FORMAT.md](BUNDLE_FORMAT.md).

## Reports

Two files per scan in `BepInEx/ATLAS/Scans/`: a plain-text `ModScan_<stamp>.txt` and a self-contained
`ModScan_<stamp>.html` that leads with a verdict — *is anything wrong?* — and keeps the evidence
collapsed until you drill in. Both are static files needing no network.

`Scans/index.html` is the homepage: a sidebar of every kept scan and a **Changes** view diffing the
newest against any earlier one, showing what appeared and what went away. If your game worked last
week and does not now, that view is usually the fastest route to the answer.

Each Update-impact finding carries a **Fix brief** export (HTML and `.txt`) — what changed, which mods
patch it and how, the baseline-vs-current build context, and a plan of attack chosen by the kind of
change, written to be read cold by an experienced or AI coder. Each noisiest source exports its full
activity catalog. All exports are base64-embedded and generated client-side, so they work from a
`file://` page with no network.

## Decisions (ignore / approve / delete)

Not everything flagged is a problem. An author might leave a placeholder in a controller bind on
purpose; two mods might share a key by design because they are active in different contexts.

**Ignore / Approve** buttons sit on malformed bindings, keybind overlaps and patch conflicts. Clicking
one hides the item, moves it to the **Ignored** tab, and recomputes the verdict live. **Delete**
buttons queue an abandoned `.cfg` for removal — the only file ATLAS ever deletes, and only ever a
`.cfg` whose owning mod is not loaded.

Because the HTML report is a static file with no write access, it is only the UI: clicks batch into a
decisions bar that writes `decisions.tsv`. **The in-game panel is easier** — it has real file access,
so a decision is written the instant you click. The file is plain, hand-editable TSV either way.

## In-game panel

Press **F3** (configurable) at the menu or in-game for an overlay carrying every section, with real
file access — so every write-action lives here: setting exceptions, exporting a bundle, copying its
path, opening its folder.

It draws its own window rather than patching the game's UI, so a game update cannot break how it opens
— which would be a poor look for the mod that exists to catch exactly that. An overlay is also
scene-agnostic: it works at the menu for pre-play housekeeping and in-game for the full report,
including keybinds and content drift, which need a loaded save.

There is no on-screen button by design; a small corner hint names the toggle key. Dog-fooding its own
keybind scanner, ATLAS warns if your toggle key is also bound by the game or another mod. The window
is draggable and resizable, and text size is adjustable for high-resolution displays.

## Scan triggers

On **game load**, on **return to menu** (the fuller one — the content surface is captured after the
load scan runs, and runtime-learned keys accumulate during play), and on **quit-from-game**
(`Application.quitting` / `OnApplicationQuit`, guarded by "still in the game scene") so a
quit-to-desktop, Alt+F4 or Steam Stop still produces a complete end-of-session report.

## Config

Everything below is in `BepInEx/config/`. Defaults are chosen so a fresh install is useful without
touching any of it.

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | `true` | Master toggle. |
| `General.ArchiveDirectory` | `LogArchive` | Relative to `BepInEx/ATLAS/`. |
| `General.MinLevelToArchive` | `Info` | `Debug` produces very large files. |
| `Retention.MaxErrorLogs` | `20` | Pruned oldest-first at startup. |
| `Retention.MaxHealthyLogs` | `3` | Rolling backups of clean sessions. |
| `Retention.KeepHealthyLogs` | `true` | Off deletes clean sessions on exit. |
| `Sessions.RollOnReturnToMenu` | `true` | One archive per world, not per process. |
| `Sessions.SeedFromExistingLog` | `true` | Captures preloader and earlier plugins. |
| `Capture.CaptureUnityDirect` | `false` | Redundant second channel. Rarely needed. |
| `Capture.InterestingNamespaces` | see config | Frame prefixes preferred when naming. |
| `Capture.FrameworkNamespaces` | see config | Frame prefixes treated as noise when naming. |
| `Capture.IgnoreErrorsContaining` | see config | Pipe-separated. Archived, but excluded from tag and name. |
| `Scanner.Enabled` | `true` | Master toggle for the scanner half. |
| `Scanner.ScanOnGameLoad` | `true` | Re-scan on each save load. |
| `Scanner.WriteReportFile` | `true` | Write the timestamped text report. |
| `Scanner.WriteHtmlReport` | `true` | Also write the browser-readable HTML report. |
| `Scanner.WriteScansIndex` | `true` | Write the `Scans/index.html` homepage. |
| `Scanner.MaxReports` | `10` | Rolling backups; `.txt` and `.html` pruned independently. |
| `Scanner.KeybindDiagnostics` | `false` | Verbose keybind logging. |
| `Scanner.LearnHardcodedKeys` | `false` | Observe keys read directly rather than through bindings. |
| `Scanner.VerifyPatchesApplied` | `true` | Check declared patches against Harmony's live registry. |
| `Scanner.LogActivitySummary` | `true` | Summarise recurring vs one-off errors across kept logs. |
| `Drift.Enabled` | `true` | Master toggle for the drift half. Read-only. |
| `Drift.AcceptCurrentBuild` | `false` | One-shot: accept the current build as the new baseline, then resets. |
| `Drift.ScanReflection` | `true` | Also verify reflected member names against the game. |
| `Drift.ScanContent` | `true` | Snapshot the group roster and key `GroupData` fields. |
| `Drift.GameNamespaces` | `SpaceCraft.` | Prefixes treated as game code for reflection checks. |
| `Drift.CheckModCompatibility` | `true` | The compatibility axis. Nearly free when Drift is on. |
| `Bundle.AutoBuild` | `true` | Build the support bundle alongside each scan. |
| `Bundle.Scope` | `Last3` | `Session`, `Last3`, or `All`. GitHub caps attachments at 25 MB. |
| `Bundle.Redact` | `true` | Scrub usernames, paths, machine name, Steam/join IDs. |
| `Bundle.ExtraRedactions` | *(empty)* | Comma-separated literals to also remove. |
| `Bundle.IncludeModConfigs` | `true` | Include other mods' `.cfg` files (scrubbed). |
| `Panel.Enabled` | `true` | Show the overlay at all. |
| `Panel.ToggleKey` | `F3` | Open/close key (New Input System). Avoid F10/F12. |
| `Panel.ShowHint` | `true` | Small corner hint naming the key. |
| `Panel.BlockGameInput` | `true` | Suspend the game's input while the overlay is open. |
| `Panel.FontSize` | `16` | Overlay text size. Raise it on high-DPI displays. |

## What ATLAS does not do

No network calls of any kind. No modification of the game, your saves, or another mod. No attempted
repair. It never locks a game assembly. The only files it writes are its own — logs, reports,
baselines, `decisions.tsv`, and the support bundle — and the only user file it deletes is an abandoned
`.cfg` explicitly queued for deletion.

## Build

Requires BepInEx installed in the game folder. Edit `<GameDir>` in `ATLAS.csproj` if your install is
not at `C:\Steam\steamapps\common\The Planet Crafter`.

Building copies `ATLAS.dll` to `…\BepInEx\plugins\Davemuziek - ATLAS`. The copy runs with
`ContinueOnError`, so having the game open produces a warning rather than a failed build. Watch for
`ATLAS BUILD ACTIVE v0.15.0` in the log to confirm the DLL that loaded is the one you just built.

Targets netstandard2.1 / C# 9. References BepInEx, 0Harmony, Mono.Cecil, Assembly-CSharp, and the
Unity Core/IMGUI/TextRendering modules plus `Unity.InputSystem`.

## Reporting

Bugs, triage help, and mod-author requests go to
[Issues](https://github.com/davemuziek/PlanetCrafterATLAS/issues) — attach a support bundle. General
questions are better on the
[Nexus posts tab](https://www.nexusmods.com/planetcrafter/mods/233?tab=posts).
