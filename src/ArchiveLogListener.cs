using System;
using BepInEx.Logging;

namespace ATLAS
{
    internal sealed class ArchiveLogListener : ILogListener
    {
        private volatile ArchiveSession _session;
        private readonly LogLevel _mask;

        public ArchiveLogListener(ArchiveSession session, LogLevel minLevel)
        {
            _session = session;
            _mask = BuildMask(minLevel);
        }

        // Present on BepInEx 5.4.19+. If your BepInEx reference predates it, delete this
        // property - the mask check inside LogEvent already does the filtering on its own.
        public LogLevel LogLevelFilter => _mask;

        public void Retarget(ArchiveSession session) => _session = session;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if ((eventArgs.Level & _mask) == 0) return;

            // Never let an archiving failure take down the logging pipeline.
            try { _session.Write(eventArgs); }
            catch { }
        }

        /// <summary>
        /// BepInEx LogLevel is a flag enum ordered most-severe-first
        /// (Fatal=1, Error=2, Warning=4, Message=8, Info=16, Debug=32),
        /// so "at least minLevel" means every flag numerically &lt;= minLevel.
        /// </summary>
        private static LogLevel BuildMask(LogLevel minLevel)
        {
            var mask = LogLevel.None;
            foreach (LogLevel flag in Enum.GetValues(typeof(LogLevel)))
            {
                if (flag == LogLevel.None || flag == LogLevel.All) continue;
                if ((int)flag <= (int)minLevel) mask |= flag;
            }
            return mask;
        }

        public void Dispose() { }
    }
}
