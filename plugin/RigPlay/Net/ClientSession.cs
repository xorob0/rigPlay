// SPDX-License-Identifier: GPL-3.0-only
// ClientSession.cs: one tablet's TCP control connection, from accept to close (spec §5.2): reads newline-delimited
// JSON, runs the AwaitingHello → Unpaired → Paired state machine, answers hello with welcome, sends a heartbeat every
// second, and closes on 5 s of silence, EOF, a socket error or a fatal error. What a message means for the rest of
// the plugin (pairing, status, audio) is decided by the ControlServer that owns the session.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Net
{
    public enum SessionState
    {
        AwaitingHello,
        Unpaired,
        Paired,
        Closed,
    }

    /// <summary>The timeouts of spec §9. Tests shorten them.</summary>
    public sealed class SessionTimings
    {
        public int HeartbeatMs { get; set; } = ProtocolDefaults.HeartbeatIntervalMs;
        public int WatchdogMs { get; set; } = ProtocolDefaults.WatchdogMs;
        public int HelloTimeoutMs { get; set; } = ProtocolDefaults.HelloTimeoutMs;
        public int PairRequestTimeoutMs { get; set; } = ProtocolDefaults.PairRequestTimeoutMs;

        /// <summary>Minimum gap between two non-fatal errors sent on one session (spec §14.2).</summary>
        public int ErrorIntervalMs { get; set; } = 1000;

        /// <summary>How often the session's timer checks the timeouts.</summary>
        public int TickMs { get; set; } = 100;

        /// <summary>After a fatal error, how long the session drains input waiting for the tablet to close.</summary>
        public int LingerMs { get; set; } = 300;

        /// <summary>Socket send timeout, so a stalled tablet cannot block a broadcast forever.</summary>
        public int SendTimeoutMs { get; set; } = 3000;

        public static SessionTimings Default => new SessionTimings();
    }

    public sealed class ClientSession : IDisposable
    {
        private static int nextId;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly ControlServer server;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly SessionTimings timings;
        private readonly IClock clock;
        private readonly object stateLock = new object();
        private readonly object writeLock = new object();
        private readonly LineFramer framer = new LineFramer();
        private Timer timer;
        private int closed;
        private int closing;
        private long closeDeadline;
        private string pendingCloseReason;

        private long acceptedAt;
        private long welcomeAt;
        private long lastLineAt;
        private long lastHeartbeatSentAt;
        private long lastErrorSentAt = long.MinValue / 2;
        private long heartbeatSeq;
        private bool pairRequestSeen;
        private string lastStateSent;

        internal ClientSession(ControlServer server, TcpClient client, SessionTimings timings, IClock clock)
        {
            this.server = server;
            this.client = client;
            this.timings = timings;
            this.clock = clock;
            Id = Interlocked.Increment(ref nextId);
            client.NoDelay = true;
            client.SendTimeout = timings.SendTimeoutMs;
            stream = client.GetStream();
            Remote = Normalize(client.Client.RemoteEndPoint as IPEndPoint);
            Local = Normalize(client.Client.LocalEndPoint as IPEndPoint);
            acceptedAt = clock.NowMs;
            lastLineAt = acceptedAt;
        }

        /// <summary>Process-wide session number, for logs and the page.</summary>
        public int Id { get; }

        /// <summary>The tablet's end of the connection (IPv4).</summary>
        public IPEndPoint Remote { get; }

        /// <summary>The PC's end of this connection: the address the tablet reached the PC on (spec §11).</summary>
        public IPEndPoint Local { get; }

        public SessionState State { get; private set; } = SessionState.AwaitingHello;

        public HelloMessage Hello { get; private set; }

        public string TabletId => Hello?.TabletId;

        public string TabletName => Hello?.Name;

        /// <summary>The negotiated protocol version; 0 before welcome.</summary>
        public int Protocol { get; private set; }

        /// <summary>Features in effect for this session (both sides named them).</summary>
        public IReadOnlyList<string> Features { get; private set; } = new string[0];

        /// <summary>The tablet's last valid status, or null.</summary>
        public StatusMessage LastStatus { get; internal set; }

        /// <summary>The tablet's last artwork (spec §6.14), or null before the first.</summary>
        public ArtworkMessage LastArtwork { get; internal set; }

        /// <summary>Monotonic time the last status arrived.</summary>
        public long LastStatusAtMs { get; internal set; }

        /// <summary>Order in which sessions paired; larger is more recent. Set by the server.</summary>
        public long PairedOrder { get; internal set; }

        /// <summary>Order in which sessions' phoneConnected became true; 0 when no phone. Set by the server.</summary>
        public long PhoneConnectedOrder { get; internal set; }

        /// <summary>Usable: not closed and not closing after a fatal error.</summary>
        public bool IsOpen => Volatile.Read(ref closed) == 0 && Volatile.Read(ref closing) == 0;

        /// <summary>The socket is still open (possibly draining after a fatal error).</summary>
        internal bool IsAlive => Volatile.Read(ref closed) == 0;

        /// <summary>Why the session closed, once it has.</summary>
        public string CloseReason { get; private set; }

        /// <summary>Monotonic time the last line (any line, an empty one included) arrived from the tablet.</summary>
        public long LastLineAtMs => Interlocked.Read(ref lastLineAt);

        public bool HasFeature(string feature)
        {
            return Features.Contains(feature);
        }

        public override string ToString()
        {
            return "session " + Id + " (" + Remote + (TabletName != null ? ", " + TabletName : "") + ")";
        }

        internal void Start()
        {
            timer = new Timer(_ => Tick(), null, timings.TickMs, timings.TickMs);
            Task.Run(ReadLoop);
        }

        /// <summary>Sends one message. False when the session is closed or the write failed (the session then closes).</summary>
        public bool Send(Message message)
        {
            if (!IsOpen) return false;
            return SendRaw(message);
        }

        /// <summary>
        /// Sends an already-encoded message line (without the trailing newline), so a broadcaster can encode once and
        /// send the same line to many sessions. False when the session is closed or the write failed (it then closes);
        /// it shares writeLock and the fail → Close semantics with <see cref="Send"/>.
        /// </summary>
        internal bool SendEncoded(string line)
        {
            if (!IsOpen) return false;
            return SendRawLine(line);
        }

        private bool SendRaw(Message message)
        {
            if (!IsAlive) return false;
            return SendRawLine(MessageCodec.Encode(message));
        }

        private bool SendRawLine(string line)
        {
            if (!IsAlive) return false;
            var bytes = Utf8.GetBytes(line + "\n");
            try
            {
                lock (writeLock)
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                Close("write failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Sends a state unless it is identical to the last one this session got.</summary>
        internal bool SendStateIfChanged(StateMessage state, bool force)
        {
            var encoded = MessageCodec.Encode(state);
            lock (stateLock)
            {
                if (!force && encoded == lastStateSent) return false;
                lastStateSent = encoded;
            }
            return Send(state);
        }

        /// <summary>
        /// Sends a fatal error and closes (spec §14.2) without losing the line: write it, shut down the sending side
        /// (FIN), keep reading and discarding until the tablet closes or <see cref="SessionTimings.LingerMs"/> pass, then
        /// close. Closing with unread input pending would make the OS send RST, and the tablet would drop the line.
        /// </summary>
        public void CloseWithError(string code, string message)
        {
            CloseWithError(ErrorMessage.Of(code, message, fatal: true));
        }

        /// <summary>As <see cref="CloseWithError(string, string)"/> with a prepared error; it is sent with fatal: true.</summary>
        public void CloseWithError(ErrorMessage error)
        {
            if (!IsOpen || Interlocked.Exchange(ref closing, 1) != 0) return;
            error.Fatal = true;
            var reason = error.Code + (string.IsNullOrEmpty(error.Message) ? "" : ": " + error.Message);
            lock (stateLock)
            {
                pendingCloseReason = reason;
                closeDeadline = clock.NowMs + timings.LingerMs;
            }
            if (!SendRaw(error)) return;
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
                Close(reason);
                return;
            }
            server.OnSessionChanged(this);
        }

        /// <summary>Waits up to <paramref name="ms"/> for the session to finish closing.</summary>
        internal bool WaitClosed(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (IsAlive && DateTime.UtcNow < until) Thread.Sleep(10);
            return !IsAlive;
        }

        /// <summary>Closes the connection. Idempotent.</summary>
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            Interlocked.Exchange(ref closing, 1);
            lock (stateLock)
            {
                State = SessionState.Closed;
                CloseReason = reason;
            }
            try { timer?.Dispose(); } catch { }
            try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { client.Close(); } catch { }
            PluginLog.Info(this + " closed: " + reason);
            server.OnSessionClosed(this);
        }

        public void Dispose()
        {
            Close("disposed");
        }

        // Reading

        private async Task ReadLoop()
        {
            var buffer = new byte[8192];
            try
            {
                while (IsAlive)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        Close(Volatile.Read(ref closing) != 0 ? PendingCloseReason : "connection closed by the tablet");
                        return;
                    }
                    // After a fatal error the session only drains what the tablet still sends.
                    if (Volatile.Read(ref closing) != 0) continue;
                    List<string> lines;
                    try
                    {
                        lines = framer.Feed(buffer, 0, read);
                    }
                    catch (LineTooLongException)
                    {
                        CloseWithError(ErrorCodes.LineTooLong, "line exceeds " + ProtocolDefaults.MaxLineBytes + " bytes");
                        continue;
                    }
                    foreach (var line in lines)
                    {
                        if (!IsOpen) break;
                        HandleLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsAlive) Close((Volatile.Read(ref closing) != 0 ? PendingCloseReason + "; " : "") + "read failed: " + ex.Message);
            }
        }

        private string PendingCloseReason
        {
            get { lock (stateLock) return pendingCloseReason ?? "closed"; }
        }

        private void HandleLine(string line)
        {
            Interlocked.Exchange(ref lastLineAt, clock.NowMs);
            if (line.Length == 0) return;

            try
            {
                if (State == SessionState.AwaitingHello) HandleFirstLine(line);
                else HandleMessageLine(line);
            }
            catch (Exception ex)
            {
                PluginLog.Error(this + ": handling a line failed", ex);
                SendError(ErrorCodes.Internal, ex.GetType().Name + ": " + ex.Message, null);
            }
        }

        private void HandleFirstLine(string line)
        {
            var result = MessageCodec.TryDecode(line);
            var hello = result.Message as HelloMessage;
            if (hello == null)
            {
                PluginLog.Info(this + ": first line is not a valid hello (" + (result.Ok ? result.Type : result.Reason) + ")");
                CloseWithError(ErrorCodes.HelloRequired, "the first line must be a valid hello");
                return;
            }

            // Version negotiation (spec §7.1).
            var v = Math.Min(hello.Protocol, ProtocolDefaults.ProtocolVersion);
            if (v < Math.Max(hello.EffectiveMinProtocol, ProtocolDefaults.MinProtocolVersion))
            {
                PluginLog.Info(this + ": no common protocol version with " + hello.Name + " (tablet " + hello.EffectiveMinProtocol + "-" + hello.Protocol + ")");
                CloseWithError(new ErrorMessage
                {
                    Code = ErrorCodes.UnsupportedProtocol,
                    Message = "plugin speaks " + ProtocolDefaults.MinProtocolVersion + "-" + ProtocolDefaults.ProtocolVersion
                        + ", tablet " + hello.EffectiveMinProtocol + "-" + hello.Protocol,
                    Fatal = true,
                    MinProtocol = ProtocolDefaults.MinProtocolVersion,
                    MaxProtocol = ProtocolDefaults.ProtocolVersion,
                });
                return;
            }

            var offered = server.PluginFeatures;
            var features = offered.Where(f => hello.EffectiveFeatures.Contains(f)).ToList();

            // A newer session with the same tabletId replaces an older one (spec §12) before this one is welcomed.
            server.OnHello(this, hello);

            var now = clock.NowMs;
            lock (stateLock)
            {
                if (State != SessionState.AwaitingHello) return;
                Hello = hello;
                Protocol = v;
                Features = features;
                State = SessionState.Unpaired;
                welcomeAt = now;
                lastHeartbeatSentAt = now;
            }
            PluginLog.Info(this + ": hello from tablet " + hello.TabletId + " app " + hello.AppVersion + ", protocol " + v
                + (features.Count > 0 ? ", features " + string.Join(",", features) : ""));
            Send(server.BuildWelcome(v, features));
            server.OnSessionChanged(this);
        }

        private void HandleMessageLine(string line)
        {
            var result = MessageCodec.TryDecode(line);
            switch (result.Failure)
            {
                case DecodeFailure.Malformed:
                    PluginLog.Info(this + ": ignored a malformed line (" + result.Reason + ")");
                    SendError(ErrorCodes.BadMessage, result.Reason, null);
                    return;
                case DecodeFailure.UnknownType:
                    PluginLog.Debug(this + ": ignored unknown message type " + result.Type);
                    return;
                case DecodeFailure.Invalid:
                    PluginLog.Info(this + ": ignored an invalid " + result.Type + " (" + result.Reason + ")");
                    // Never answer an error with an error (spec §14.2).
                    if (result.Type != MessageTypes.Error) SendError(ErrorCodes.BadMessage, result.Reason, result.Type);
                    return;
            }

            var message = result.Message;
            var error = message as ErrorMessage;
            if (error != null)
            {
                PluginLog.Info(this + ": tablet reported error " + error.Code + (error.Message != null ? " (" + error.Message + ")" : "")
                    + (error.IsFatal ? ", fatal" : ""));
                if (error.IsFatal) Close("tablet sent fatal error " + error.Code);
                return;
            }

            if (!MessageTypes.TabletToPlugin.Contains(message.Type) || message is HelloMessage)
            {
                SendError(ErrorCodes.UnexpectedMessage, message.Type + " is not expected from a tablet now", message.Type);
                return;
            }
            if (message is HeartbeatMessage) return;

            if (State == SessionState.Unpaired)
            {
                var request = message as PairRequestMessage;
                if (request == null)
                {
                    SendError(ErrorCodes.NotPaired, message.Type + " requires a paired session", message.Type);
                    return;
                }
                lock (stateLock) pairRequestSeen = true;
                server.OnPairRequest(this, request);
                return;
            }

            if (message is PairRequestMessage)
            {
                SendError(ErrorCodes.UnexpectedMessage, "already paired", message.Type);
                return;
            }
            if (MessageTypes.MicFeature.Contains(message.Type) && !HasFeature(global::RigPlayPlugin.Protocol.Features.Mic))
            {
                // micStart / micStop need feature mic (spec §6.13).
                SendError(ErrorCodes.UnexpectedMessage, message.Type + " requires feature mic", message.Type);
                return;
            }
            server.OnPairedMessage(this, message);
        }

        /// <summary>
        /// After a refusal that leaves no PIN pending (tokenInvalid, denied, pinExpired, tooManyAttempts) the session
        /// is back to waiting for the user: the 10 s pairRequest timeout starts again (spec §9).
        /// </summary>
        internal void RestartPairRequestTimer()
        {
            lock (stateLock)
            {
                if (State != SessionState.Unpaired) return;
                pairRequestSeen = false;
                welcomeAt = clock.NowMs;
            }
        }

        /// <summary>Called by the server when pairing succeeded, after the pairResult went out.</summary>
        internal void MarkPaired()
        {
            lock (stateLock)
            {
                if (State == SessionState.Unpaired) State = SessionState.Paired;
            }
        }

        /// <summary>A non-fatal error, at most one per ErrorIntervalMs (spec §14.2); extra ones are dropped.</summary>
        internal void SendError(string code, string message, string refType)
        {
            var now = clock.NowMs;
            lock (stateLock)
            {
                if (now - lastErrorSentAt < timings.ErrorIntervalMs) return;
                lastErrorSentAt = now;
            }
            Send(ErrorMessage.Of(code, message, false, refType));
        }

        // Timers

        private void Tick()
        {
            if (!IsAlive) return;
            try
            {
                var now = clock.NowMs;
                if (Volatile.Read(ref closing) != 0)
                {
                    long deadline;
                    lock (stateLock) deadline = closeDeadline;
                    if (now >= deadline) Close(PendingCloseReason);
                    return;
                }
                // Capture the published state under a short lock, then decide the timeouts outside it: never do I/O
                // or call Close() while holding stateLock (Close() takes it, and server handlers run outside it).
                SessionState state;
                long welcomeAtNow;
                bool pairRequestSeenNow;
                lock (stateLock)
                {
                    state = State;
                    welcomeAtNow = welcomeAt;
                    pairRequestSeenNow = pairRequestSeen;
                }
                if (state == SessionState.AwaitingHello)
                {
                    if (now - acceptedAt >= timings.HelloTimeoutMs) Close("no hello within " + timings.HelloTimeoutMs + " ms");
                    return;
                }
                if (now - Interlocked.Read(ref lastLineAt) >= timings.WatchdogMs)
                {
                    Close("link lost: no line for " + timings.WatchdogMs + " ms");
                    return;
                }
                if (state == SessionState.Unpaired && !pairRequestSeenNow && now - welcomeAtNow >= timings.PairRequestTimeoutMs)
                {
                    Close("no pairRequest within " + timings.PairRequestTimeoutMs + " ms of welcome");
                    return;
                }
                if (now - lastHeartbeatSentAt >= timings.HeartbeatMs)
                {
                    lastHeartbeatSentAt = now;
                    var seq = heartbeatSeq;
                    heartbeatSeq = seq >= 4294967295L ? 0 : seq + 1;
                    Send(new HeartbeatMessage { Seq = seq });
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(this + ": timer failed", ex);
            }
        }

        private static IPEndPoint Normalize(IPEndPoint endPoint)
        {
            return endPoint == null ? null : new IPEndPoint(NetUtil.Normalize(endPoint.Address), endPoint.Port);
        }
    }
}
