// SPDX-License-Identifier: GPL-3.0-only
// RigPlayHost.cs: everything rigPlay runs inside SimHub, without SimHub: the discovery beacon, the control server and
// the per-session state. The plugin class creates one in Init and disposes it in End; the page reads it.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RigPlayPlugin.Dashboards;
using RigPlayPlugin.Net;
using RigPlayPlugin.Pairing;
using RigPlayPlugin.Protocol;
using RigPlayPlugin.Telemetry;

namespace RigPlayPlugin
{
    /// <summary>What the host needs from its surroundings.</summary>
    public sealed class HostEnvironment
    {
        public string PluginVersion { get; set; } = "0.0.0";
        public string SimHubVersion { get; set; }
        public string MachineName { get; set; } = Environment.MachineName;

        /// <summary>SimHub's install folder, which holds DashTemplates; null when unknown.</summary>
        public string SimHubDir { get; set; }

        /// <summary>The web dash server port from SimHub's own settings, when it could be read.</summary>
        public int? SimHubWebPort { get; set; }

        /// <summary>Persists the settings object (SimHub's SaveCommonSettings).</summary>
        public Action SaveSettings { get; set; } = () => { };
    }

    public sealed class RigPlayHost : IDisposable
    {
        private readonly object sync = new object();
        private readonly HostEnvironment env;
        private readonly IClock clock;
        private bool started;

        public RigPlayHost(RigPlaySettings settings, HostEnvironment env, IClock clock = null, SessionTimings timings = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.env = env ?? new HostEnvironment();
            this.clock = clock ?? SystemClock.Instance;
            Timings = timings ?? SessionTimings.Default;
            Pairing = new PairingService(settings, () => this.env.SaveSettings(), this.clock);
            Pairing.Changed += RaiseChanged;
            TelemetrySampler = new TelemetrySampler();
            TelemetrySender = new TelemetrySender(TelemetrySampler, () => Server, () => Settings);
            Probe = new WebDashProbe(() => EffectiveWebDashPort);
            Probe.Changed += () =>
            {
                PushState();
                RaiseChanged();
            };
        }

        /// <summary>SimHub frames in, the telemetry message out (spec §6.9). RigPlay.DataUpdate feeds it.</summary>
        public TelemetrySampler TelemetrySampler { get; }

        /// <summary>The 10 Hz telemetry timer.</summary>
        public TelemetrySender TelemetrySender { get; }

        /// <summary>Tests turn the telemetry timer off and call TelemetrySender.Tick themselves.</summary>
        public bool TelemetryTimerEnabled { get; set; } = true;

        /// <summary>Features this plugin offers in welcome (spec §7.3).</summary>
        public static readonly string[] OfferedFeatures = { Features.Telemetry, Features.IdleDashboard, Features.Mic };

        /// <summary>The primary tablet's artwork as a file for dashboards (RigPlay.NowPlaying.ArtworkPath, spec §16.1).</summary>
        public ArtworkFile Artwork { get; set; } = new ArtworkFile();

        /// <summary>Tests turn the periodic web dash probe off.</summary>
        public bool ProbeEnabled { get; set; } = true;

        /// <summary>PINs, tokens and the paired-tablet list (spec §8).</summary>
        public PairingService Pairing { get; }

        public RigPlaySettings Settings { get; }

        public SessionTimings Timings { get; }

        public ControlServer Server { get; private set; }

        public DiscoveryBeacon Beacon { get; private set; }

        /// <summary>
        /// Whether the audio receiver takes what tablets send (state.audio.enabled, spec §6.6). AudioGlue sets it to "the
        /// receiver's UDP port is bound"; until then, tablets are told to play locally. Call <see cref="PushState"/> after
        /// changing what it returns.
        /// </summary>
        public Func<bool> AudioEnabled { get; set; } = () => false;

        /// <summary>The UDP port for state.audio.port; null (or no delegate) means Settings.AudioPort.</summary>
        public Func<int?> AudioPort { get; set; }

        /// <summary>The receiver's formats for state.audio.formats, most preferred first; null means pcm_s16le.</summary>
        public Func<List<string>> AudioFormats { get; set; }

        /// <summary>
        /// state.mic.enabled for sessions with feature mic (spec §6.6, §6.13): the plugin answers micStart. MicGlue sets it
        /// to "Microphone to the phone is on and an input device was found". Call <see cref="PushState"/> after a change.
        /// </summary>
        public Func<bool> MicEnabled { get; set; } = () => false;

