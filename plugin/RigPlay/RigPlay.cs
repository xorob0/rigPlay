// SPDX-License-Identifier: GPL-3.0-only
// RigPlay.cs: the SimHub plugin class. Reads and saves RigPlaySettings, offers the rigPlay page in SimHub's left
// menu, starts the tablet server (PluginBridge: discovery, pairing, dashboards, SimHub surface) and the audio
// pipeline, and connects the two (AudioGlue). As an IDataPlugin it copies each SimHub frame into the telemetry sampler
// (#40); the host's sender turns the latest frame into `telemetry` messages at 10 Hz.
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameReaderCommon;
using RigPlayPlugin.Telemetry;
using SimHub.Plugins;

namespace RigPlayPlugin
{
    [PluginName("rigPlay")]
    [PluginAuthor("xorob0")]
    [PluginDescription("Pairs rigPlay CarPlay tablets with SimHub, picks the dashboard they show and plays their audio on this PC.")]
    public class RigPlay : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        /// <summary>SimHub stores the settings as PluginsData/Common/RigPlay.RigPlaySettings.json.</summary>
        public const string SettingsKey = "RigPlaySettings";

        public const string IconResource = "RigPlay.Icon.png";

        private ImageSource icon;
        private bool iconLoaded;
        private PluginBridge bridge;
        private global::RigPlayPlugin.Audio.AudioGlue audioGlue;
        private global::RigPlayPlugin.Audio.TalkPauser talkPauser;
        private global::RigPlayPlugin.Audio.MicGlue micGlue;

        /// <summary>Discovery, control server and tablet state; null before Init and after End.</summary>
        public RigPlayHost Host => bridge?.Host;

        public PluginManager PluginManager { get; set; }

        /// <summary>The live settings object; the page edits it and calls SaveSettings().</summary>
        public RigPlaySettings Settings { get; private set; } = new RigPlaySettings();

        /// <summary>The audio receiver and output (#24); the control server forwards audioStart / audioStop to it.</summary>
        public global::RigPlayPlugin.Audio.AudioPipeline Audio { get; private set; }

        /// <summary>The PC microphone to the phone (#34): micStart / micStop from tablets; null without a tablet server.</summary>
        public global::RigPlayPlugin.Audio.MicSender Mic { get; private set; }

        public string LeftMenuTitle => "rigPlay";

        public ImageSource PictureIcon
        {
            get
            {
                if (!iconLoaded)
                {
                    iconLoaded = true;
                    icon = LoadIcon();
                }
                return icon;
            }
        }

