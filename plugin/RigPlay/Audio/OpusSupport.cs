// SPDX-License-Identifier: GPL-3.0-only
// OpusSupport.cs: the plugin's Opus decoder (docs/protocol.md §10.4), on Concentus, the managed port of libopus
// that ships next to RigPlay.dll (Concentus.dll). Everything that touches a Concentus type is behind a
// NoInlining method, so a RigPlay.dll installed without Concentus.dll still loads: Opus is then simply
// unavailable, the page says so, and state.audio.formats stays pcm_s16le only. Pure: no SimHub, WPF or NAudio
// types (compiled into RigPlay.Tests).
using System;
using System.Runtime.CompilerServices;

namespace RigPlayPlugin.Audio
{
    /// <summary>One stream's Opus decoder: packets in, interleaved s16le PCM out.</summary>
    public sealed class OpusStreamDecoder : IDisposable
    {
        private readonly object decoder;
        private readonly short[] samples;

        private OpusStreamDecoder(int sampleRate, int channels, object decoder)
        {
            SampleRate = sampleRate;
            Channels = channels;
            this.decoder = decoder;
            samples = new short[MaxFrames * channels];
        }

        /// <summary>The longest packet Opus allows: 120 ms.</summary>
        public int MaxFrames
        {
            get { return SampleRate * 120 / 1000; }
        }

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>
        /// Creates a decoder, or throws when Concentus is missing or the format is not one Opus takes (§10.4:
        /// 8, 12, 16, 24 or 48 kHz, 1 or 2 channels).
        /// </summary>
        public static OpusStreamDecoder Create(int sampleRate, int channels)
        {
            if (!AudioHeader.IsOpusSampleRate(sampleRate)) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate + " Hz is not an Opus rate");
            if (channels != 1 && channels != 2) throw new ArgumentOutOfRangeException(nameof(channels));
            return new OpusStreamDecoder(sampleRate, channels, CreateConcentus(sampleRate, channels));
        }

        /// <summary>
        /// Decodes one packet into <paramref name="pcm"/> (interleaved s16le, at least <see cref="MaxFrames"/> x
        /// <see cref="Channels"/> x 2 bytes). Returns the frames written, or 0 when the packet could not be decoded.
        /// </summary>
        public int Decode(byte[] packet, int offset, int length, byte[] pcm)
        {
            int frames;
            try
            {
                frames = DecodeConcentus(packet, offset, length);
            }
            catch (Exception)
            {
                return 0;
            }
            if (frames <= 0) return 0;
            var count = frames * Channels;
            if (pcm.Length < count * 2) throw new ArgumentException("pcm buffer too small", nameof(pcm));
            for (var i = 0; i < count; i++)
            {
                pcm[2 * i] = (byte)samples[i];
                pcm[2 * i + 1] = (byte)(samples[i] >> 8);
            }
            return frames;
        }

        /// <summary>Bytes <see cref="Decode"/> needs in its output buffer.</summary>
        public int MaxPcmBytes
        {
            get { return MaxFrames * Channels * 2; }
        }

        public void Dispose()
        {
            // Concentus decoders hold no unmanaged state.
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object CreateConcentus(int sampleRate, int channels)
        {
            return Concentus.OpusCodecFactory.CreateDecoder(sampleRate, channels);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int DecodeConcentus(byte[] packet, int offset, int length)
        {
            var d = (Concentus.IOpusDecoder)decoder;
            return d.Decode(new ReadOnlySpan<byte>(packet, offset, length), new Span<short>(samples), MaxFrames, false);
        }
    }

    /// <summary>Whether this installation can decode Opus at all (Concentus.dll present and loadable).</summary>
    public static class OpusSupport
    {
        private static readonly object gate = new object();
        private static bool probed;
        private static bool available;
        private static string reason = "";

        /// <summary>True when an Opus decoder can be created. Probed once, on first use.</summary>
        public static bool Available
        {
            get
            {
                Probe();
                return available;
            }
        }

        /// <summary>Why <see cref="Available"/> is false, for the page and the log; empty when it is true.</summary>
        public static string UnavailableReason
        {
            get
            {
                Probe();
                return reason;
            }
        }

        private static void Probe()
        {
            lock (gate)
            {
                if (probed) return;
                probed = true;
                try
                {
                    using (OpusStreamDecoder.Create(48000, 2)) available = true;
                }
                catch (Exception ex)
                {
                    available = false;
                    reason = ex is System.IO.FileNotFoundException || ex is TypeLoadException || ex is BadImageFormatException
                        ? "Concentus.dll is missing next to RigPlay.dll"
                        : "the Opus decoder failed to start: " + ex.Message;
                }
            }
        }
    }
}