        /// <summary>The monotonic clock the sessions use (the microphone watchdog compares against LastLineAtMs).</summary>
        public IClock Clock => clock;

        /// <summary>Tests: listen on this port instead of Settings.ControlPort (0 picks a free one).</summary>
        public int? ControlPortOverride { get; set; }

        /// <summary>Tests turn the beacon off so they do not broadcast on the LAN.</summary>
        public bool BeaconEnabled { get; set; } = true;

        /// <summary>Something the page shows changed (sessions, status, server state). Raised on any thread.</summary>
        public event Action Changed;

        /// <summary>The name tablets show for this PC.</summary>
        public string DisplayName => string.IsNullOrEmpty(Settings.HostName) ? env.MachineName : Settings.HostName;

        // SimHub surface (spec §12, §16)

        private volatile SurfaceSnapshot surface = SurfaceSnapshot.Empty;

        /// <summary>The primary tablet as the SimHub properties see it. Cheap to read from SimHub's data thread.</summary>
        public SurfaceSnapshot Surface => surface;

        /// <summary>Id of the primary tablet's session (spec §12); 0 when there is none.</summary>
        public int PrimarySessionId => surface.PrimarySessionId;

        /// <summary>The primary tablet's playback position now, extrapolated while playing (spec §6.7).</summary>
        public double NowPlayingPosition => surface.PositionAt(clock.NowMs);

        public ClientSession PrimarySession
        {
            get
            {
                var id = PrimarySessionId;
                return id == 0 ? null : Server?.PairedSessions.FirstOrDefault(s => s.Id == id);
            }
        }

        /// <summary>Runs a SimHub action: sends its command to the primary tablet. False (logged at debug level) when there is none.</summary>
        public bool RunAction(SurfaceAction action)
        {
            var primary = PrimarySession;
            if (primary == null)
            {
                PluginLog.Debug("Action " + action + " ignored: no primary tablet");
                return false;
            }
            var command = SurfaceActions.CommandFor(action, primary.LastStatus?.Screen);
            var sent = primary.Send(command);
            PluginLog.Debug("Action " + action + " -> " + MessageCodec.Encode(command) + " to " + primary + (sent ? "" : " (failed)"));
            return sent;
        }

        private readonly object surfaceLock = new object();

        private void UpdateSurface()
        {
            // Serialised so that the last snapshot written is computed from the latest sessions.
            lock (surfaceLock) UpdateSurfaceLocked();
        }

        private void UpdateSurfaceLocked()
        {
            var paired = Server?.PairedSessions ?? new List<ClientSession>();
            var primaryId = PrimaryTabletSelector.Select(paired.Select(s => new TabletCandidate
            {
                SessionId = s.Id,
                Paired = true,
                PhoneConnected = s.LastStatus != null && s.LastStatus.PhoneConnected,
                PhoneConnectedOrder = s.PhoneConnectedOrder,
                PairedOrder = s.PairedOrder,
            }));
            var primary = paired.FirstOrDefault(s => s.Id == primaryId);
            var status = primary?.LastStatus;
            var artworkPath = Artwork.Show(primary?.LastArtwork);
            surface = new SurfaceSnapshot
            {
                TabletConnected = paired.Count > 0,
                PrimarySessionId = primaryId,
                PhoneConnected = status != null && status.PhoneConnected,
                Screen = status?.Screen ?? Screens.Off,
                NowPlaying = status?.NowPlaying,
                StatusReceivedAtMs = primary?.LastStatusAtMs ?? 0,
                Nav = status?.Nav,
                NavEta = SurfaceSnapshot.FormatEta(status?.Nav?.EtaEpochS),
                ArtworkPath = artworkPath,
            };
        }

        public string PluginVersion => env.PluginVersion;

        public string SimHubVersion => env.SimHubVersion;

        public void Start()
        {
            lock (sync)
            {
                if (started) return;
                started = true;
            }
            StartServer();
            Beacon = new DiscoveryBeacon(BuildBeacon);
            if (BeaconEnabled) Beacon.Start();
            RefreshDashboards();
            PluginLog.Info(Dashboards.Count + " dashboard(s) in " + (env.SimHubDir ?? "(SimHub folder not found)") + ", web dash port " + EffectiveWebDashPort);
            if (ProbeEnabled) Probe.Start();
            if (TelemetryTimerEnabled) TelemetrySender.Start();
            RaiseChanged();
        }

