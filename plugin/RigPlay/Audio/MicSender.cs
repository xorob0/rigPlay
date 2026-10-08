// SPDX-License-Identifier: GPL-3.0-only
// MicSender.cs: the PC microphone to the phone (#34, docs/protocol.md §6.13, §10.4). A tablet with feature mic sends
// micStart when the phone opens its microphone (Siri, a call); the plugin captures an input device (IMicCapture, the
// NAudio side in MicCapture.cs), cuts the mono s16 samples into 5 ms datagrams with the §10.2 header (streamType 4,
// seq and timestamp from 0 per micStart, start flag on the first) and sends them to the tablet's address and port.
// Every buffer goes through MicGainControl first (the boost from the settings, automatic by default), so the level
// meter shows what the phone gets. It stops on micStop from the owning session, on that session closing, after 2 s
// without a line from it, and when the user switches "Microphone to the phone" off. One stream at a time: a
// micStart from another tablet takes it over.
// MicGlue connects it to the host's events and to state.mic.enabled.
// Pure: no SimHub, WPF or NAudio types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Audio
{
    /// <summary>The capture side, implemented with NAudio WASAPI by MicCapture; tests use a fake.</summary>
    public interface IMicCapture : IDisposable
    {
        /// <summary>
        /// Opens <paramref name="deviceId"/> (empty: the Windows default recording device) and starts delivering mono s16
        /// samples at <paramref name="sampleRate"/> to <paramref name="onSamples"/> (array, count), on the capture thread.
        /// Returns null when capturing, otherwise why not (no device, device busy). Starting again restarts.
        /// </summary>
        string Start(string deviceId, int sampleRate, Action<short[], int> onSamples);

        /// <summary>Stops capturing; nothing is delivered after it returns. No effect when stopped.</summary>
        void Stop();

        /// <summary>True when <paramref name="deviceId"/> (or, empty, a default recording device) is present. Never throws.</summary>
        bool HasDevice(string deviceId);

        /// <summary>The name of the device being captured, or null.</summary>
        string DeviceName { get; }
    }

    /// <summary>
    /// Cuts mono s16 samples into datagrams of <see cref="MicSender.PacketMs"/> with the §10.2 header (streamType 4,
    /// pcm_s16le, 1 channel). seq and timestamp start at 0 and the first datagram carries the start flag after each
    /// <see cref="Reset"/>.
    /// </summary>
    public sealed class MicPacketizer
    {
        private readonly byte[] packet;
        private int filled;
        private bool first = true;

        public MicPacketizer(int sampleRate)
        {
            if (!AudioHeader.IsValidSampleRate(sampleRate)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            FramesPerPacket = sampleRate * MicSender.PacketMs / 1000;
            packet = new byte[AudioHeader.Size + 2 * FramesPerPacket];
        }

        public int SampleRate { get; }

        /// <summary>sampleRate / 200: 80 frames at 16 kHz, 120 at 24 kHz.</summary>
        public int FramesPerPacket { get; }

        /// <summary>Header plus payload of one datagram.</summary>
        public int PacketBytes => packet.Length;

        public ushort NextSeq { get; private set; }

        public uint NextTimestamp { get; private set; }

        /// <summary>Samples waiting for a complete datagram.</summary>
        public int Pending => filled;

        /// <summary>A new stream: seq and timestamp from 0, start flag on the next datagram, pending samples dropped.</summary>
        public void Reset()
        {
            filled = 0;
            first = true;
            NextSeq = 0;
            NextTimestamp = 0;
        }

        /// <summary>
        /// Appends <paramref name="count"/> samples and calls <paramref name="emit"/> (datagram, length) for every complete
        /// datagram. The buffer is reused: <paramref name="emit"/> must send or copy it before it returns.
        /// </summary>
        public void Write(short[] samples, int offset, int count, Action<byte[], int> emit)
        {
            if (samples == null) return;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                var s = samples[i];
                var at = AudioHeader.Size + 2 * filled;
                packet[at] = (byte)s;
                packet[at + 1] = (byte)(s >> 8);
                if (++filled < FramesPerPacket) continue;

                var header = AudioHeader.Create(NextSeq, AudioStreamType.Mic, first, NextTimestamp, SampleRate, 1);
                header.Write(packet, 0);
                first = false;
                NextSeq = unchecked((ushort)(NextSeq + 1));
                NextTimestamp = unchecked(NextTimestamp + (uint)FramesPerPacket);
                filled = 0;
                emit(packet, packet.Length);
            }
        }

        /// <summary>The peak of <paramref name="count"/> samples as a fraction of full scale (0..1).</summary>
        public static double Peak(short[] samples, int offset, int count)
        {
            var peak = 0;
            for (var i = offset; i < offset + count; i++)
            {
                var v = samples[i] == short.MinValue ? 32768 : Math.Abs((int)samples[i]);
                if (v > peak) peak = v;
            }
            return peak / 32768.0;
        }
    }

    /// <summary>What the page shows about the microphone.</summary>
    public sealed class MicStats
    {
        public static readonly MicStats Empty = new MicStats();

        /// <summary>"Microphone to the phone" is on.</summary>
        public bool Enabled;

        /// <summary>A tablet asked (micStart) and the stream has not stopped.</summary>
        public bool Started;

        /// <summary>Started and the capture runs (false without an input device).</summary>
        public bool Capturing;

        /// <summary>The tablet the stream belongs to, e.g. "Lenovo Tab P11".</summary>
        public string Tablet = "";

        /// <summary>Where datagrams go, e.g. "192.168.1.42:23713".</summary>
        public string Target = "";

        public int SampleRate;
        public long Packets;
        public double PacketsPerSecond;
        public long SendErrors;

        /// <summary>Peak level of the last 100 ms or so, after the boost, in dBFS (negative infinity for silence).</summary>
        public double LevelDb = double.NegativeInfinity;

        /// <summary>The boost in effect (dB): the fixed one, or what automatic mode has climbed to.</summary>
        public double GainDb;

        /// <summary>Automatic gain is on.</summary>
        public bool AutoGain;

        /// <summary>The input device being captured, or null.</summary>
        public string Device;

        /// <summary>Why the stream last stopped or failed, for the page.</summary>
        public string LastEvent = "";

        /// <summary>E.g. "Sending to Lenovo Tab P11 (192.168.1.42:23713) · 16 kHz · 200 pkt/s", "Idle", "Off".</summary>
        public string StateText
        {
            get
            {
                if (!Enabled) return "Off";
                if (!Started) return "Idle: no tablet is using the microphone";
                var inv = CultureInfo.InvariantCulture;
                var who = (Tablet.Length > 0 ? Tablet + " (" + Target + ")" : Target);
                if (!Capturing) return "Requested by " + who + ", but no input device is capturing";
                return "Sending to " + who + " · " + (SampleRate / 1000.0).ToString("0.#", inv) + " kHz · "
                    + PacketsPerSecond.ToString("0", inv) + " pkt/s";
            }
        }

        /// <summary>E.g. "-18 dBFS ▮▮▮▮▮▯▯▯▯▯ · boost +14 dB (automatic)", or "—" while not capturing.</summary>
        public string LevelText
        {
            get
            {
                if (!Capturing) return "—";
                return MicSender.FormatLevel(LevelDb) + " · boost " + MicSender.FormatGain(GainDb) + (AutoGain ? " (automatic)" : "");
            }
        }
    }

    /// <summary>Runs the microphone stream for one tablet at a time. Thread-safe.</summary>
    public sealed class MicSender : IDisposable
    {
        /// <summary>Datagram length (spec §10.4).</summary>
        public const int PacketMs = 5;

        /// <summary>The owning session is considered gone after this long without a line from it (spec §6.13).</summary>
        public const int SilenceTimeoutMs = 2000;

        /// <summary>How often the watchdog and the stats run.</summary>
        public const int TickMs = 250;

        // control serialises Start / Stop (and so every call into the capture); sync guards the stream state the capture
        // thread reads in OnSamples. control is taken before sync, and the capture is never started or stopped under sync.
        private readonly object control = new object();
        private readonly object sync = new object();
        private readonly Func<RigPlaySettings> settings;
        private readonly IMicCapture capture;
        private readonly IClock clock;
        private readonly Action<byte[], int, IPEndPoint> send;
        private readonly RateMeter rate = new RateMeter();
        private Socket socket;
        private Timer timer;
        private bool disposed;

        // The stream, under sync.
        private int ownerSessionId;
        private Func<long> ownerLastLine;
        private string ownerName = "";
        private IPEndPoint target;
        private MicPacketizer packetizer;
        private MicGainControl gainControl;
        private bool capturing;
        private long packets;
        private long sendErrors;
        private double peak;
        private double levelDb = double.NegativeInfinity;
        private string device;
        private string lastEvent = "";
        private int generation;
        private bool loggedNoDevice;
        private volatile MicStats stats = MicStats.Empty;

        /// <param name="settings">The live settings: MicEnabled and MicDeviceId are read on every use.</param>
        /// <param name="capture">The input device; null when NAudio is unavailable (every micStart then fails, logged once).</param>
        /// <param name="send">Sends one datagram; null for a UDP socket. Tests capture the datagrams.</param>
        public MicSender(Func<RigPlaySettings> settings, IMicCapture capture, IClock clock = null, Action<byte[], int, IPEndPoint> send = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.capture = capture;
            this.clock = clock ?? SystemClock.Instance;
            this.send = send ?? SendUdp;
            RefreshStats();
        }

        /// <summary>The page or state.mic changed (stream started or stopped, availability changed). Any thread.</summary>
        public event Action Changed;

        /// <summary>The latest snapshot for the page, refreshed every <see cref="TickMs"/>.</summary>
        public MicStats Stats => stats;

        /// <summary>
        /// The setting is on and an input device is present: state.mic.enabled (spec §6.6). The device check enumerates
        /// the recording devices, so it is cached for <see cref="DeviceCheckMs"/>; a settings change or a failed capture
        /// checks again.
        /// </summary>
        public bool Available
        {
            get
            {
                var s = settings();
                if (s == null || !s.MicEnabled || capture == null) return false;
                var now = clock.NowMs;
                lock (sync)
                {
                    if (deviceCheckedAt != long.MinValue && now - deviceCheckedAt < DeviceCheckMs && deviceCheckedId == s.MicDeviceId) return devicePresent;
                }
                var present = SafeHasDevice(s.MicDeviceId);
                lock (sync)
                {
                    deviceCheckedAt = now;
                    deviceCheckedId = s.MicDeviceId;
                    devicePresent = present;
                }
                return present;
            }
        }

        /// <summary>How long a device presence check is reused.</summary>
        public const int DeviceCheckMs = 2000;

        private long deviceCheckedAt = long.MinValue;
        private string deviceCheckedId;
        private bool devicePresent;

        private void InvalidateDeviceCheck()
        {
            lock (sync) deviceCheckedAt = long.MinValue;
        }

        /// <summary>Id of the session that owns the stream; 0 when none.</summary>
        public int OwnerSessionId
        {
            get { lock (sync) return ownerSessionId; }
        }

        /// <summary>Starts the watchdog timer (the plugin; tests call <see cref="Tick"/> themselves).</summary>
        public void StartTimer()
        {
            lock (sync)
            {
                if (disposed || timer != null) return;
                timer = new Timer(_ => SafeTick(), null, TickMs, TickMs);
            }
        }

        /// <summary>
        /// micStart from session <paramref name="sessionId"/> (spec §6.13): (re)starts the stream to <paramref name="target"/>
        /// at <paramref name="sampleRate"/>. False when the setting is off (ignored), the rate is invalid, or the capture
        /// could not start (the stream then stays started without datagrams, so a later device or micStop is handled).
        /// </summary>
        public bool Start(int sessionId, string tabletName, IPEndPoint target, int sampleRate, Func<long> lastLineAtMs)
        {
            lock (control) return StartControlled(sessionId, tabletName, target, sampleRate, lastLineAtMs);
        }

        private bool StartControlled(int sessionId, string tabletName, IPEndPoint target, int sampleRate, Func<long> lastLineAtMs)
        {
            var s = settings();
            if (s == null || !s.MicEnabled)
            {
                Log("micStart from " + Describe(tabletName, target) + " ignored: \"Microphone to the phone\" is off");
                RefreshStats();
                return false;
            }
            if (target == null || !AudioHeader.IsValidSampleRate(sampleRate))
            {
                Log("micStart from " + Describe(tabletName, target) + " ignored: invalid target or sample rate " + sampleRate);
                return false;
            }

            int gen;
            bool wasCapturing;
            lock (sync)
            {
                if (disposed) return false;
                if (ownerSessionId != 0 && ownerSessionId != sessionId) lastEvent = "Taken over by " + Describe(tabletName, target);
                wasCapturing = ReleaseCaptureLocked();
                gen = ++generation;
                ownerSessionId = sessionId;
                ownerLastLine = lastLineAtMs;
                ownerName = tabletName ?? "";
                this.target = target;
                packetizer = new MicPacketizer(sampleRate);
                gainControl = new MicGainControl(sampleRate) { Automatic = s.MicAutoBoost, BoostDb = s.MicBoostDb };
                // packets/sendErrors are incremented out of sync by OnSamples via Interlocked; a new stream never
                // overlaps the previous one's sends (the old generation stops emitting), so a plain store is enough,
                // but Interlocked.Exchange keeps the read in RefreshStats consistent across threads.
                Interlocked.Exchange(ref packets, 0);
                Interlocked.Exchange(ref sendErrors, 0);
                peak = 0;
                levelDb = double.NegativeInfinity;
                rate.Clear();
            }
            if (wasCapturing) StopCapture();

            string error;
            if (capture == null)
            {
                error = "the audio library (NAudio) is unavailable";
            }
            else
            {
                try
                {
                    error = capture.Start(s.MicDeviceId, sampleRate, (samples, count) => OnSamples(gen, samples, count));
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            var ok = error == null;
            if (!ok) InvalidateDeviceCheck();
            var logFailure = false;
            var name = ok ? SafeDeviceName() : null;
            lock (sync)
            {
                if (gen != generation) return false; // cannot happen while control is held; kept as a guard
                capturing = ok;
                device = name;
                lastEvent = ok ? "Started by " + Describe(tabletName, target) : "No input device: " + error;
                if (ok)
                {
                    loggedNoDevice = false;
                }
                else if (!loggedNoDevice)
                {
                    // Logged once until a capture succeeds: a tablet asks again on every Siri request.
                    loggedNoDevice = true;
                    logFailure = true;
                }
            }
            if (ok) Log("Microphone to " + Describe(tabletName, target) + " at " + sampleRate + " Hz from " + (name ?? "the default device"));
            else if (logFailure) Log("Microphone to " + Describe(tabletName, target) + " not started: " + error + " (logged once)");
            RefreshStats();
            RaiseChanged();
            return ok;
        }

        /// <summary>micStop or the close of session <paramref name="sessionId"/>: stops when it owns the stream.</summary>
        public void StopIfOwner(int sessionId, string reason)
        {
            lock (sync)
            {
                if (ownerSessionId == 0 || ownerSessionId != sessionId) return;
            }
            Stop(reason);
        }

        /// <summary>Stops the stream, whoever owns it. No effect without one.</summary>
        public void Stop(string reason)
        {
            lock (control) StopControlled(reason);
        }

        private void StopControlled(string reason)
        {
            string who;
            bool wasCapturing;
            lock (sync)
            {
                if (ownerSessionId == 0) return;
                who = Describe(ownerName, target);
                wasCapturing = ReleaseCaptureLocked();
                generation++;
                ownerSessionId = 0;
                ownerLastLine = null;
                packetizer = null;
                lastEvent = "Stopped: " + reason;
            }
            // Outside the lock: stopping waits for the capture thread, which may be waiting for the lock in OnSamples.
            if (wasCapturing) StopCapture();
            Log("Microphone to " + who + " stopped: " + reason);
            RefreshStats();
            RaiseChanged();
        }

        /// <summary>
        /// The page changed "Microphone to the phone" or the input device: off stops the stream; a new device restarts
        /// it on that device; state.mic.enabled is re-evaluated (Changed).
        /// </summary>
        public void SettingsChanged()
        {
            InvalidateDeviceCheck();
            var s = settings();
            int owner;
            string name;
            IPEndPoint to;
            int sampleRate;
            Func<long> lastLine;
            lock (sync)
            {
                owner = ownerSessionId;
                name = ownerName;
                to = target;
                sampleRate = packetizer?.SampleRate ?? 0;
                lastLine = ownerLastLine;
            }
            if (owner != 0 && (s == null || !s.MicEnabled)) Stop("\"Microphone to the phone\" switched off");
            else if (owner != 0 && sampleRate > 0) Start(owner, name, to, sampleRate, lastLine);
            RaiseChanged();
        }

        /// <summary>The watchdog: 2 s without a line from the owner, or the setting switched off, stops the stream.</summary>
        public void Tick()
        {
            Func<long> lastLine;
            lock (sync)
            {
                lastLine = ownerSessionId != 0 ? ownerLastLine : null;
            }
            if (lastLine != null)
            {
                var s = settings();
                long silentMs;
                try { silentMs = clock.NowMs - lastLine(); } catch (Exception) { silentMs = long.MaxValue; }
                if (s == null || !s.MicEnabled) Stop("\"Microphone to the phone\" switched off");
                else if (silentMs >= SilenceTimeoutMs) Stop("no heartbeat from the tablet for " + (SilenceTimeoutMs / 1000) + " s");
            }
            RefreshStats();
        }

        private void SafeTick()
        {
            try { Tick(); } catch (Exception ex) { Log("The microphone watchdog failed: " + ex.Message); }
        }

        /// <summary>Samples from the capture thread: boost, level, packets, send.</summary>
        internal void OnSamples(int gen, short[] samples, int count)
        {
            var s = settings();
            // The datagrams produced under sync, each a copy (the packetizer reuses its buffer), with the target to
            // reach; the actual send (a possibly blocking SendTo) happens after the lock so the capture thread and
            // every other caller of sync (stats, Start/Stop) are not held behind the socket.
            List<KeyValuePair<byte[], int>> outgoing = null;
            IPEndPoint to;
            lock (sync)
            {
                if (gen != generation || packetizer == null || target == null) return;
                double p;
                if (gainControl != null)
                {
                    if (s != null)
                    {
                        gainControl.Automatic = s.MicAutoBoost;
                        gainControl.BoostDb = s.MicBoostDb;
                    }
                    p = gainControl.Process(samples, 0, count);
                }
                else
                {
                    p = MicPacketizer.Peak(samples, 0, count);
                }
                if (p > peak) peak = p;
                to = target;
                packetizer.Write(samples, 0, count, (datagram, length) =>
                {
                    var copy = new byte[length];
                    Buffer.BlockCopy(datagram, 0, copy, 0, length);
                    (outgoing ?? (outgoing = new List<KeyValuePair<byte[], int>>())).Add(new KeyValuePair<byte[], int>(copy, length));
                });
            }

            if (outgoing == null) return;
            foreach (var datagram in outgoing)
            {
                try
                {
                    send(datagram.Key, datagram.Value, to);
                    Interlocked.Increment(ref packets);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref sendErrors);
                }
            }
        }

        /// <summary>Marks the capture stopped; true when the caller must call <see cref="StopCapture"/> after the lock.</summary>
        private bool ReleaseCaptureLocked()
        {
            if (!capturing) return false;
            capturing = false;
            device = null;
            return true;
        }

        private void StopCapture()
        {
            try { capture?.Stop(); } catch (Exception ex) { Log("Stopping the microphone capture failed: " + ex.Message); }
        }

        private void RefreshStats()
        {
            var s = settings();
            MicStats next;
            var packetsNow = Interlocked.Read(ref packets);
            var sendErrorsNow = Interlocked.Read(ref sendErrors);
            lock (sync)
            {
                rate.Add(clock.NowMs / 1000.0, packetsNow, 0);
                // A peak meter with a 20 dB/s fall, so speech reads as a steady level rather than flicker.
                var db = peak > 0 ? AudioMath.GainToDb(peak) : double.NegativeInfinity;
                var fallen = double.IsNegativeInfinity(levelDb) ? double.NegativeInfinity : levelDb - 20.0 * TickMs / 1000.0;
                levelDb = Math.Max(db, fallen < -90 ? double.NegativeInfinity : fallen);
                peak = 0;
                next = new MicStats
                {
                    Enabled = s != null && s.MicEnabled,
                    Started = ownerSessionId != 0,
                    Capturing = ownerSessionId != 0 && capturing,
                    Tablet = ownerSessionId != 0 ? ownerName : "",
                    Target = ownerSessionId != 0 && target != null ? target.ToString() : "",
                    SampleRate = packetizer?.SampleRate ?? 0,
                    Packets = packetsNow,
                    PacketsPerSecond = ownerSessionId != 0 ? rate.PacketsPerSecond : 0,
                    SendErrors = sendErrorsNow,
                    LevelDb = levelDb,
                    GainDb = gainControl != null ? gainControl.GainDb : (s != null ? (double)s.MicBoostDb : 0),
                    AutoGain = s != null && s.MicAutoBoost,
                    Device = device,
                    LastEvent = lastEvent,
                };
            }
            stats = next;
        }

        /// <summary>E.g. "+14 dB", "0 dB".</summary>
        public static string FormatGain(double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db)) return "0 dB";
            var rounded = (int)Math.Round(db);
            return (rounded > 0 ? "+" : "") + rounded.ToString(CultureInfo.InvariantCulture) + " dB";
        }

        /// <summary>E.g. "-18 dBFS ▮▮▮▮▮▮▯▯▯▯": ten steps from -60 to 0 dBFS.</summary>
        public static string FormatLevel(double db)
        {
            if (double.IsNaN(db) || double.IsNegativeInfinity(db) || db < -90) return "silence ▯▯▯▯▯▯▯▯▯▯";
            var steps = (int)Math.Round((Math.Min(0, db) + 60) / 6.0);
            steps = Math.Max(0, Math.Min(10, steps));
            return Math.Round(db).ToString("0", CultureInfo.InvariantCulture) + " dBFS " + new string('▮', steps) + new string('▯', 10 - steps);
        }

        private void SendUdp(byte[] datagram, int length, IPEndPoint to)
        {
            var s = socket;
            if (s == null)
            {
                lock (sync)
                {
                    if (socket == null) socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s = socket;
                }
            }
            s.SendTo(datagram, 0, length, SocketFlags.None, to);
        }

        private bool SafeHasDevice(string id)
        {
            try { return capture.HasDevice(id); } catch (Exception) { return false; }
        }

        private string SafeDeviceName()
        {
            try { return capture?.DeviceName; } catch (Exception) { return null; }
        }

        private static string Describe(string tabletName, IPEndPoint target)
        {
            var name = string.IsNullOrEmpty(tabletName) ? "a tablet" : tabletName;
            return target == null ? name : name + " (" + target + ")";
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { Log("A microphone change handler failed: " + ex.Message); }
        }

        private static void Log(string message)
        {
            PluginLog.Info(message);
        }

        public void Dispose()
        {
            Timer t;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                t = timer;
                timer = null;
            }
            t?.Dispose();
            Stop("the plugin is stopping");
            try { capture?.Dispose(); } catch (Exception) { }
            lock (sync)
            {
                try { socket?.Dispose(); } catch (Exception) { }
                socket = null;
            }
        }
    }

    /// <summary>
    /// Connects a <see cref="MicSender"/> to the host (spec §6.13): micStart / micStop of Paired sessions with feature mic,
    /// the close of the owning session, and state.mic.enabled, pushed to tablets whenever it changes.
    /// </summary>
    public sealed class MicGlue : IDisposable
    {
        private readonly RigPlayHost host;
        private readonly MicSender sender;
        private bool attached;
        private bool lastAvailable;

        public MicGlue(RigPlayHost host, MicSender sender)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
            lastAvailable = sender.Available;
            host.MicEnabled = () => lastAvailable;
            host.MicStart += OnMicStart;
            host.MicStop += OnMicStop;
            host.PairedSessionClosed += OnSessionClosed;
            sender.Changed += Refresh;
            attached = true;
            host.PushState();
        }

        /// <summary>The setting or the devices changed: re-evaluates state.mic.enabled and pushes it when it changed.</summary>
        public void Refresh()
        {
            if (!attached) return;
            var now = sender.Available;
            if (now == lastAvailable) return;
            lastAvailable = now;
            PluginLog.Info("PC microphone " + (now ? "available" : "unavailable") + " to tablets (state.mic.enabled " + (now ? "true" : "false") + ")");
            host.PushState();
        }

        private void OnMicStart(ClientSession session, MicStartMessage message)
        {
            var target = new IPEndPoint(session.Remote.Address, message.Port);
            sender.Start(session.Id, session.TabletName, target, message.SampleRate, () => session.LastLineAtMs);
            Refresh();
        }

        private void OnMicStop(ClientSession session)
        {
            sender.StopIfOwner(session.Id, "micStop from the tablet");
        }

        private void OnSessionClosed(ClientSession session)
        {
            sender.StopIfOwner(session.Id, "the tablet disconnected (" + (session.CloseReason ?? "closed") + ")");
        }

        public void Dispose()
        {
            if (!attached) return;
            attached = false;
            host.MicStart -= OnMicStart;
            host.MicStop -= OnMicStop;
            host.PairedSessionClosed -= OnSessionClosed;
            sender.Changed -= Refresh;
            host.MicEnabled = () => false;
        }
    }
}
