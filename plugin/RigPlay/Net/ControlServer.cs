// SPDX-License-Identifier: GPL-3.0-only
// ControlServer.cs: the TCP control channel (spec §5). Listens on all IPv4 interfaces, refuses non-LAN peers (§15),
// keeps one ClientSession per tabletId (§12, `replaced`), hands pairRequests to the pairing authority (§8), sends
// each Paired session its own `state` when it changes (§6.6), and says `shutdown` to everyone before a deliberate
// stop (§9). A port that cannot be bound is reported in Status, never thrown (§2).
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Net
{
    /// <summary>Decides pairRequests (spec §8). The PairingService implements it.</summary>
    public interface IPairingAuthority
    {
        /// <summary>The answer to one pairRequest from an Unpaired session. ok: true pairs the session.</summary>
        PairResultMessage HandlePairRequest(ClientSession session, PairRequestMessage request);

        /// <summary>A session closed; drop whatever was pending for it.</summary>
        void SessionClosed(ClientSession session);
    }

    /// <summary>Refuses every pairRequest. Used until a real authority is set.</summary>
    public sealed class DenyAllPairing : IPairingAuthority
    {
        public PairResultMessage HandlePairRequest(ClientSession session, PairRequestMessage request)
        {
            return PairResultMessage.Failure(PairReasons.Denied);
        }

        public void SessionClosed(ClientSession session) { }
    }

    /// <summary>What the page shows about the listener.</summary>
    public sealed class ListenerStatus
    {
        public bool Listening { get; set; }
        public int Port { get; set; }

        /// <summary>Why the listener is not running; null when it is, or when it was stopped on purpose.</summary>
        public string Error { get; set; }
    }

    public sealed class ControlServer : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<ClientSession> sessions = new List<ClientSession>();
        private readonly int requestedPort;
        private readonly SessionTimings timings;
        private readonly IClock clock;
        private readonly Func<WelcomeMessage> welcomeTemplate;
        private TcpListener listener;
        private volatile bool running;
        private long orderCounter;

        /// <param name="port">TCP port; 0 picks a free one (tests).</param>
        /// <param name="welcomeTemplate">hostId, name, version and simhubVersion for welcome; called per session.</param>
        public ControlServer(int port, Func<WelcomeMessage> welcomeTemplate, SessionTimings timings = null, IClock clock = null)
        {
            requestedPort = port;
            this.welcomeTemplate = welcomeTemplate ?? throw new ArgumentNullException(nameof(welcomeTemplate));
            this.timings = timings ?? SessionTimings.Default;
            this.clock = clock ?? SystemClock.Instance;
            Status = new ListenerStatus { Port = port };
        }

        public IPairingAuthority Pairing { get; set; } = new DenyAllPairing();

        /// <summary>Builds the state for one Paired session (URLs depend on the connection, spec §6.6). Null: no state.</summary>
        public Func<ClientSession, StateMessage> StateFactory { get; set; }

        /// <summary>Features this plugin offers, in the order welcome lists them (spec §7.3).</summary>
        public IReadOnlyList<string> PluginFeatures { get; set; } = new[] { Features.IdleDashboard };

        public ListenerStatus Status { get; private set; }

        /// <summary>The bound port once listening (differs from the requested one when that was 0).</summary>
        public int Port => Status.Port;

        /// <summary>Sessions were added, removed, said hello, paired or changed status. Raised on network threads.</summary>
        public event Action SessionsChanged;

        public event Action<ClientSession> SessionPaired;
        public event Action<ClientSession> SessionClosed;
        public event Action<ClientSession, StatusMessage> StatusReceived;
        public event Action<ClientSession, AudioStartMessage> AudioStartReceived;
        public event Action<ClientSession, AudioStopMessage> AudioStopReceived;
        public event Action<ClientSession, ArtworkMessage> ArtworkReceived;
        public event Action<ClientSession, MicStartMessage> MicStartReceived;
        public event Action<ClientSession, MicStopMessage> MicStopReceived;

        /// <summary>Starts listening. False (with Status.Error set) when the port cannot be bound.</summary>
        public bool Start()
        {
            lock (sync)
            {
                if (running) return true;
                try
                {
                    var l = new TcpListener(IPAddress.Any, requestedPort);
                    try { l.ExclusiveAddressUse = true; } catch (Exception) { }
                    l.Start();
                    listener = l;
                    running = true;
                    Status = new ListenerStatus { Listening = true, Port = ((IPEndPoint)l.LocalEndpoint).Port };
                }
                catch (SocketException ex)
                {
                    Status = new ListenerStatus { Listening = false, Port = requestedPort, Error = DescribeBindError(ex, requestedPort) };
                    PluginLog.Error("Control server could not listen on TCP " + requestedPort + ": " + ex.SocketErrorCode + " " + ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    Status = new ListenerStatus { Listening = false, Port = requestedPort, Error = "Could not listen on TCP port " + requestedPort + ": " + ex.Message };
                    PluginLog.Error("Control server could not start", ex);
                    return false;
                }
            }
            PluginLog.Info("Control server listening on TCP " + Status.Port);
            Task.Run(AcceptLoop);
            return true;
        }

        /// <summary>Sends `error shutdown` (fatal) to every session, closes them and stops listening (spec §9, §13.3).</summary>
        public void Stop(string reason = "SimHub is closing")
        {
            TcpListener l;
            lock (sync)
            {
                if (!running && listener == null) return;
                running = false;
                l = listener;
                listener = null;
                Status = new ListenerStatus { Listening = false, Port = Status.Port };
            }
            try { l?.Stop(); } catch { }
            var open = Sessions;
            foreach (var s in open) s.CloseWithError(ErrorCodes.Shutdown, reason);
            // Give each tablet the linger time to read the line and close its end (no RST that would eat it).
            foreach (var s in open) s.WaitClosed(timings.LingerMs + 200);
            PluginLog.Info("Control server stopped (" + open.Count + " session(s) told: " + reason + ")");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Open sessions, oldest first.</summary>
        public List<ClientSession> Sessions
        {
            get { lock (sync) return sessions.Where(s => s.IsOpen).ToList(); }
        }

        public List<ClientSession> PairedSessions => Sessions.Where(s => s.State == SessionState.Paired).ToList();

        public ClientSession FindByTabletId(string tabletId)
        {
            return Sessions.FirstOrDefault(s => string.Equals(s.TabletId, tabletId, StringComparison.Ordinal));
        }

        /// <summary>Sends each Paired session its state when it differs from the last one it got (spec §6.6).</summary>
        public void BroadcastState()
        {
            foreach (var s in PairedSessions) SendState(s, false);
        }

        internal void SendState(ClientSession session, bool force)
        {
            var factory = StateFactory;
            if (factory == null || session.State != SessionState.Paired) return;
            StateMessage state;
            try
            {
                state = factory(session);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Building the state for " + session + " failed", ex);
                return;
            }
            if (state != null) session.SendStateIfChanged(state, force);
        }

        // Called by sessions

        internal WelcomeMessage BuildWelcome(int protocol, List<string> features)
        {
            var t = welcomeTemplate();
            return new WelcomeMessage
            {
                HostId = t.HostId,
                Name = t.Name,
                Version = t.Version,
                SimhubVersion = t.SimhubVersion,
                Protocol = protocol,
                Features = features,
            };
        }

        internal void OnHello(ClientSession session, HelloMessage hello)
        {
            List<ClientSession> older;
            lock (sync)
            {
                older = sessions.Where(s => s != session && s.IsOpen && string.Equals(s.TabletId, hello.TabletId, StringComparison.Ordinal)).ToList();
            }
            foreach (var old in older)
            {
                PluginLog.Info(old + " replaced by " + session);
                old.CloseWithError(ErrorCodes.Replaced, "a newer connection from the same tablet took over");
            }
        }

        internal void OnPairRequest(ClientSession session, PairRequestMessage request)
        {
            PairResultMessage result;
            try
            {
                result = (Pairing ?? new DenyAllPairing()).HandlePairRequest(session, request);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Pairing failed for " + session, ex);
                session.SendError(ErrorCodes.Internal, "pairing failed: " + ex.Message, MessageTypes.PairRequest);
                return;
            }
            if (result == null) return;
            if (!session.Send(result)) return;
            if (!result.Ok)
            {
                PluginLog.Info(session + ": pairRequest (" + request.Form + ") answered " + result.Reason);
                if (result.Reason != PairReasons.PinRequired && result.Reason != PairReasons.WrongPin) session.RestartPairRequestTimer();
                return;
            }
            session.MarkPaired();
            session.PairedOrder = Interlocked.Increment(ref orderCounter);
            PluginLog.Info(session + ": paired (" + request.Form + ")");
            SendState(session, true);
            Raise(SessionPaired, session);
            RaiseChanged();
        }

        internal void OnPairedMessage(ClientSession session, Message message)
        {
            switch (message)
            {
                case StatusMessage status:
                    var hadPhone = session.LastStatus != null && session.LastStatus.PhoneConnected;
                    session.LastStatus = status;
                    session.LastStatusAtMs = clock.NowMs;
                    if (status.PhoneConnected && !hadPhone) session.PhoneConnectedOrder = Interlocked.Increment(ref orderCounter);
                    else if (!status.PhoneConnected) session.PhoneConnectedOrder = 0;
                    var handler = StatusReceived;
                    if (handler != null)
                    {
                        try { handler(session, status); } catch (Exception ex) { PluginLog.Error("A status handler failed", ex); }
                    }
                    RaiseChanged();
                    break;
                case AudioStartMessage start:
                    var startHandler = AudioStartReceived;
                    if (startHandler != null)
                    {
                        try { startHandler(session, start); } catch (Exception ex) { PluginLog.Error("An audioStart handler failed", ex); }
                    }
                    break;
                case ArtworkMessage artwork:
                    session.LastArtwork = artwork;
                    PluginLog.Debug(session + ": artwork " + (artwork.Mime + ", " + artwork.Bytes.Length + " bytes"));
                    var artworkHandler = ArtworkReceived;
                    if (artworkHandler != null)
                    {
                        try { artworkHandler(session, artwork); } catch (Exception ex) { PluginLog.Error("An artwork handler failed", ex); }
                    }
                    RaiseChanged();
                    break;
                case AudioStopMessage stop:
                    var stopHandler = AudioStopReceived;
                    if (stopHandler != null)
                    {
                        try { stopHandler(session, stop); } catch (Exception ex) { PluginLog.Error("An audioStop handler failed", ex); }
                    }
                    break;
                case MicStartMessage micStart:
                    var micStartHandler = MicStartReceived;
                    if (micStartHandler != null)
                    {
                        try { micStartHandler(session, micStart); } catch (Exception ex) { PluginLog.Error("A micStart handler failed", ex); }
                    }
                    break;
                case MicStopMessage micStop:
                    var micStopHandler = MicStopReceived;
                    if (micStopHandler != null)
                    {
                        try { micStopHandler(session, micStop); } catch (Exception ex) { PluginLog.Error("A micStop handler failed", ex); }
                    }
                    break;
            }
        }

        internal void OnSessionChanged(ClientSession session)
        {
            RaiseChanged();
        }

        internal void OnSessionClosed(ClientSession session)
        {
            lock (sync) sessions.Remove(session);
            try { Pairing?.SessionClosed(session); } catch (Exception ex) { PluginLog.Error("Pairing cleanup failed", ex); }
            Raise(SessionClosed, session);
            RaiseChanged();
        }

        // Accepting

        private async Task AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    var l = listener;
                    if (l == null) return;
                    client = await l.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (running) PluginLog.Warn("Accepting a connection failed: " + ex.Message);
                    if (!running) return;
                    continue;
                }
                try
                {
                    Accept(client);
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Setting up a connection failed", ex);
                    try { client.Close(); } catch { }
                }
            }
        }

        private void Accept(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote == null || !NetUtil.IsAllowedRemote(remote.Address))
            {
                PluginLog.Warn("Refused a control connection from " + remote + ": not a LAN address");
                client.Close();
                return;
            }
            if (!running)
            {
                client.Close();
                return;
            }
            var session = new ClientSession(this, client, timings, clock);
            lock (sync) sessions.Add(session);
            PluginLog.Info(session + " accepted on " + session.Local);
            session.Start();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = SessionsChanged;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { PluginLog.Error("A sessions-changed handler failed", ex); }
        }

        private static void Raise(Action<ClientSession> handler, ClientSession session)
        {
            if (handler == null) return;
            try { handler(session); } catch (Exception ex) { PluginLog.Error("A session handler failed", ex); }
        }

        /// <summary>A sentence for the page explaining why the port could not be bound.</summary>
        public static string DescribeBindError(SocketException ex, int port)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.AddressAlreadyInUse:
                    return "TCP port " + port + " is already in use by another program. Close it or choose another control port.";
                case SocketError.AccessDenied:
                    return "Windows refused TCP port " + port + " (access denied). The port may be reserved by Windows; choose another control port.";
                default:
                    return "Could not listen on TCP port " + port + ": " + ex.Message;
            }
        }
    }
}
