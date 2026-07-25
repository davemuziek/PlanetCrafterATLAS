using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ATLAS
{
    /// <summary>
    /// One archived log. Writes live to a ".active" file so that a hard crash still
    /// leaves a complete, classifiable log on disk with no exit hook involved.
    /// </summary>
    internal sealed class ArchiveSession : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _dir;
        private readonly string _activePath;
        private readonly DateTime _started;

        private StreamWriter? _writer;
        private bool _closed;

        private int _errorCount;
        private int _warningCount;
        private ErrorSignature? _bestError;

        private string? _lastUnityText;
        private DateTime _lastUnityAt;

        public ArchiveSession(string dir)
        {
            _dir = dir;
            _started = DateTime.Now;

            var stamp = _started.ToString("yyyy-MM-dd_HHmmss");
            var salt = Guid.NewGuid().ToString("N").Substring(0, 6);
            _activePath = Path.Combine(dir, $"session_{stamp}_{salt}.active");

            // FileShare.Read: readable while running, but not writable by anyone else.
            // OrphanRecovery relies on that exclusivity to tell live sessions from dead ones.
            var stream = new FileStream(_activePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            _writer.WriteLine($"=== ATLAS v{Plugin.Ver} session {_started:u} ===");
            _writer.WriteLine($"=== Unity {Application.unityVersion} / game {Application.version} ===");
        }

        public void Write(LogEventArgs e)
        {
            var raw = e.Data?.ToString() ?? string.Empty;
            var isError = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            var isWarning = (e.Level & LogLevel.Warning) != 0;
            var source = e.Source?.SourceName ?? "?";

            lock (_gate)
            {
                var w = _writer;
                if (_closed || w == null) return;

                w.WriteLine(e.ToStringLine().TrimEnd());
                if (isError) NoteError(raw, source);
                else if (isWarning) _warningCount++;
            }
        }

        /// <summary>Second capture channel, used when BepInEx failed to hook Unity's logger.</summary>
        public void WriteUnityDirect(string text)
        {
            lock (_gate)
            {
                var w = _writer;
                if (_closed || w == null) return;

                // Cheap dedupe: BepInEx's Unity hook may also be delivering this.
                if (text == _lastUnityText && (DateTime.UtcNow - _lastUnityAt).TotalSeconds < 1.0) return;
                _lastUnityText = text;
                _lastUnityAt = DateTime.UtcNow;

                w.WriteLine("[Error  :   Unity] " + text);
                NoteError(text, "Unity");
            }
        }

        // Caller holds _gate.
        private void NoteError(string raw, string source)
        {
            if (ErrorClassifier.IsIgnored(raw)) return;

            _errorCount++;
            Consider(ErrorClassifier.Classify(raw, source));
        }

        // Caller holds _gate. Best signature wins, not first - the first error in a session
        // is usually startup noise, and whatever actually broke shows up later.
        private void Consider(ErrorSignature? sig)
        {
            if (sig == null) return;
            if (_bestError == null || sig.Quality > _bestError.Quality) _bestError = sig;
        }

        /// <summary>
        /// Copies whatever is already in LogOutput.log into this archive, so preloader and
        /// earlier-loading plugin lines survive. Opened with FileShare.ReadWrite because
        /// BepInEx's own DiskLogListener still holds it open for writing.
        /// </summary>
        public void SeedFromLiveLogOutput()
        {
            try
            {
                var path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(path)) return;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);

                lock (_gate)
                {
                    var w = _writer;
                    if (_closed || w == null) return;

                    w.WriteLine("--- begin seed from LogOutput.log ---");

                    // Buffer as we go: a stack trace spans several lines, so classifying
                    // line by line here (which is what the first cut did) can only ever
                    // see the message and never the frames.
                    var seeded = new List<string>();
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        w.WriteLine(line);
                        seeded.Add(line);
                    }
                    w.WriteLine("--- end seed ---");

                    var sig = ErrorClassifier.ScanLines(seeded, out int seedErrors);
                    _errorCount += seedErrors;
                    Consider(sig);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not seed from LogOutput.log: " + ex.Message);
            }
        }

        /// <summary>
        /// Closes the file and renames it to its descriptive final name.
        /// Returns the final path, or null if the log was clean and clean logs are not kept.
        /// </summary>
        public string? FinalizeSession(bool crashed, bool keepHealthy)
        {
            lock (_gate)
            {
                if (_closed) return null;
                _closed = true;

                try
                {
                    _writer?.WriteLine($"=== end of session: {_errorCount} error(s), {_warningCount} warning(s) ===");
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch
                {
                    // Nothing useful to do - we are usually inside process shutdown here.
                }
                _writer = null;

                if (_errorCount == 0 && !keepHealthy)
                {
                    TryDelete(_activePath);
                    return null;
                }

                var name = NameBuilder.Build(_started, crashed, _errorCount, _bestError);
                var target = NameBuilder.MakeUnique(Path.Combine(_dir, name));

                try
                {
                    File.Move(_activePath, target);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not rename archive: " + ex.Message);
                    return _activePath;
                }
                return target;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose() => FinalizeSession(false, true);
    }
}
