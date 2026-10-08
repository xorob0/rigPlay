// SPDX-License-Identifier: GPL-3.0-only
// Log.cs: SimHub's own log (SimHub.Logging.Current, a log4net ILog writing to SimHub's Logs folder) behind a
// "[rigPlay]" prefix and a try/catch, so that a logging failure can never take the plugin down.
using System;

namespace RigPlayPlugin
{
    internal static class Log
    {
        public const string Prefix = "[rigPlay] ";

        public static void Debug(string message)
        {
            try { SimHub.Logging.Current.Debug(Prefix + message); } catch { }
        }

        public static void Info(string message)
        {
            try { SimHub.Logging.Current.Info(Prefix + message); } catch { }
        }

        public static void Warn(string message)
        {
            try { SimHub.Logging.Current.Warn(Prefix + message); } catch { }
        }

        public static void Error(string message)
        {
            try { SimHub.Logging.Current.Error(Prefix + message); } catch { }
        }

        public static void Error(string message, Exception ex)
        {
            Error(message + ": " + ex);
        }
    }
}
