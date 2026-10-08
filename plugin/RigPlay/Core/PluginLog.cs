// SPDX-License-Identifier: GPL-3.0-only
// PluginLog.cs: the log used by the pure code (protocol, network, pairing, dashboards). It forwards to a sink the
// plugin sets in Init (Log.cs, SimHub's log); the tests leave it unset or point it at their output.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;

namespace RigPlayPlugin
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
    }

    public static class PluginLog
    {
        /// <summary>Where lines go. Null drops them. Set once, before the services start.</summary>
        public static volatile Action<LogLevel, string> Sink;

        public static void Debug(string message) { Write(LogLevel.Debug, message); }
        public static void Info(string message) { Write(LogLevel.Info, message); }
        public static void Warn(string message) { Write(LogLevel.Warn, message); }
        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Error(string message, Exception ex)
        {
            Write(LogLevel.Error, message + ": " + ex);
        }

        private static void Write(LogLevel level, string message)
        {
            var sink = Sink;
            if (sink == null) return;
            try { sink(level, message); } catch { }
        }

        /// <summary>The first 4 characters of a secret, for logs (spec §15: never log a token).</summary>
        public static string Redact(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "(none)";
            return (secret.Length <= 4 ? secret : secret.Substring(0, 4)) + "…";
        }
    }
}
