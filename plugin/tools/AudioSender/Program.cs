// SPDX-License-Identifier: GPL-3.0-only
// AudioSender: streams audio to the rigPlay plugin's audio port the way the tablet does (docs/protocol.md §10):
// 12-byte big-endian header, s16 little-endian payload, 5 ms datagrams by default, the start flag on the first
// one, seq and timestamp from 0; or, with --opus, one 20 ms Opus packet per datagram (§10.4), encoded with
// Concentus. There is no control channel here: the plugin accepts audio only from the IP of a paired tablet that
// sent audioStart for the stream (in the same format), so pair a (fake) tablet from the same address and send
// audioStart on its control connection first; otherwise the datagrams are counted as rejected.
//
//   dotnet run -- <host> <port> <file.wav> [options]
//   dotnet run -- <host> <port> --tone 440 [options]
//
// Options:
//   --tone <hz>          send a sine tone instead of a file
//   --opus [kbps]        send Opus packets (20 ms each) instead of PCM, at kbps (default 96 stereo, 48 mono);
//                        the source must be at 8, 12, 16, 24 or 48 kHz
//   --loss <percent>     drop this share of datagrams at random (seq and timestamp still advance)
//   --stream <name>      media (default), alt or telephony
//   --rate <hz>          tone sample rate (default 48000; a multiple of 100 from 8000 to 48000)
//   --channels <1|2>     tone channels (default 2)
//   --ms <n>             milliseconds per datagram (default 5)
//   --seconds <n>        stop after n seconds (default: the file's length, or forever for a tone)
//   --loop               repeat the file
//   --volume <0..1>      tone amplitude (default 0.3)
//   --seed <n>           random seed for --loss
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus;
using Concentus.Enums;
using RigPlayPlugin.Audio;

