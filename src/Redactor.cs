using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ATLAS
{
    /// <summary>
    /// Best-effort scrubber for the support bundle (0.15.0). A single <see cref="Scrub"/> is applied
    /// to every text file entering the zip, stripping the personal identifiers that leak into logs and
    /// configs — profile paths, the account/machine name, and the Steam / multiplayer join IDs — before
    /// the bundle is handed to a stranger.
    ///
    /// Best-effort <em>by construction</em>, and said to be so in READ_ME_FIRST and on the panel button:
    /// a mod that logs something personal in a shape ATLAS does not know about passes straight through.
    /// That honesty is why <c>Bundle.ExtraRedactions</c> exists — a manual escape hatch — rather than
    /// this reflecting into live game state to auto-detect a gamertag (which would over-promise and
    /// couple ATLAS to the game).
    ///
    /// Pure and static — no Unity or game dependency — so the harness can exercise every pass without
    /// the game running. The identity passes seed once from <see cref="Environment"/>; a caller (the
    /// harness) can override them with <see cref="Configure"/>.
    /// </summary>
    internal static class Redactor
    {
        // 1. Windows profile path. Normalises the account segment to <USER>, leaving the rest of the
        //    path (\AppData\…) intact. The class stops at the next separator or at a character that
        //    cannot appear in a Windows path segment, so it never swallows trailing log punctuation
        //    while still allowing a username with spaces ("John Smith").
        private static readonly Regex WinPath = new Regex(
            @"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+", RegexOptions.Compiled);

        // 2. Proton / Linux home. Steam Deck and native-Linux users exist and their logs look nothing
        //    like a Windows user's; Proton also surfaces the drive as Z:. Match either separator.
        private static readonly Regex NixHome = new Regex(
            @"(?:Z:\\)?[/\\]home[/\\][^/\\ \t\r\n""']+", RegexOptions.Compiled);

        // 5. SteamID64 — target the 7656119… prefix, not any 17-digit number, so a large unrelated
        //    integer in a log is left alone.
        private static readonly Regex SteamId = new Regex(
            @"\b7656119\d{10}\b", RegexOptions.Compiled);

        // 6. Multiplayer invite code. If one reaches a log it is a live join credential. The shape is
        //    the game's own: SpaceCraft.InviteCodeGenerator.Generate(int length = 6) draws six
        //    characters from the confusable-free alphabet below (read from Assembly-CSharp). But that
        //    alphabet contains vowels, so the bare shape collides with ordinary all-caps six-runs — the
        //    keyboard key PAGEUP, for one, which ATLAS lists in its own keybind report and which a naked
        //    match would shred into "<INVITECODE>" in every bundle. So the match is anchored to an
        //    invite/join context (a variable-length lookbehind, .NET-only): a six-run presented as a
        //    code is redacted; a six-run that merely fits the alphabet is left alone. A bare, context-
        //    free code is indistinguishable from such a token and is left to ExtraRedactions — the
        //    honest escape hatch the tool already documents. The code class stays case-sensitive
        //    (real codes are upper); only the cue is case-insensitive.
        private static readonly Regex InviteCode = new Regex(
            @"(?<=(?i:invite|join|lobby|session|connect|mpa|steam://|code)[^\r\n]{0,40})\b[ACEFGHJKMNPRTUVWXYZ234679]{6}\b",
            RegexOptions.Compiled);

        // Guard for the identity passes (3, 4): a username equal to a common word would shred the logs.
        private static readonly HashSet<string> CommonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dev", "admin", "administrator", "user", "users", "test", "guest", "home",
            "root", "steam", "player", "game", "public", "default", "owner", "main",
        };

        private static string _userName = SafeEnv(() => Environment.UserName);
        private static string _machineName = SafeEnv(() => Environment.MachineName);
        private static Regex? _userRx = BuildIdentity(_userName);
        private static Regex? _machineRx = BuildIdentity(_machineName);

        /// <summary>
        /// Overrides the identity (username / machine) the scrubber removes. The mod never needs this —
        /// it seeds from <see cref="Environment"/> — but the harness uses it so the identity passes can
        /// be asserted against a known name rather than the build machine's account.
        /// </summary>
        internal static void Configure(string userName, string machineName)
        {
            _userName = userName ?? "";
            _machineName = machineName ?? "";
            _userRx = BuildIdentity(_userName);
            _machineRx = BuildIdentity(_machineName);
        }

        public static string Scrub(string input) => Scrub(input, null);

        /// <summary>
        /// The ordered passes. Path passes run first (they normalise the account segment in place), then
        /// the bare-identity passes catch the name outside a path, then the ID passes, then the
        /// user-supplied literals. Each pass is independent; a text with none of these is returned as-is.
        /// </summary>
        public static string Scrub(string input, string? extraCsv)
        {
            if (string.IsNullOrEmpty(input)) return input ?? "";

            var s = input;
            s = WinPath.Replace(s, @"C:\Users\<USER>");
            s = NixHome.Replace(s, "/home/<USER>");
            if (_userRx != null) s = _userRx.Replace(s, "<USER>");
            if (_machineRx != null) s = _machineRx.Replace(s, "<MACHINE>");
            s = SteamId.Replace(s, "<STEAMID>");
            s = InviteCode.Replace(s, "<INVITECODE>");
            s = ApplyExtras(s, extraCsv);
            return s;
        }

        // 7. User-supplied literals (gamertag, session name, anything they know about). Comma-separated,
        //    matched case-insensitively as plain text. A literal shorter than two characters is skipped —
        //    it would carpet the whole file.
        private static string ApplyExtras(string s, string? extraCsv)
        {
            if (string.IsNullOrWhiteSpace(extraCsv)) return s;
            foreach (var raw in extraCsv.Split(','))
            {
                var lit = raw.Trim();
                if (lit.Length < 2) continue;
                s = Regex.Replace(s, Regex.Escape(lit), "<REDACTED>", RegexOptions.IgnoreCase);
            }
            return s;
        }

        // 3 & 4. Whole-word, case-insensitive match on the literal name, so it is caught outside a path
        //         without matching a substring of a longer token. Skipped when too short or a common word.
        private static Regex? BuildIdentity(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return null;
            if (CommonWords.Contains(name)) return null;
            return new Regex(@"\b" + Regex.Escape(name) + @"\b",
                             RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private static string SafeEnv(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }
    }
}
