# ATLAS — Copy-Pasteable Support Blocks

Drop-in for other authors' pages. The only edit required is the last line's link (their own
tracker); everything else works unchanged. Keep the ATLAS link intact so readers can find it.

```bbcode
[size=4][b]━━ REPORTING A BUG ━━[/b][/size]

Please attach an ATLAS support bundle. [url=https://www.nexusmods.com/planetcrafter/mods/233]ATLAS[/url] is a free, read-only diagnostic mod that records what is actually loaded on your machine — mod versions, patch conflicts, keybind clashes, mods that failed to load, and the errors your logs keep repeating. It saves a lot of back-and-forth.

[b]How:[/b]
• Install ATLAS, then play until you hit the problem
• Return to the main menu, or quit the game normally
• Open [b]BepInEx\ATLAS\Scans\[/b] and grab [b]ATLAS_support_bundle.zip[/b]
• Post it wherever I have asked for reports — the zip explains itself to whoever opens it

Nexus's boxes are text-only, so the zip needs a file host or an issue tracker rather than a comment.

[b]Privacy:[/b] your Windows username, profile path, machine name and Steam ID are stripped before the zip is written (best-effort — open it first if you would like to check). ATLAS has no network access and sends nothing anywhere.

[b]A note on what it reports:[/b] a finding is a lead, not a verdict. ATLAS reports what it can see; it cannot see everything, and a mod named in a finding is not necessarily at fault.
```

---

## Notes

- **The framing line is not optional.** Without it, users arrive in comment sections announcing that
  ATLAS says someone's mod is broken. Authors who feel accused stop recommending the tool, and
  authors are the audience that decides whether it survives.
- **The privacy paragraph earns the upload.** Users who suspect a diagnostic zip leaks their name
  will not send it. Saying what is stripped — and admitting it is best-effort — converts better than
  either silence or an overclaim.
- **The fallback line in Block A matters.** Some users will not install another mod to report a bug
  on the one that broke. Give them the old path rather than a dead end.
- **F3 is the alternative route** if a user cannot find the folder: the in-game panel has a
  Support bundle section with Export, Copy path and Open folder. It is not in the block because
  three steps beat five, and the zip is already written by default.