namespace RigPlayPlugin.Tools.AudioSender
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Options o;
            try
            {
                o = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine("usage: AudioSender <host> <port> (<file.wav> | --tone <hz>) [--opus [kbps]] [--loss <percent>] [--stream media|alt|telephony]");
                Console.Error.WriteLine("                   [--rate <hz>] [--channels 1|2] [--ms <n>] [--seconds <n>] [--loop] [--volume <0..1>] [--seed <n>]");
                return 2;
            }

            ISource source;
            try
            {
                source = o.ToneHz > 0 ? new ToneSource(o.ToneHz, o.Rate, o.Channels, o.Volume) : (ISource)WavSource.Load(o.File, o.Loop);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }

            IPAddress address;
            try
            {
                address = IPAddress.TryParse(o.Host, out var parsed) ? parsed : Array.Find(Dns.GetHostAddresses(o.Host), a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine("error: cannot resolve " + o.Host + ": " + ex.Message);
                return 1;
            }
            if (address == null)
            {
                Console.Error.WriteLine("error: no IPv4 address for " + o.Host);
                return 1;
            }

            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };

            if (OperatingSystem.IsWindows()) TimeBeginPeriod(1);
            try
            {
                return Send(o, source, new IPEndPoint(address, o.Port), stop);
            }
            finally
            {
                if (OperatingSystem.IsWindows()) TimeEndPeriod(1);
            }
        }

        private static int Send(Options o, ISource source, IPEndPoint target, ManualResetEventSlim stop)
        {
            var rate = source.SampleRate;
            var channels = source.Channels;
            var opus = o.Opus;
            if (opus && !AudioHeader.IsOpusSampleRate(rate))
            {
                Console.Error.WriteLine("error: --opus needs a source at 8, 12, 16, 24 or 48 kHz, not " + rate + " Hz (use --rate for a tone, or resample the file)");
                return 2;
            }
            var frames = opus ? rate / 50 : Math.Max(1, rate * o.Ms / 1000);
            var samples = new short[frames * channels];
            var random = o.Seed.HasValue ? new Random(o.Seed.Value) : new Random();
            var maxFrames = o.Seconds > 0 ? (long)(o.Seconds * rate) : long.MaxValue;
            IOpusEncoder encoder = null;
            var packet = new byte[AudioHeader.MaxOpusPacketBytes];
            var kbps = o.OpusKbps > 0 ? o.OpusKbps : channels == 2 ? 96 : 48;
            if (opus)
            {
                encoder = OpusCodecFactory.CreateEncoder(rate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                encoder.Bitrate = kbps * 1000;
                encoder.Complexity = 5;
            }
            var format = opus ? AudioFormat.Opus : AudioFormat.PcmS16le;

            Console.WriteLine("Sending " + AudioHeader.StreamName(o.Stream) + " " + rate + " Hz x" + channels + " " + AudioHeader.FormatName(format) + " to " + target
                + ": " + frames + " frames (" + (opus ? "about " + (kbps * 1000 / 50 / 8) + " B, " + kbps + " kbit/s" : (frames * channels * 2) + " B") + ") per datagram"
                + (o.LossPercent > 0 ? ", " + o.LossPercent.ToString("0.#", CultureInfo.InvariantCulture) + " % simulated loss" : "")
                + (o.ToneHz > 0 ? ", tone " + o.ToneHz + " Hz" : ", " + o.File) + ". Ctrl+C stops.");

            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                ushort seq = 0;
                uint timestamp = 0;
                long framesSent = 0, sent = 0, dropped = 0, lastSent = 0, lastDropped = 0;
                var clock = Stopwatch.StartNew();
                var nextReport = 1000L;
                while (!stop.IsSet && framesSent < maxFrames)
                {
                    var n = source.Read(samples, frames);
                    if (n == 0) break;
                    if (opus && n < frames)
                    {
                        // The encoder takes whole frames: pad the file's tail with silence.
                        Array.Clear(samples, n * channels, (frames - n) * channels);
                        n = frames;
                    }
                    var header = AudioHeader.Create(seq, o.Stream, seq == 0 && framesSent == 0, timestamp, rate, channels, format);
                    byte[] datagram = null;
                    if (opus)
                    {
                        var size = encoder.Encode(samples, frames, packet, packet.Length);
                        datagram = new byte[AudioHeader.Size + size];
                        header.Write(datagram, 0);
                        Array.Copy(packet, 0, datagram, AudioHeader.Size, size);
                    }
                    if (o.LossPercent > 0 && random.NextDouble() * 100.0 < o.LossPercent)
                    {
                        dropped++;
                    }
                    else
                    {
                        if (datagram == null) datagram = header.Encode(samples, 0, n * channels);
                        try
                        {
                            udp.Send(datagram, datagram.Length, target);
                            sent++;
                        }
                        catch (SocketException ex)
                        {
                            // ICMP port unreachable from a previous datagram (nothing listening yet): keep going.
                            if (ex.SocketErrorCode != SocketError.ConnectionReset) Console.Error.WriteLine("send failed: " + ex.Message);
                        }
                    }
                    seq++;
                    timestamp += (uint)n;
                    framesSent += n;

                    // Pace on the sample clock: datagram k is due at k * frames / rate seconds.
                    var dueMs = framesSent * 1000 / rate;
                    while (!stop.IsSet)
                    {
                        var ahead = dueMs - clock.ElapsedMilliseconds;
                        if (ahead <= 0) break;
                        if (ahead > 2) Thread.Sleep(1);
                        else Thread.SpinWait(200);
                    }

                    if (clock.ElapsedMilliseconds >= nextReport)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,6:0.0} s  {1} datagrams/s sent, {2} dropped (total {3} sent, {4} dropped, {5:0.0} %)",
                            clock.ElapsedMilliseconds / 1000.0, sent - lastSent, dropped - lastDropped, sent, dropped,
                            sent + dropped == 0 ? 0 : 100.0 * dropped / (sent + dropped)));
                        lastSent = sent;
                        lastDropped = dropped;
                        nextReport += 1000;
                    }
                }
                Console.WriteLine("Done: " + sent + " datagrams sent, " + dropped + " dropped, " + (framesSent / (double)rate).ToString("0.0", CultureInfo.InvariantCulture) + " s of audio.");
            }
            return 0;
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint milliseconds);
    }

    internal sealed class Options
    {
        public string Host;
        public int Port;
        public string File;
        public int ToneHz;
        public double LossPercent;
        public AudioStreamType Stream = AudioStreamType.Media;
        public int Rate = 48000;
        public int Channels = 2;
        public int Ms = 5;
        public double Seconds;
        public bool Loop;
        public double Volume = 0.3;
        public int? Seed;
        public bool Opus;
        public int OpusKbps;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            var positional = 0;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value");
                    return args[++i];
                }
                switch (a)
                {
                    case "--tone": o.ToneHz = Int(Next(), a, 1, 20000); break;
                    case "--opus":
                        o.Opus = true;
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps)) o.OpusKbps = Int(Next(), a, 6, 510);
                        break;
                    case "--loss": o.LossPercent = Dbl(Next(), a, 0, 100); break;
                    case "--stream":
                        if (!AudioHeader.TryParseStreamName(Next(), out o.Stream)) throw new ArgumentException("--stream must be media, alt or telephony");
                        break;
                    case "--rate": o.Rate = Int(Next(), a, 8000, 48000); break;
                    case "--channels": o.Channels = Int(Next(), a, 1, 2); break;
                    case "--ms": o.Ms = Int(Next(), a, 1, 40); break;
                    case "--seconds": o.Seconds = Dbl(Next(), a, 0, 1e7); break;
                    case "--loop": o.Loop = true; break;
                    case "--volume": o.Volume = Dbl(Next(), a, 0, 1); break;
                    case "--seed": o.Seed = Int(Next(), a, int.MinValue, int.MaxValue); break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unknown option " + a);
                        switch (positional++)
                        {
                            case 0: o.Host = a; break;
                            case 1: o.Port = Int(a, "port", 1, 65535); break;
                            case 2: o.File = a; break;
                            default: throw new ArgumentException("unexpected argument " + a);
                        }
                        break;
                }
            }
            if (o.Host == null || o.Port == 0) throw new ArgumentException("host and port are required");
            if (o.ToneHz == 0 && o.File == null) throw new ArgumentException("give a WAV file or --tone <hz>");
            if (o.ToneHz > 0 && o.File != null) throw new ArgumentException("give either a WAV file or --tone, not both");
            if (!AudioHeader.IsValidSampleRate(o.Rate)) throw new ArgumentException("--rate must be a multiple of 100 from 8000 to 48000");
            if (o.Opus && o.ToneHz > 0 && !AudioHeader.IsOpusSampleRate(o.Rate)) throw new ArgumentException("--opus takes --rate 8000, 12000, 16000, 24000 or 48000");
            var datagramBytes = o.Rate * o.Ms / 1000 * o.Channels * 2;
            if (datagramBytes > AudioHeader.MaxPayloadBytes) throw new ArgumentException("--ms too large: " + datagramBytes + " B payload exceeds " + AudioHeader.MaxPayloadBytes);
            return o;
        }

        private static int Int(string s, string name, int min, int max)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw new ArgumentException(name + " must be an integer from " + min + " to " + max);
            return v;
        }

        private static double Dbl(string s, string name, double min, double max)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw new ArgumentException(name + " must be a number from " + min + " to " + max);
            return v;
        }
    }

    internal interface ISource
    {
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>Fills up to <paramref name="frames"/> interleaved frames; returns the number written, 0 at the end.</summary>
        int Read(short[] buffer, int frames);
    }

    internal sealed class ToneSource : ISource
    {
        private readonly double step;
        private readonly double amplitude;
        private double phase;

        public ToneSource(int hz, int rate, int channels, double volume)
        {
            SampleRate = rate;
            Channels = channels;
            step = 2 * Math.PI * hz / rate;
            amplitude = volume * short.MaxValue;
        }

        public int SampleRate { get; }
        public int Channels { get; }

        public int Read(short[] buffer, int frames)
        {
            for (var f = 0; f < frames; f++)
            {
                var v = (short)Math.Round(Math.Sin(phase) * amplitude);
                phase += step;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                for (var c = 0; c < Channels; c++) buffer[f * Channels + c] = v;
            }
            return frames;
        }
    }

    /// <summary>A WAV file decoded to s16: PCM 8/16/24/32-bit or IEEE float 32/64-bit, any rate and channel count.</summary>
    internal sealed class WavSource : ISource
    {
        private readonly short[] samples;
        private readonly bool loop;
        private int position;

        private WavSource(short[] samples, int rate, int channels, bool loop)
        {
            this.samples = samples;
            this.loop = loop;
            SampleRate = rate;
            Channels = channels;
        }

        public int SampleRate { get; }
        public int Channels { get; }

        public int Read(short[] buffer, int frames)
        {
            var total = samples.Length / Channels;
            if (total == 0) return 0;
            var written = 0;
            while (written < frames)
            {
                if (position >= total)
                {
                    if (!loop) break;
                    position = 0;
                }
                var take = Math.Min(frames - written, total - position);
                Array.Copy(samples, position * Channels, buffer, written * Channels, take * Channels);
                position += take;
                written += take;
            }
            return written;
        }

        public static WavSource Load(string path, bool loop)
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length < 12 || Ascii(bytes, 0) != "RIFF" || Ascii(bytes, 8) != "WAVE") throw new InvalidDataException(path + " is not a RIFF/WAVE file");

            int formatTag = 0, channels = 0, rate = 0, bits = 0, dataOffset = -1, dataLength = 0;
            var p = 12;
            while (p + 8 <= bytes.Length)
            {
                var id = Ascii(bytes, p);
                var size = BitConverter.ToInt32(bytes, p + 4);
                var body = p + 8;
                if (size < 0) break;
                if (id == "fmt ")
                {
                    formatTag = BitConverter.ToUInt16(bytes, body);
                    channels = BitConverter.ToUInt16(bytes, body + 2);
                    rate = BitConverter.ToInt32(bytes, body + 4);
                    bits = BitConverter.ToUInt16(bytes, body + 14);
                    if (formatTag == 0xFFFE && size >= 40) formatTag = BitConverter.ToUInt16(bytes, body + 24); // WAVE_FORMAT_EXTENSIBLE sub-format
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(size, bytes.Length - body);
                    break;
                }
                p = body + size + (size & 1);
            }
            if (dataOffset < 0 || channels < 1 || rate < 1) throw new InvalidDataException(path + ": no fmt or data chunk");
            if (formatTag != 1 && formatTag != 3) throw new InvalidDataException(path + ": format tag " + formatTag + " is not PCM or IEEE float");

            var bytesPerSample = bits / 8;
            var frames = dataLength / (bytesPerSample * channels);
            var outChannels = Math.Min(2, channels);
            var floats = new float[frames * outChannels];
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < outChannels; c++)
                {
                    var at = dataOffset + (f * channels + c) * bytesPerSample;
                    floats[f * outChannels + c] = ReadSample(bytes, at, formatTag, bits);
                }
            }

            // The header carries the rate in units of 100 Hz: resample 11025, 22050, 96000... to 48000.
            var outRate = rate;
            if (!AudioHeader.IsValidSampleRate(rate))
            {
                outRate = 48000;
                floats = Resample(floats, outChannels, rate, outRate);
                Console.WriteLine(path + ": resampled " + rate + " Hz to " + outRate + " Hz");
            }

            var s16 = new short[floats.Length];
            for (var i = 0; i < floats.Length; i++) s16[i] = (short)Math.Round(Math.Clamp(floats[i], -1f, 1f) * short.MaxValue);
            Console.WriteLine(path + ": " + rate + " Hz, " + channels + " ch, " + bits + "-bit " + (formatTag == 3 ? "float" : "PCM") + ", " + (frames / (double)rate).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            return new WavSource(s16, outRate, outChannels, loop);
        }

        private static float ReadSample(byte[] b, int at, int formatTag, int bits)
        {
            if (formatTag == 3)
            {
                return bits == 64 ? (float)BitConverter.ToDouble(b, at) : BitConverter.ToSingle(b, at);
            }
            switch (bits)
            {
                case 8: return (b[at] - 128) / 128f;
                case 16: return BitConverter.ToInt16(b, at) / 32768f;
                case 24: return ((b[at] << 8 | b[at + 1] << 16 | b[at + 2] << 24) >> 8) / 8388608f;
                case 32: return BitConverter.ToInt32(b, at) / 2147483648f;
                default: throw new InvalidDataException(bits + "-bit PCM is not supported");
            }
        }

        private static float[] Resample(float[] input, int channels, int fromRate, int toRate)
        {
            var inFrames = input.Length / channels;
            var outFrames = (int)((long)inFrames * toRate / fromRate);
            var output = new float[outFrames * channels];
            for (var f = 0; f < outFrames; f++)
            {
                var src = (double)f * fromRate / toRate;
                var i0 = (int)src;
                var i1 = Math.Min(i0 + 1, inFrames - 1);
                var t = (float)(src - i0);
                for (var c = 0; c < channels; c++)
                {
                    output[f * channels + c] = input[i0 * channels + c] * (1 - t) + input[i1 * channels + c] * t;
                }
            }
            return output;
        }

        private static string Ascii(byte[] b, int at)
        {
            return at + 4 <= b.Length ? System.Text.Encoding.ASCII.GetString(b, at, 4) : "";
        }
    }
}