        /// <summary>The plugin version from Directory.Build.props, e.g. "0.1.0".</summary>
        public static string Version
        {
            get
            {
                var assembly = typeof(RigPlay).Assembly;
                var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .Select(a => a.InformationalVersion)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(informational))
                {
                    var plus = informational.IndexOf('+');
                    return plus > 0 ? informational.Substring(0, plus) : informational;
                }
                var version = assembly.GetName().Version;
                return version == null ? "0.0.0" : version.Major + "." + version.Minor + "." + version.Build;
            }
        }

        public void Init(PluginManager pluginManager)
        {
            Log.Info("rigPlay plugin " + Version + " starting");
            LoadSettings();
            Log.Info("Settings loaded: control port " + Settings.ControlPort + ", discovery port " + Settings.DiscoveryPort
                + ", audio port " + Settings.AudioPort + ", " + Settings.PairedTablets.Count + " paired tablet(s)");
            bridge = new PluginBridge(this);
            bridge.Start(pluginManager);
            // Writes the normalised file back, so a repaired or first-run file is on disk from the start.
            SaveSettings();
            Audio = new global::RigPlayPlugin.Audio.AudioPipeline(() => Settings);
            Audio.LearnedBufferChanged += SaveSettings; // the depth learned from the network survives a restart
            AttachAudio();
            AttachMic();
            AttachTalkPauser();
        }

        /// <summary>
        /// The talk watch's pause mode (#58): while a watched program talks and the phone is playing, toggle play/pause on
        /// the primary tablet, and toggle back when it is quiet again. Duck mode is handled inside the audio pipeline.
        /// </summary>
        private void AttachTalkPauser()
        {
            talkPauser = new global::RigPlayPlugin.Audio.TalkPauser(
                () => Host?.Surface.Playing ?? false,
                () => Host != null && Host.RunAction(global::RigPlayPlugin.SurfaceAction.PlayPause));
            Audio.TalkingChanged += talking =>
            {
                var host = Host;
                if (host == null) return;
                if (Settings.TalkMode != RigPlaySettings.TalkModePause)
                {
                    talkPauser.Reset();
                    return;
                }
                if (talkPauser.OnTalking(talking, host.Clock.NowMs)) Log.Info("Talk watch: " + (talking ? "a watched program talks, music paused" : "quiet again, music resumed"));
            };
        }

        /// <summary>The PC microphone (#34): tablets with feature mic get it on micStart while "Microphone to the phone" is on.</summary>
        private void AttachMic()
        {
            try
            {
                var host = Host;
                if (host == null) return;
                Mic = new global::RigPlayPlugin.Audio.MicSender(() => Settings, global::RigPlayPlugin.Audio.MicCaptureFactory.TryCreate(), host.Clock);
                micGlue = new global::RigPlayPlugin.Audio.MicGlue(host, Mic);
                Mic.StartTimer();
                Log.Info("Microphone to the phone " + (Settings.MicEnabled ? "on" : "off") + ", " + (Mic.Available ? "an input device is present" : "no input device found"));
            }
            catch (Exception ex)
            {
                Log.Error("Setting up the PC microphone failed", ex);
            }
        }

        /// <summary>
        /// Audio plays only for paired tablets: datagrams from other sources are dropped and a stream starts only with
        /// its audioStart. state.audio.enabled is true while the audio port is bound, even with no output device (the
        /// page then shows why nothing plays). Without a tablet server no source is accepted.
        /// </summary>
        private void AttachAudio()
        {
            try
            {
                var host = Host;
                if (host == null)
                {
                    Audio.Receiver.AutoStartOnFirstFlag = false;
                    Audio.Receiver.SourceFilter = address => false;
                    Log.Warn("Audio is received from no tablet: the tablet server did not start");
                    return;
                }
                audioGlue = new global::RigPlayPlugin.Audio.AudioGlue(host, Audio.Receiver);
                Audio.PortChanged += audioGlue.ListenerChanged;
                Audio.FormatsChanged += audioGlue.ListenerChanged;
                Log.Info("Audio receiver wired to the tablet server: " + (Audio.Receiver.Listening ? "UDP " + Audio.Receiver.BoundPort + ", paired tablets only" : "not listening, tablets play locally"));
            }
            catch (Exception ex)
            {
                Log.Error("Connecting the audio receiver to the tablet server failed", ex);
            }
        }

        /// <summary>
        /// SimHub's 60 Hz data callback. Copies the frame into a struct and hands it to the telemetry sampler: no
        /// allocation and no property lookups per frame; names that SimHub may build on each read are read once a second.
        /// </summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            var sampler = Host?.TelemetrySampler;
            if (sampler == null) return;
            try
            {
                var input = TelemetryInput.Empty;
                var d = data.NewData;
                input.GameRunning = data.GameRunning && d != null;
                if (input.GameRunning)
                {
                    input.SpeedKmh = d.SpeedKmh;
                    input.Gear = d.Gear;
                    input.Rpm = d.Rpms;
                    input.InPit = d.IsInPit != 0;
                    input.InPitLane = d.IsInPitLane != 0;
                    input.YawDeg = d.OrientationYaw;
                    var c = d.CarCoordinates;
                    if (c != null && c.Length >= 3)
                    {
                        input.X = c[0];
                        input.Y = c[1];
                        input.Z = c[2];
                    }
                    input.SessionRestart = d.IsSessionRestart;
                    input.TrackPct = d.TrackPositionPercent;
                    if (slowCountdown-- <= 0)
                    {
                        slowCountdown = 60;
                        ReadSlow(pluginManager, d);
                        gameName = data.GameName;
                    }
                    input.TrackName = trackName;
                    input.SessionType = sessionType;
                    input.TrackCode = trackCode;
                    input.GameName = gameName;
                    input.FuelPercent = slow.FuelPercent;
                    input.Fuel = slow.Fuel;
                    input.MaxFuel = slow.MaxFuel;
                    input.FuelRemainingLaps = slow.FuelRemainingLaps;
                    input.TrackLengthM = slow.TrackLengthM;
                    input.TimeOfDaySec = slow.TimeOfDaySec;
                    input.Headlights = slow.Headlights;
                    input.CustomNight = slow.CustomNight;
                }
                else
                {
                    slowCountdown = 0;
                }
                sampler.Update(ref input);
            }
            catch (Exception ex)
            {
                if (!dataUpdateFailed) Log.Error("Reading SimHub's game data for telemetry failed (logged once)", ex);
                dataUpdateFailed = true;
            }
        }

        private int slowCountdown;
        private string trackName;
        private string sessionType;
        private string trackCode;
        private string gameName;
        private bool dataUpdateFailed;
        private TelemetryInput slow = TelemetryInput.Empty;

        /// <summary>
        /// Once a second: names SimHub may build per read, fuel and track length (#46), and the night sources (#45),
        /// which are property lookups by name and box their values.
        /// </summary>
        private void ReadSlow(PluginManager pluginManager, StatusDataBase d)
        {
            trackName = d.TrackNameWithConfig;
            if (string.IsNullOrWhiteSpace(trackName)) trackName = d.TrackName;
            sessionType = d.SessionTypeName;
            trackCode = d.TrackCode;

            slow.FuelPercent = d.FuelPercent;
            slow.Fuel = d.Fuel;
            slow.MaxFuel = d.MaxFuel;
            var laps = d.EstimatedFuelRemaingLaps;
            slow.FuelRemainingLaps = laps.HasValue ? laps.Value : double.NaN;
            var length = d.TrackLength > 0 ? d.TrackLength : d.ReportedTrackLength;
            slow.TrackLengthM = length > 0 ? length : double.NaN;

            Func<string, object> read = name => pluginManager.GetPropertyValue(name);
            slow.TimeOfDaySec = NightSources.FirstNumber(NightSources.TimeOfDayProperties, read);
            var lights = NightSources.FirstNumber(NightSources.HeadlightProperties, read);
            slow.Headlights = double.IsNaN(lights) ? -1 : lights != 0 ? 1 : 0;
            var custom = Settings.Telemetry.NightProperty;
            slow.CustomNight = string.IsNullOrEmpty(custom) ? -1 : NightSources.ToFlag(SafeRead(pluginManager, custom));
        }

        private static object SafeRead(PluginManager pluginManager, string name)
        {
            try { return pluginManager.GetPropertyValue(name); } catch (Exception) { return null; }
        }

        public void End(PluginManager pluginManager)
        {
            micGlue?.Dispose();
            micGlue = null;
            Mic?.Dispose();
            audioGlue?.Dispose();
            audioGlue = null;
            Audio?.Dispose();
            SaveSettings();
            bridge?.Stop();
            Log.Info("rigPlay plugin stopped");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            try
            {
                return new SettingsControl(this);
            }
            catch (Exception ex)
            {
                Log.Error("The settings page could not be built", ex);
                return new UserControl
                {
                    Content = new TextBlock
                    {
                        Text = "The rigPlay page could not be displayed: " + ex.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(24),
                    },
                };
            }
        }

        public void SaveSettings()
        {
            try
            {
                Settings.Normalize();
                this.SaveCommonSettings(SettingsKey, Settings);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the settings failed", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                Settings = this.ReadCommonSettings<RigPlaySettings>(SettingsKey, () => new RigPlaySettings()) ?? new RigPlaySettings();
            }
            catch (Exception ex)
            {
                Log.Error("Reading the settings failed; using the defaults", ex);
                Settings = new RigPlaySettings();
            }
            Settings.Normalize();
        }

        private static ImageSource LoadIcon()
        {
            try
            {
                using (var stream = typeof(RigPlay).Assembly.GetManifestResourceStream(IconResource))
                {
                    if (stream == null)
                    {
                        Log.Warn("The menu icon resource " + IconResource + " is missing");
                        return null;
                    }
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    return frame;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load the menu icon: " + ex.Message);
                return null;
            }
        }
    }
}
