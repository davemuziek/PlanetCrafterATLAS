# Reading an ATLAS report

> **A finding is a lead, not a verdict.** ATLAS reports what it can see by reading code and logs.
> It cannot see everything, and a mod named in a finding is not necessarily at fault. Treat
> everything below as triage input, not as an accusation.

Start at `Scans/index.html`. The sidebar lists every kept scan, newest first, with a coloured dot
for its verdict. The **Changes** view at the top is usually the most useful thing in the bundle: it
diffs the newest scan against an earlier one and shows what *appeared* and what *went away*. If your
game worked last week and does not now, compare against last week's scan and read the "appeared"
column.

## The four axes

ATLAS asks four separate questions. They are deliberately kept apart, because a clean answer to one
says nothing about the others.

**1. Drift — did the game change under you?**
ATLAS remembers what the game's code looked like when things worked, and compares. This is the only
axis that catches a method whose *body* changed while its name and signature stayed the same — the
silent kind of breakage that produces no error at all, just wrong behaviour. Needs a baseline
recorded from a working setup, so it says nothing on a fresh install.

**2. Mod compatibility — do the things mods hook into still exist?**
For every mod, ATLAS resolves the game methods and fields it reaches for against the game as
currently installed. A mod built for an older version that reaches for something since deleted shows
up here. Works with no baseline, so it fires on a fresh install. Checks that a member *exists*, by
name — not that its signature or its behaviour is unchanged.

**3. Runtime patch verification — did the patches actually take?**
After every mod has loaded, ATLAS checks Harmony's real registry to see whether each declared patch
is genuinely applied, and reads the logs for mods that failed to load outright. A mod that silently
did nothing shows up here. Note that a patch confirmed applied is not a patch confirmed *correct*,
and a "not observed applied" at Low severity is very often a patch that was conditional by design.

**4. Analysis coverage — how much could ATLAS actually see?**
Not a breakage check; a trust calibration. If a mod scores low coverage, the silence of the other
three axes about that mod means less. Obfuscated, dynamically-constructed or heavily-inlined code is
invisible to static analysis, and this axis is how ATLAS admits it.

## The other sections

**Conflicts** — methods patched by two or more mods. Common and usually harmless: mods stack on the
same method all the time. What raises severity is the *shape* of the overlap — a prefix that can
return `false` will skip everything downstream of it, so a conflict involving one is worth reading
even when nothing is visibly broken. The applied execution order is shown, because order often is
the bug.

**Missing dependencies** — a mod declared a dependency that is not loaded, or is loaded at a version
below what it asked for.

**Keybinds** — two things bound to the same key, keys bound to nothing, and — most usefully —
overlaps ATLAS *watched actually fire together* during play. A confirmed collision is worth more
than a theoretical one.

**Log activity** — a step back from individual errors to the pattern. Errors seen in two or more
sessions are **consistently firing** (a standing problem); errors seen once are **situational**.
The **noisiest sources** list separates the engine firehose from actual mod chatter — a huge `Unity`
count is normal and not a finding.

## What to do with it

1. Read the verdict and the High findings. Ignore Low on a first pass.
2. Open **Changes** and compare against a scan from when it worked.
3. If something names a specific mod, check that mod's coverage score before drawing a conclusion.
4. Report to the mod's author with the bundle attached — not with a screenshot of one line.

## What ATLAS does not do

It never modifies the game, your saves, or another mod. It has no network access of any kind. The
only files it writes are its own: logs, reports, baselines, `decisions.tsv`, and the support bundle.
