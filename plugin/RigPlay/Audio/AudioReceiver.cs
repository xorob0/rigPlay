// SPDX-License-Identifier: GPL-3.0-only
// AudioReceiver.cs: receives the tablet's audio datagrams (docs/protocol.md §10) on the audio UDP port, on a
// dedicated thread, and keeps one AudioStream (format + JitterBuffer) per stream type. Streams are started and
// stopped by the control channel through OnAudioStart / OnAudioStop / OnSourceLost / OnLinkLost. A stream
// started by audioStart belongs to the tablet IP that sent it: only that source's datagrams feed it, and only
// that source's audioStop or session loss stops it. Without a control channel (tests, AutoStartOnFirstFlag) a
// datagram with the start flag starts its stream on its own, with no owner. A stream joins the output mix
// (IAudioSink) when its datagrams arrive and leaves it on audioStop, link loss or 2 s without datagrams. Every 500 ms the receiver publishes an AudioStats snapshot.
// An opus stream (§10.4) carries an OpusStreamDecoder: each packet is decoded on the receive thread and the PCM goes
// into the same jitter buffer; opus is accepted only while OpusEnabled is on and Concentus is present.
// No NAudio, SimHub or WPF types here (compiled into RigPlay.Tests); AudioOutput.cs is the NAudio side.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RigPlayPlugin.Audio
{
    /// <summary>Where the receiver's log lines go; AudioPipeline points these at SimHub's log.</summary>
    public static class AudioLog
    {
        public static Action<string> Info = message => { };
        public static Action<string> Warn = message => { };
    }

    /// <summary>The output side of the receiver: a stream joins or leaves the mix.</summary>
    public interface IAudioSink
    {
        /// <summary>Datagrams are arriving for <paramref name="stream"/>: pull its PCM from <see cref="AudioStream.Buffer"/>.</summary>
        void StreamActivated(AudioStream stream);

        /// <summary>Stop pulling from <paramref name="stream"/> (stopped, link lost, or idle for 2 s).</summary>
        void StreamDeactivated(AudioStream stream);
    }

    /// <summary>One started stream: its announced format and its play-out buffer. A format change makes a new one.</summary>
    public sealed class AudioStream
    {
        internal AudioStream(AudioStreamType type, int sampleRate, int channels, AudioFormat format, bool autoStarted, int targetMs, int maxTargetMs, int skipSlackMs, IPAddress source = null)
        {
            Source = source;
            Type = type;
            SampleRate = sampleRate;
            Channels = channels;
            Format = format;
            AutoStarted = autoStarted;
            Buffer = new JitterBuffer(sampleRate, channels, targetMs, maxTargetMs, skipSlackMs);
            if (format == AudioFormat.Opus)
            {
                // Throws when Concentus is missing or the rate is not an Opus rate; OnAudioStart checks both first.
                Decoder = OpusStreamDecoder.Create(sampleRate, channels);
                DecodedPcm = new byte[Decoder.MaxPcmBytes];
            }
        }

        public AudioStreamType Type { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public AudioFormat Format { get; }
        public bool AutoStarted { get; }
        public JitterBuffer Buffer { get; }

        /// <summary>The tablet IP whose audioStart started this stream; null for a stream started by its first datagram.</summary>
        public IPAddress Source { get; }

        /// <summary>The stream's Opus decoder; null for pcm_s16le.</summary>
        public OpusStreamDecoder Decoder { get; }

        /// <summary>Scratch for one decoded Opus packet (receive thread only).</summary>
        internal readonly byte[] DecodedPcm;

        /// <summary>Opus packets the decoder refused (counted as invalid datagrams too).</summary>
        internal long DecodeFailures;

        /// <summary>In the output mix.</summary>
        public bool Active { get; internal set; }

        internal long LastPacketMs;
        internal readonly RateMeter Meter = new RateMeter();

        internal bool Matches(AudioHeader header)
        {
            return header.SampleRate == SampleRate && header.Channels == Channels && header.Format == Format;
        }

        public override string ToString()
        {
            return AudioHeader.StreamName(Type) + " " + AudioStats.DescribeFormat(SampleRate, Channels, Format);
        }
    }

    public sealed class AudioReceiver : IDisposable
    {
        /// <summary>A stream with no datagram for this long leaves the mix and its buffer is flushed.</summary>
        public const int IdleTimeoutMs = 2000;

        public const int StatsIntervalMs = 500;

        /// <summary>
        /// Caps on the learned buffer depth per stream (ms), below the receiver's own cap. Music tolerates any delay;
        /// Siri's answers may come a little later; a call with more than a second of delay stops being a conversation,
        /// so telephony trades the longest stalls for latency it can live with.
        /// </summary>
        public const int AltMaxTargetMs = 1500;
        public const int TelephonyMaxTargetMs = 1000;

        private static readonly AudioStreamType[] StreamTypes = { AudioStreamType.Media, AudioStreamType.Alt, AudioStreamType.Telephony };

        private readonly object gate = new object();
        private readonly Dictionary<AudioStreamType, AudioStream> streams = new Dictionary<AudioStreamType, AudioStream>();
        // The jitter buffer target (ms) a stopped stream of each type had grown to: a restart on the same network starts there.
        private readonly Dictionary<AudioStreamType, double> learnedTargetMs = new Dictionary<AudioStreamType, double>();
        private readonly IAudioSink sink;
        private readonly Func<long> clockMs;
        private int targetMs;
        private readonly int maxTargetMs;
        private readonly int skipSlackMs;
        // The highest target any stream learned (ms), reported once through TargetLearned and seeded from the saved value.
        private double learnedMaxMs;
        private readonly RateMeter datagramMeter = new RateMeter();
        private readonly HashSet<string> loggedOnce = new HashSet<string>();

        private UdpClient udp;
        private Thread thread;
        private Timer timer;
        private volatile bool running;
        private int ticking;
        private volatile bool opusEnabled;

        private long datagrams;
        private long invalid;
        private long rejected;
        private volatile string lastError = "";
        private volatile AudioStats stats = AudioStats.Empty;

        /// <param name="targetMs">The depth every stream starts with (the settings' minimum buffer).</param>
        /// <param name="maxTargetMs">The most a stream's depth may learn to; per-stream caps apply below it.</param>
        public AudioReceiver(int port, IAudioSink sink, int targetMs = JitterBuffer.DefaultTargetMs, int maxTargetMs = JitterBuffer.DefaultMaxTargetMs)
            : this(port, sink, targetMs, maxTargetMs, JitterBuffer.DefaultSkipSlackMs, null)
        {
        }

        internal AudioReceiver(int port, IAudioSink sink, int targetMs, int maxTargetMs, int skipSlackMs, Func<long> clockMs)
        {
            Port = port;
            this.sink = sink;
            this.targetMs = targetMs;
            this.maxTargetMs = maxTargetMs;
            this.skipSlackMs = skipSlackMs;
            this.clockMs = clockMs ?? StopwatchMs;
        }

        /// <summary>The cap on the learned depth of <paramref name="type"/>, under <paramref name="receiverCap"/>.</summary>
        public static int MaxTargetMsFor(AudioStreamType type, int receiverCap)
        {
            switch (type)
            {
                case AudioStreamType.Alt: return Math.Min(receiverCap, AltMaxTargetMs);
                case AudioStreamType.Telephony: return Math.Min(receiverCap, TelephonyMaxTargetMs);
                default: return receiverCap;
            }
        }

        /// <summary>
        /// Raised (on the stats thread) when a stream learned a deeper buffer than any before: the value in ms, for
        /// the settings to keep so the next SimHub start does not have to learn it again through dropouts.
        /// </summary>
        public event Action<double> TargetLearned;

        /// <summary>The highest depth learned so far (ms), or the seeded value; 0 when none.</summary>
        public double LearnedTargetMs
        {
            get { lock (gate) return learnedMaxMs; }
        }

        /// <summary>
        /// Seeds the learned depth (ms) from the last run: every stream of every type starts from it, each under its cap.
        /// A value at or below what is known changes nothing.
        /// </summary>
        public void InheritLearnedTarget(double ms)
        {
            if (ms <= 0 || double.IsNaN(ms) || double.IsInfinity(ms)) return;
            lock (gate)
            {
                if (ms <= learnedMaxMs) return;
                learnedMaxMs = ms;
                foreach (var type in StreamTypes) learnedTargetMs[type] = ms;
                foreach (var stream in streams.Values) stream.Buffer.InheritTarget(ms);
            }
        }

        /// <summary>The settings' minimum depth changed (ms): streams started from now on begin there.</summary>
        public void SetTargetMs(int ms)
        {
            if (ms < 0) return;
            lock (gate) targetMs = ms;
        }

        /// <summary>
        /// Forgets what was learned: the streams playing go back to the configured depth (trimming what they hold above
        /// it) and the ones started from now on begin there.
        /// </summary>
        public void ForgetLearnedTarget()
        {
            lock (gate)
            {
                learnedMaxMs = 0;
                learnedTargetMs.Clear();
                foreach (var stream in streams.Values) stream.Buffer.ResetTarget(targetMs);
            }
        }

        /// <summary>The configured audio port (0 binds an ephemeral port, for tests).</summary>
        public int Port { get; private set; }

        /// <summary>The port actually bound, or 0 when not listening.</summary>
        public int BoundPort { get; private set; }

        public bool Listening
        {
            get { return running && BoundPort != 0; }
        }

        /// <summary>
        /// Fallback for a receiver without a control channel: a datagram with the start flag for a stream that was not
        /// started starts it with the datagram's format. The plugin turns this off (AudioGlue): streams then start only
        /// through <see cref="OnAudioStart(AudioStreamType,int,int,AudioFormat,IPAddress)"/>.
        /// </summary>
        public bool AutoStartOnFirstFlag { get; set; } = true;

        /// <summary>
        /// Optional source check (§10.1: only the remote IP of a Paired session). Null accepts every source; the
        /// control server sets it once sessions exist. Called on the receive thread.
        /// </summary>
        public Func<IPAddress, bool> SourceFilter { get; set; }

        /// <summary>Supplies the output line of the stats (device and state); set by AudioPipeline.</summary>
        public Func<string> OutputStatus { get; set; }

        /// <summary>
        /// The Opus setting (§10.4): while true, and Concentus is present, opus is listed first in
        /// <see cref="SupportedFormats"/> and audioStart may name it. Off by default: PCM is the protocol's default. Turning
        /// it off does not stop an opus stream that is already playing; the tablet restarts it in PCM after the next state.
        /// </summary>
        public bool OpusEnabled
        {
            get { return opusEnabled; }
            set { opusEnabled = value; }
        }

        /// <summary>True while this receiver takes an opus audioStart: the setting is on and the decoder loads.</summary>
        public bool OpusAccepted
        {
            get { return opusEnabled && OpusSupport.Available; }
        }

        /// <summary>The latest snapshot, refreshed every 500 ms.</summary>
        public AudioStats Stats
        {
            get { return stats; }
        }

        /// <summary>Raised on the timer thread after each new snapshot.</summary>
        public event Action<AudioStats> StatsUpdated;

        /// <summary>Binds the port and starts the receive thread and the stats timer. Never throws.</summary>
        public void Start()
        {
            if (running) return;
            running = true;
            Bind();
            timer = new Timer(state => Tick(clockMs()), null, StatsIntervalMs, StatsIntervalMs);
        }

        /// <summary>Moves the listener to another port (the settings changed). Streams keep their state.</summary>
        public void Rebind(int port)
        {
            if (port == Port && Listening) return;
            Port = port;
            if (!running) return;
            CloseSocket();
            Bind();
        }

        // Control channel API (called by the control server, #20).

        /// <summary>
        /// audioStart for one stream (§6.11): (re)starts it with this format. A stream already started is restarted,
        /// which is how the tablet announces a format change. Returns false, and logs, for an invalid announcement.
        /// </summary>
        public bool OnAudioStart(AudioStreamType stream, int sampleRate, int channels, AudioFormat format)
        {
            return OnAudioStart(stream, sampleRate, channels, format, null);
        }

        /// <summary>
        /// audioStart from the tablet at <paramref name="source"/>: the stream then accepts datagrams from that IP only.
        /// A stream of the same type owned by another tablet is replaced (one stream per type; the last audioStart wins).
        /// </summary>
        public bool OnAudioStart(AudioStreamType stream, int sampleRate, int channels, AudioFormat format, IPAddress source)
        {
            if (stream != AudioStreamType.Media && stream != AudioStreamType.Alt && stream != AudioStreamType.Telephony)
            {
                AudioLog.Warn("audioStart ignored: stream " + AudioHeader.StreamName(stream) + " cannot be received");
                return false;
            }
            if (!AudioHeader.IsValidSampleRate(sampleRate) || (channels != 1 && channels != 2) || (format != AudioFormat.PcmS16le && format != AudioFormat.Opus))
            {
                AudioLog.Warn("audioStart ignored: " + AudioHeader.StreamName(stream) + " " + sampleRate + " Hz x" + channels + " " + AudioHeader.FormatName(format) + " is not a valid format");
                return false;
            }
            if (format == AudioFormat.Opus)
            {
                if (!OpusAccepted)
                {
                    AudioLog.Warn("audioStart ignored: " + AudioHeader.StreamName(stream) + " in opus, which this receiver did not offer ("
                        + (opusEnabled ? OpusSupport.UnavailableReason : "the Opus setting is off") + ")");
                    return false;
                }
                if (!AudioHeader.IsOpusSampleRate(sampleRate))
                {
                    AudioLog.Warn("audioStart ignored: " + AudioHeader.StreamName(stream) + " opus at " + sampleRate + " Hz; Opus takes 8, 12, 16, 24 or 48 kHz");
                    return false;
                }
            }
            lock (gate)
            {
                try
                {
                    StartLocked(stream, sampleRate, channels, format, false, Normalize(source));
                }
                catch (Exception ex)
                {
                    AudioLog.Warn("audioStart " + AudioHeader.StreamName(stream) + " " + AudioHeader.FormatName(format) + " failed: " + ex.Message);
                    return false;
                }
            }
            return true;
        }

        /// <summary>audioStart with the JSON member values: stream "media"/"alt"/"telephony", format "pcm_s16le" or "opus".</summary>
        public bool OnAudioStart(string stream, string format, int sampleRate, int channels)
        {
            return OnAudioStart(stream, format, sampleRate, channels, null);
        }

        public bool OnAudioStart(string stream, string format, int sampleRate, int channels, IPAddress source)
        {
            AudioStreamType type;
            AudioFormat fmt;
            if (!AudioHeader.TryParseStreamName(stream, out type) || !AudioHeader.TryParseFormatName(format, out fmt))
            {
                AudioLog.Warn("audioStart ignored: stream \"" + stream + "\" format \"" + format + "\"");
                return false;
            }
            return OnAudioStart(type, sampleRate, channels, fmt, source);
        }

        /// <summary>audioStop for one stream (§6.12): it leaves the mix at once and its buffer is discarded.</summary>
        public void OnAudioStop(AudioStreamType stream)
        {
            lock (gate)
            {
                StopLocked(stream, "audioStop");
            }
        }

        public void OnAudioStop(string stream)
        {
            AudioStreamType type;
            if (AudioHeader.TryParseStreamName(stream, out type)) OnAudioStop(type);
        }

        /// <summary>audioStop from the tablet at <paramref name="source"/>: ignored when another tablet owns the stream.</summary>
        public void OnAudioStop(string stream, IPAddress source)
        {
            AudioStreamType type;
            if (!AudioHeader.TryParseStreamName(stream, out type)) return;
            source = Normalize(source);
            lock (gate)
            {
                AudioStream current;
                if (!streams.TryGetValue(type, out current)) return;
                if (source != null && current.Source != null && !current.Source.Equals(source))
                {
                    AudioLog.Info("audioStop " + stream + " from " + source + " ignored: the stream belongs to " + current.Source);
                    return;
                }
                StopLocked(type, "audioStop");
            }
        }

        /// <summary>
        /// The session of the tablet at <paramref name="source"/> closed while other tablets stay paired: the streams it
        /// started stop, the others play on.
        /// </summary>
        public void OnSourceLost(IPAddress source)
        {
            source = Normalize(source);
            if (source == null) return;
            lock (gate)
            {
                foreach (var type in StreamTypes)
                {
                    AudioStream stream;
                    if (streams.TryGetValue(type, out stream) && source.Equals(stream.Source)) StopLocked(type, "session of " + source + " closed");
                }
            }
        }

        /// <summary>The session's link was lost (§9): every stream stops, as after audioStop.</summary>
        public void OnLinkLost()
        {
            lock (gate)
            {
                foreach (var type in StreamTypes) StopLocked(type, "link lost");
            }
        }

        /// <summary>Records an error for the page (the output reports device failures here).</summary>
        public void ReportError(string message)
        {
            lastError = message ?? "";
        }

        public void Dispose()
        {
            running = false;
            try { timer?.Dispose(); } catch { }
            timer = null;
            CloseSocket();
            OnLinkLost();
        }

        // Datagram path

        /// <summary>Handles one datagram. Called on the receive thread; internal for tests.</summary>
        internal void ProcessDatagram(byte[] data, int length, IPAddress from)
        {
            Interlocked.Increment(ref datagrams);
            AudioHeader header;
            var error = AudioHeader.TryParse(data, 0, length, out header);
            if (error != AudioHeaderError.Ok)
            {
                Interlocked.Increment(ref invalid);
                LogOnce("invalid:" + error, "Dropped an invalid audio datagram (" + error + ", " + length + " bytes) from " + from + "; further ones are counted silently");
                return;
            }

            var filter = SourceFilter;
            if (filter != null && !filter(from))
            {
                Interlocked.Increment(ref rejected);
                LogOnce("source:" + from, "Dropped audio from " + from + ": not a paired tablet");
                return;
            }

            var now = clockMs();
            lock (gate)
            {
                AudioStream stream;
                streams.TryGetValue(header.StreamType, out stream);
                if (stream == null || (!stream.Matches(header) && header.IsStart && stream.AutoStarted))
                {
                    if (!header.IsStart || !AutoStartOnFirstFlag || (header.Format == AudioFormat.Opus && (!OpusAccepted || !AudioHeader.IsOpusSampleRate(header.SampleRate))))
                    {
                        Interlocked.Increment(ref rejected);
                        LogOnce("notstarted:" + header.StreamType, "Dropped " + AudioHeader.StreamName(header.StreamType) + " audio: the stream was not started (no audioStart)");
                        return;
                    }
                    stream = StartLocked(header.StreamType, header.SampleRate, header.Channels, header.Format, true, null);
                }
                else if (stream.Source != null && !stream.Source.Equals(Normalize(from)))
                {
                    Interlocked.Increment(ref rejected);
                    LogOnce("owner:" + header.StreamType + ":" + from, "Dropped " + AudioHeader.StreamName(header.StreamType) + " audio from " + from + ": that stream was started by " + stream.Source);
                    return;
                }
                else if (!stream.Matches(header))
                {
                    Interlocked.Increment(ref rejected);
                    LogOnce("format:" + header.StreamType, "Dropped " + AudioHeader.StreamName(header.StreamType) + " audio in " + AudioStats.DescribeFormat(header.SampleRate, header.Channels, header.Format)
                        + ": the stream was started as " + stream.ToString());
                    return;
                }

                if (stream.Decoder != null)
                {
                    var frames = stream.Decoder.Decode(data, AudioHeader.Size, length - AudioHeader.Size, stream.DecodedPcm);
                    if (frames <= 0)
                    {
                        Interlocked.Increment(ref invalid);
                        stream.DecodeFailures++;
                        LogOnce("opus:" + header.StreamType, "Dropped an " + AudioHeader.StreamName(header.StreamType) + " datagram the Opus decoder could not decode (" + (length - AudioHeader.Size) + " bytes); further ones are counted silently");
                        return;
                    }
                    stream.Buffer.Push(header.Seq, header.Timestamp, header.IsStart, stream.DecodedPcm, 0, frames * stream.Buffer.BlockAlign);
                }
                else
                {
                    stream.Buffer.Push(header.Seq, header.Timestamp, header.IsStart, data, AudioHeader.Size, length - AudioHeader.Size);
                }
                stream.LastPacketMs = now;
                if (!stream.Active)
                {
                    stream.Active = true;
                    AudioLog.Info("Audio " + stream + " playing" + (stream.AutoStarted ? " (started by the first datagram)" : ""));
                    NotifyActivated(stream);
                }
            }
        }

        /// <summary>The 500 ms housekeeping: idle streams leave the mix, then a new stats snapshot. Internal for tests.</summary>
        internal void Tick(long now)
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                var snapshot = new AudioStats
                {
                    At = DateTime.UtcNow,
                    Port = Port,
                    Listening = Listening,
                    Datagrams = Interlocked.Read(ref datagrams),
                    Invalid = Interlocked.Read(ref invalid),
                    Rejected = Interlocked.Read(ref rejected),
                    LastError = lastError,
                };
                var seconds = now / 1000.0;
                double learned = 0;
                lock (gate)
                {
                    datagramMeter.Add(seconds, snapshot.Datagrams, 0);
                    snapshot.DatagramsPerSecond = datagramMeter.PacketsPerSecond;
                    foreach (var type in StreamTypes)
                    {
                        AudioStream stream;
                        if (!streams.TryGetValue(type, out stream))
                        {
                            snapshot.Streams.Add(new AudioStreamStats { Stream = type });
                            continue;
                        }
                        if (stream.Active && now - stream.LastPacketMs >= IdleTimeoutMs)
                        {
                            AudioLog.Info("Audio " + stream + ": no datagram for " + (IdleTimeoutMs / 1000) + " s, output stopped");
                            Deactivate(stream);
                        }
                        var c = stream.Buffer.Counters;
                        stream.Meter.Add(seconds, c.Received, c.Lost);
                        var targetMsNow = c.TargetFrames * 1000.0 / stream.SampleRate;
                        if (targetMsNow > learnedMaxMs + 0.5)
                        {
                            learnedMaxMs = targetMsNow;
                            learned = targetMsNow;
                        }
                        snapshot.Streams.Add(new AudioStreamStats
                        {
                            Stream = type,
                            Started = true,
                            Active = stream.Active,
                            AutoStarted = stream.AutoStarted,
                            SampleRate = stream.SampleRate,
                            Channels = stream.Channels,
                            Format = stream.Format,
                            PacketsPerSecond = stream.Meter.PacketsPerSecond,
                            LossPercent = stream.Meter.LossPercent,
                            BufferMs = c.BufferedFrames * 1000.0 / stream.SampleRate,
                            TargetMs = c.TargetFrames * 1000.0 / stream.SampleRate,
                            Received = c.Received,
                            Lost = c.Lost,
                            Late = c.Late,
                            Underruns = c.Underruns,
                            Skips = c.Overflows,
                            LongestStallMs = c.LongestStallFrames * 1000.0 / stream.SampleRate,
                            CatchingUp = c.CatchingUp,
                            DecodeFailures = stream.DecodeFailures,
                        });
                    }
                }
                if (learned > 0)
                {
                    AudioLog.Info("Audio buffer learned: streams now start with " + learned.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms of depth on this network");
                    try { TargetLearned?.Invoke(learned); } catch (Exception ex) { AudioLog.Warn("A buffer listener failed: " + ex.Message); }
                }
                try
                {
                    var output = OutputStatus;
                    if (output != null) snapshot.Output = output() ?? "";
                }
                catch (Exception ex)
                {
                    snapshot.Output = "Unknown (" + ex.Message + ")";
                }
                stats = snapshot;
                try { StatsUpdated?.Invoke(snapshot); } catch (Exception ex) { AudioLog.Warn("A stats listener failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                AudioLog.Warn("Audio housekeeping failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref ticking, 0);
            }
        }

        /// <summary>True while <paramref name="type"/> is started; for tests and the control server.</summary>
        public bool IsStarted(AudioStreamType type)
        {
            lock (gate) return streams.ContainsKey(type);
        }

        /// <summary>The started stream of this type, or null.</summary>
        public AudioStream GetStream(AudioStreamType type)
        {
            lock (gate)
            {
                AudioStream stream;
                return streams.TryGetValue(type, out stream) ? stream : null;
            }
        }

        private AudioStream StartLocked(AudioStreamType type, int sampleRate, int channels, AudioFormat format, bool auto, IPAddress source)
        {
            AudioStream old;
            if (streams.TryGetValue(type, out old))
            {
                Deactivate(old);
                streams.Remove(type);
                learnedTargetMs[type] = old.Buffer.TargetMs;
                StopLockedDispose(old);
            }
            var stream = new AudioStream(type, sampleRate, channels, format, auto, targetMs, MaxTargetMsFor(type, maxTargetMs), skipSlackMs, source);
            double learned;
            if (learnedTargetMs.TryGetValue(type, out learned)) stream.Buffer.InheritTarget(learned);
            else if (learnedMaxMs > 0) stream.Buffer.InheritTarget(learnedMaxMs);
            stream.Meter.Add(clockMs() / 1000.0, 0, 0); // so the first snapshot already has a rate
            streams[type] = stream;
            AudioLog.Info("Audio stream " + stream + (old == null ? "" : " (restarted)") + " started" + (auto ? " by its first datagram (no control channel)" : source != null ? " by " + source : ""));
            return stream;
        }

        private void StopLocked(AudioStreamType type, string reason)
        {
            AudioStream stream;
            if (!streams.TryGetValue(type, out stream)) return;
            streams.Remove(type);
            Deactivate(stream);
            learnedTargetMs[type] = stream.Buffer.TargetMs;
            StopLockedDispose(stream);
            AudioLog.Info("Audio stream " + stream + " stopped (" + reason + ")");
        }

        private void Deactivate(AudioStream stream)
        {
            if (stream.Active)
            {
                stream.Active = false;
                try { sink?.StreamDeactivated(stream); } catch (Exception ex) { AudioLog.Warn("The audio output failed to remove a stream: " + ex.Message); }
            }
            stream.Buffer.Reset();
        }

        private void StopLockedDispose(AudioStream stream)
        {
            try { stream.Decoder?.Dispose(); } catch { }
        }

        private void NotifyActivated(AudioStream stream)
        {
            try { sink?.StreamActivated(stream); } catch (Exception ex) { AudioLog.Warn("The audio output failed to add a stream: " + ex.Message); }
        }

        // Socket

        private void Bind()
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                try
                {
                    client.Client.ReceiveBufferSize = 1 << 20;
                    DisableConnectionReset(client.Client);
                    client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                }
                catch
                {
                    client.Close();
                    throw;
                }
                udp = client;
                BoundPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
                var t = new Thread(() => ReceiveLoop(client))
                {
                    IsBackground = true,
                    Name = "rigPlay audio receiver",
                    Priority = ThreadPriority.AboveNormal,
                };
                thread = t;
                t.Start();
                AudioLog.Info("Audio receiver listening on UDP port " + BoundPort);
                if (lastError.StartsWith("Audio port", StringComparison.Ordinal)) lastError = "";
            }
            catch (Exception ex)
            {
                BoundPort = 0;
                lastError = "Audio port " + Port + " could not be bound: " + ex.Message;
                AudioLog.Warn(lastError + ". rigPlay keeps running without audio.");
            }
        }

        private void CloseSocket()
        {
            var client = udp;
            var t = thread;
            udp = null;
            thread = null;
            BoundPort = 0;
            try { client?.Close(); } catch { }
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(1000); } catch { }
            }
        }

        private void ReceiveLoop(UdpClient client)
        {
            var buffer = new byte[65536];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (running && ReferenceEquals(udp, client))
            {
                int length;
                try
                {
                    length = client.Client.ReceiveFrom(buffer, ref remote);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (!running || !ReferenceEquals(udp, client)) break;
                    // ICMP port unreachable from an earlier send, or an oversized datagram: not fatal.
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) continue;
                    lastError = "Audio receive failed: " + ex.Message;
                    AudioLog.Warn(lastError);
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    ProcessDatagram(buffer, length, ((IPEndPoint)remote).Address);
                }
                catch (Exception ex)
                {
                    lastError = "Audio datagram handling failed: " + ex.Message;
                    LogOnce("process:" + ex.GetType().Name, lastError + " " + ex);
                }
            }
        }

        /// <summary>Windows reports an ICMP port-unreachable as a receive error on UDP sockets; turn that off.</summary>
        private static void DisableConnectionReset(Socket socket)
        {
            try
            {
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch
            {
                // Not Windows, or not supported: nothing to turn off.
            }
        }

        private void LogOnce(string key, string message)
        {
            lock (loggedOnce)
            {
                if (loggedOnce.Count > 256) return;
                if (!loggedOnce.Add(key)) return;
            }
            AudioLog.Warn(message);
        }

        /// <summary>
        /// The formats this receiver plays, most preferred first (state.audio.formats, spec §6.6): opus then pcm_s16le
        /// while <see cref="OpusAccepted"/>, otherwise pcm_s16le alone.
        /// </summary>
        public List<string> SupportedFormats()
        {
            var formats = new List<string>();
            if (OpusAccepted) formats.Add(AudioHeader.FormatName(AudioFormat.Opus));
            formats.Add(AudioHeader.FormatName(AudioFormat.PcmS16le));
            return formats;
        }

        private static IPAddress Normalize(IPAddress address)
        {
            if (address != null && address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            return address;
        }

        private static long StopwatchMs()
        {
            return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        }
    }
}
