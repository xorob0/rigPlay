// SPDX-License-Identifier: GPL-3.0-only
// PluginBridge.cs: the glue between SimHub and RigPlayHost. It points the pure code's log at SimHub's log, tells the
// host which SimHub it runs in, and starts and stops it with the plugin.
using System;
using System.Diagnostics;
using System.Reflection;
using SimHub.Plugins;

namespace RigPlayPlugin
{
    internal sealed class PluginBridge : IDisposable
    {
        private readonly RigPlay plugin;

        public PluginBridge(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        public RigPlayHost Host { get; private set; }

        public void Start(PluginManager pluginManager)
        {
            try
            {
                StartCore(pluginManager);
            }
            catch (Exception ex)
            {
                Log.Error("The tablet server could not start", ex);
            }
        }

        private void StartCore(PluginManager pluginManager)
        {
            PluginLog.Sink = Forward;
            var env = new HostEnvironment
            {
                PluginVersion = RigPlay.Version,
                SimHubVersion = DetectSimHubVersion(),
                MachineName = Environment.MachineName,
                SaveSettings = plugin.SaveSettings,
                SimHubDir = Dashboards.DashboardCatalog.FindSimHubDir(new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    SafeDirectoryOf(typeof(PluginManager).Assembly),
                }),
                SimHubWebPort = DetectSimHubWebPort(),
            };
            Host = new RigPlayHost(plugin.Settings, env);
            Host.Start();
            AttachSurface();
            Log.Info("Tablet server: control TCP " + (Host.Server.Status.Listening ? Host.Server.Port + " listening" : "not listening (" + Host.Server.Status.Error + ")")
                + ", beacon UDP " + plugin.Settings.DiscoveryPort + (Host.Beacon.Status.Running ? " running" : " not running")
                + ", host " + Host.DisplayName + " (" + plugin.Settings.HostId + "), SimHub " + (env.SimHubVersion ?? "?"));
        }

        public void Stop()
        {
            try
            {
                Host?.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Stopping the tablet server failed", ex);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// The properties and actions of docs/protocol.md §16. SimHub prefixes each name with the plugin class name, so
        /// "TabletConnected" appears as RigPlay.TabletConnected and "PlayPause" as RigPlay.PlayPause.
        /// </summary>
        private void AttachSurface()
        {
            Func<SurfaceSnapshot> s = () => plugin.Host?.Surface ?? SurfaceSnapshot.Empty;
            plugin.AttachDelegate("TabletConnected", () => s().TabletConnected);
            plugin.AttachDelegate("PhoneConnected", () => s().PhoneConnected);
            plugin.AttachDelegate("Screen", () => s().Screen);
            plugin.AttachDelegate("NowPlaying.Title", () => s().Title);
            plugin.AttachDelegate("NowPlaying.Artist", () => s().Artist);
            plugin.AttachDelegate("NowPlaying.Album", () => s().Album);
            plugin.AttachDelegate("NowPlaying.App", () => s().App);
            plugin.AttachDelegate("NowPlaying.Playing", () => s().Playing);
            plugin.AttachDelegate("NowPlaying.Position", () => plugin.Host?.NowPlayingPosition ?? 0.0);
            plugin.AttachDelegate("NowPlaying.Duration", () => s().Duration);
            plugin.AttachDelegate("NowPlaying.ArtworkPath", () => s().ArtworkPath);
            plugin.AttachDelegate("Nav.Active", () => s().NavActive);
            plugin.AttachDelegate("Nav.Maneuver", () => s().NavManeuver);
            plugin.AttachDelegate("Nav.Distance", () => s().NavDistance);
            plugin.AttachDelegate("Nav.Road", () => s().NavRoad);
            plugin.AttachDelegate("Nav.Eta", () => s().NavEta);

            foreach (SurfaceAction action in Enum.GetValues(typeof(SurfaceAction)))
            {
                var a = action;
                plugin.AddAction(SurfaceActions.Name(a), (manager, name) =>
                {
                    try { plugin.Host?.RunAction(a); } catch (Exception ex) { Log.Error("Action " + a + " failed", ex); }
                }, (manager, name) => { });
            }
            Log.Info("SimHub properties RigPlay.TabletConnected, PhoneConnected, Screen, NowPlaying.*, Nav.* and actions RigPlay."
                + string.Join(", RigPlay.", Enum.GetNames(typeof(SurfaceAction))) + " registered");
        }

        private static void Forward(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Debug: Log.Debug(message); break;
                case LogLevel.Info: Log.Info(message); break;
                case LogLevel.Warn: Log.Warn(message); break;
                default: Log.Error(message); break;
            }
        }

        private static string SafeDirectoryOf(Assembly assembly)
        {
            try { return System.IO.Path.GetDirectoryName(assembly.Location); } catch (Exception) { return null; }
        }

        /// <summary>
        /// SimHub keeps its web server port in its application settings (SimHubWPF.exe.config: SimHubWebPort). Read it
        /// from the loaded settings class by reflection; null when that fails (the page then uses 8888 or its override).
        /// </summary>
        private static int? DetectSimHubWebPort()
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name ?? "";
                    if (!name.StartsWith("SimHubWPF", StringComparison.OrdinalIgnoreCase)) continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    foreach (var type in types)
                    {
                        if (type == null || type.Name != "Settings" || type.Namespace == null || !type.Namespace.EndsWith(".Properties")) continue;
                        var port = type.GetProperty("SimHubWebPort", BindingFlags.Public | BindingFlags.Instance);
                        var instance = type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (port == null || instance == null) continue;
                        var value = Convert.ToInt32(port.GetValue(instance));
                        if (value > 0 && value <= 65535)
                        {
                            Log.Info("SimHub web dash server port from SimHub's settings: " + value);
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read SimHub's web server port: " + ex.Message);
            }
            return null;
        }

        /// <summary>SimHub's version as "9.12.6", from the SimHub executable; null when unknown.</summary>
        private static string DetectSimHubVersion()
        {
            try
            {
                // SimHub's own idea of its version (SimHub.Plugins.Configuration.SimHubVersion); SimHubWPF.exe is 1.0.0.0.
                var property = typeof(PluginManager).Assembly.GetType("SimHub.Plugins.Configuration")
                    ?.GetProperty("SimHubVersion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var value = property?.GetValue(null);
                // A SimHub.Plugins.VersionParser: GetVersionAsString() gives "9.12.6" (plus "bN" for betas).
                var asString = value?.GetType().GetMethod("GetVersionAsString", Type.EmptyTypes);
                var configured = asString != null ? asString.Invoke(value, null) as string : value as string;
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            }
            catch (Exception) { }
            try
            {
                // The installer's uninstaller carries the release version (9.12.6 on the test VM).
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var uninstaller = System.IO.Path.Combine(dir ?? "", "unins000.exe");
                if (System.IO.File.Exists(uninstaller))
                {
                    var product = FileVersionInfo.GetVersionInfo(uninstaller).ProductVersion;
                    if (!string.IsNullOrWhiteSpace(product) && !product.StartsWith("1.0.0")) return product.Trim();
                }
            }
            catch (Exception) { }
            // SimHubWPF.exe itself is versioned 1.0.0.0, which says nothing; better no version than a wrong one.
            return null;
        }
    }
}
