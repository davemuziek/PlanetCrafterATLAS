# ATLAS support bundle — format

A support bundle is a single, self-describing ZIP (`ATLAS_support_bundle.zip`) that ATLAS writes
beside its scan reports. It carries a user's diagnostic history — scans, session logs and mod
configs — scrubbed of personal identifiers, so it can be handed to a mod author or attached to a
GitHub issue without a pasted fragment losing context.

This document is the contract for anyone writing a parser (e.g. a companion app). The authoritative
per-bundle description is `manifest.json` inside the zip; this file explains its shape.

## Layout

```
ATLAS_support_bundle.zip
├── READ_ME_FIRST.html          opens standalone, explains the bundle to a cold reader
├── manifest.json               schema-versioned; the machine-readable index (see below)
├── Scans/
│   ├── index.html              the scans homepage, with its ledger embedded inline
│   ├── ModScan_<stamp>.html    one HTML report per kept scan
│   └── ModScan_<stamp>.txt     the plain-text twin of each report (paste-friendly)
├── Logs/
│   └── <session>.log           archived session logs, scrubbed
├── Config/
│   └── <mod>.cfg               BepInEx .cfg files, scrubbed (present when IncludeModConfigs is on)
└── decisions.tsv               the user's exceptions ledger (only when it exists and is non-empty)
```

The filename is fixed and the zip is a single copy, overwritten on each build — there are no stale
bundles to accumulate.

## `manifest.json`

Hand-written JSON, UTF-8, every string escaped. Read `schema` first.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | Format version. **The contract.** Bumped only on a breaking shape change; additive fields do not bump it. Current: `1`. |
| `atlasVersion` | string | ATLAS plugin version that wrote the bundle. |
| `generatedUtc` | string | ISO-8601 UTC build time. |
| `scope` | string | `Session` \| `Last3` \| `All` — how much history is included. |
| `redacted` | bool | Whether the redaction pass ran over the text files. |
| `game` | object | `{ "version": string, "assemblyMvid": string }` — the game build. `assemblyMvid` is the `Assembly-CSharp` module id, or `""` if not recorded. |
| `bepinex` | string | BepInEx assembly version. |
| `unity` | string | Unity runtime version. |
| `verdict` | string | `clean` \| `attention` \| `problem` — ATLAS's overall read for the newest scan. |
| `counts` | object | `{ "high": int, "medium": int, "low": int }` — patch-conflict severity tally. |
| `axes` | object | Which analyses ran: `{ "drift": string, "compatChecked": bool, "patchCheckRan": bool, "coverageChecked": bool }`. `drift` is the code-surface state (`Unchanged` / `Changed` / `NoBaseline` / `Unreadable` / `Unavailable` / `n/a`). |
| `mods` | array | `{ "guid", "name", "version", "coverage" }` per installed mod. `coverage` is `full` \| `partial` \| `unknown` — how much of that mod's hooking surface ATLAS could statically resolve. |
| `findings` | array | The notable High/Medium items across axes: `{ "axis", "severity", "owner", "member" }`. `axis` is one of `load` / `compat` / `patch` / `drift` / `conflict`. |
| `files` | array | Every entry in the zip: `{ "path", "kind" }`. `kind` is one of `readme` / `manifest` / `scan-index` / `scan-html` / `scan-text` / `log` / `config` / `decisions`. |

Parsers should ignore unknown fields and tolerate empty arrays. If `schema` is higher than the
version you were written against, read what you recognise and surface the rest as opaque.

## Redaction

When `redacted` is `true`, every text file was passed through a best-effort scrubber before entering
the zip. It removes, in order: Windows profile paths (`C:\Users\<name>` → `C:\Users\<USER>`), Proton /
Linux home paths (→ `/home/<USER>`), the account username and machine name (whole-word), SteamID64s
(`<STEAMID>`), the game's multiplayer invite codes (`<INVITECODE>`), and any user-supplied literals
(`<REDACTED>`).

It is **best-effort by design**: a mod that logs something personal in a shape ATLAS does not
recognise can pass through. Do not treat a redacted bundle as guaranteed free of identifiers.

## Stability

The layout and `manifest.json` shape at `schema: 1` are frozen. Additive, backward-compatible fields
may appear without a `schema` bump; any breaking change increments `schema`.