        public void Stop()
        {
            lock (sync)
            {
                if (!started) return;
                started = false;
            }
            try { TelemetrySender.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the telemetry sender failed", ex); }
            try { Probe.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the web dash probe failed", ex); }
            try { Beacon?.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the beacon failed", ex); }
            try { Server?.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the control server failed", ex); }
            RaiseChanged();
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Restarts the control server, e.g. after the control port changed. Sessions get `shutdown`.</summary>
        public void RestartServer()
        {
            try { Server?.Stop("control port changed"); } catch (Exception ex) { PluginLog.Error("Stopping the control server failed", ex); }
            StartServer();
            RaiseChanged();
        }

        /// <summary>Sends every Paired session its state if it changed.</summary>
        public void PushState()
        {
            Server?.BroadcastState();
        }

        public BeaconMessage BuildBeacon()
        {
            return new BeaconMessage
            {
                Name = DisplayName,
                HostId = Settings.HostId,
                Version = env.PluginVersion,
                SimhubVersion = env.SimHubVersion,
                ControlPort = Settings.ControlPort,
                AudioPort = Settings.AudioPort,
                Protocol = ProtocolDefaults.ProtocolVersion,
                MinProtocol = ProtocolDefaults.MinProtocolVersion,
            };
        }

        public WelcomeMessage BuildWelcomeTemplate()
        {
            return new WelcomeMessage
            {
                HostId = Settings.HostId,
                Name = DisplayName,
                Version = env.PluginVersion,
                SimhubVersion = env.SimHubVersion,
            };
        }

        /// <summary>The state for one Paired session (spec §6.6).</summary>
        public StateMessage BuildState(ClientSession session)
        {
            bool audio;
            try { audio = AudioEnabled != null && AudioEnabled(); } catch (Exception) { audio = false; }
            int? audioPort = null;
            try { audioPort = AudioPort?.Invoke(); } catch (Exception) { }
            if (audioPort == null || audioPort < 1 || audioPort > 65535) audioPort = Settings.AudioPort;
            List<string> formats = null;
            try { formats = AudioFormats?.Invoke(); } catch (Exception) { }
            if (formats == null || formats.Count == 0) formats = new List<string> { AudioStreams.PcmS16Le };
            var port = EffectiveWebDashPort;
            var local = session.Local?.Address;
            return new StateMessage
            {
                // Sent even when the probe fails, so the tablet can explain the problem (spec §11).
                DashboardUrl = DashboardUrls.Build(local, port, Settings.SelectedDashboard),
                IdleDashboardUrl = session.HasFeature(Features.IdleDashboard) ? DashboardUrls.Build(local, port, Settings.IdleDashboard) : null,
                DashboardServer = Probe.Current,
                Audio = new AudioInfo { Enabled = audio, Port = audioPort.Value, Formats = formats },
                Mic = session.HasFeature(Features.Mic) ? new MicInfo { Enabled = SafeMicEnabled() } : null,
            };
        }

        private bool SafeMicEnabled()
        {
            try { return MicEnabled != null && MicEnabled(); } catch (Exception) { return false; }
        }

        // Dashboards (spec §11)

        /// <summary>SimHub's install folder (holds DashTemplates), or null when unknown.</summary>
        public string SimHubDir => env.SimHubDir;

        /// <summary>The web dash server port: the page's override, else SimHub's setting, else 8888.</summary>
        public int EffectiveWebDashPort => Settings.WebDashPort > 0 ? Settings.WebDashPort : env.SimHubWebPort ?? ProtocolDefaults.WebDashPort;

        /// <summary>Probes the web dash server every 10 s.</summary>
        public WebDashProbe Probe { get; }

        /// <summary>The installed dashboards, as of the last <see cref="RefreshDashboards"/>.</summary>
        public List<DashboardInfo> Dashboards { get; private set; } = new List<DashboardInfo>();

        public List<DashboardInfo> RefreshDashboards()
        {
            var dir = string.IsNullOrEmpty(env.SimHubDir) ? null : System.IO.Path.Combine(env.SimHubDir, DashboardCatalog.DashTemplatesFolder);
            Dashboards = DashboardCatalog.Enumerate(dir);
            return Dashboards;
        }

        /// <summary>The page changed the dashboard selection or the port: save, re-probe and push state.</summary>
        public void DashboardSettingsChanged()
        {
            env.SaveSettings();
            PushState();
            System.Threading.Tasks.Task.Run(() => Probe.ProbeNow());
            RaiseChanged();
        }

        /// <summary>Deny on the page: discards the pending PIN and tells the tablet `denied` (spec §8 step 4).</summary>
        public void DenyPairing(string tabletId)
        {
            if (!Pairing.Deny(tabletId)) return;
            var session = Server?.FindByTabletId(tabletId);
            if (session == null || session.State != SessionState.Unpaired) return;
            session.Send(PairResultMessage.Failure(PairReasons.Denied));
            session.RestartPairRequestTimer();
        }

        /// <summary>Forget on the page: deletes the token hash and closes the tablet's live session with `forgotten`.</summary>
        public void ForgetTablet(string tabletId)
        {
            var known = Pairing.Forget(tabletId);
            var session = Server?.FindByTabletId(tabletId);
            if (session != null && (known || session.State == SessionState.Paired))
                session.CloseWithError(ErrorCodes.Forgotten, "this tablet was removed on the PC");
            RaiseChanged();
        }

        private void StartServer()
        {
            var server = new ControlServer(ControlPortOverride ?? Settings.ControlPort, BuildWelcomeTemplate, Timings, clock)
            {
                StateFactory = BuildState,
                Pairing = Pairing,
                PluginFeatures = OfferedFeatures,
            };
            server.SessionsChanged += () =>
            {
                UpdateSurface();
                RaiseChanged();
            };
            server.AudioStartReceived += (s, m) => RaiseAudio(AudioStart, h => h(m.Stream, m.Format, m.SampleRate, m.Channels, s.Remote.Address));
            server.AudioStopReceived += (s, m) => RaiseAudio(AudioStop, h => h(m.Stream, s.Remote.Address));
            server.SessionClosed += s =>
            {
                if (s.PairedOrder > 0) RaiseAudio(SessionLost, h => h(s.Remote.Address));
                if (s.PairedOrder > 0) RaiseAudio(PairedSessionClosed, h => h(s));
            };
            server.MicStartReceived += (s, m) => RaiseAudio(MicStart, h => h(s, m));
            server.MicStopReceived += (s, m) => RaiseAudio(MicStop, h => h(s));
            Server = server;
            server.Start();
        }

        // Audio hooks for the receiver (#24, spec §10.1). Raised on network threads; keep handlers short.

        /// <summary>A Paired session sent audioStart: stream, format, sample rate (Hz), channels, the tablet's IP.</summary>
        public event Action<string, string, int, int, IPAddress> AudioStart;

        /// <summary>A Paired session sent audioStop: stream, the tablet's IP.</summary>
        public event Action<string, IPAddress> AudioStop;

        /// <summary>A Paired session closed (link loss, replaced, forgotten, shutdown): every stream from that IP stops.</summary>
        public event Action<IPAddress> SessionLost;

        /// <summary>A Paired session with feature mic sent micStart (spec §6.13). Raised on a network thread.</summary>
        public event Action<ClientSession, MicStartMessage> MicStart;

        /// <summary>A Paired session with feature mic sent micStop.</summary>
        public event Action<ClientSession> MicStop;

        /// <summary>A Paired session closed; the session itself, for owners keyed by session (the microphone).</summary>
        public event Action<ClientSession> PairedSessionClosed;

        /// <summary>Remote IPs of the Paired sessions: the only sources audio is accepted from (spec §10.1).</summary>
        public List<IPAddress> PairedAddresses
        {
            get { return Server == null ? new List<IPAddress>() : Server.PairedSessions.Select(s => s.Remote.Address).Distinct().ToList(); }
        }

        /// <summary>For Receiver.SourceFilter: true when <paramref name="address"/> belongs to a Paired session.</summary>
        public bool IsPairedAddress(IPAddress address)
        {
            var normalized = NetUtil.Normalize(address);
            return normalized != null && Server != null && Server.PairedSessions.Any(s => s.Remote.Address.Equals(normalized));
        }

        private static void RaiseAudio<T>(T handler, Action<T> invoke) where T : class
        {
            if (handler == null) return;
            try { invoke(handler); } catch (Exception ex) { PluginLog.Error("An audio handler failed", ex); }
        }

        internal void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { PluginLog.Error("A change handler failed", ex); }
        }
    }
}
