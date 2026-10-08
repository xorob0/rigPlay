// SPDX-License-Identifier: GPL-3.0-only
// MicCapture.cs: the NAudio side of the PC microphone (#34, docs/protocol.md §10.4). WasapiCapture in shared mode on the
// chosen input device (MMDeviceEnumerator, DataFlow.Capture; the Windows default recording device when the setting is
// empty or the device is gone), every buffer mixed down to mono float, resampled to the rate the tablet asked for
// (WdlResamplingSampleProvider) and handed to MicSender as s16. All device work runs on one MTA worker thread, as in
// AudioOutput. Without any input device (a PC or VM with no microphone) Start returns the reason and MicSender logs it
// once. Kept thin: packetizing, the stream lifecycle and the stats live in MicSender, which is unit-tested.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RigPlayPlugin.Audio
{
    public sealed class MicCapture : IMicCapture
    {
        /// <summary>WASAPI buffer; polled every half of it, so samples leave in 10 ms chunks.</summary>
        public const int BufferMs = 20;

        private static readonly Guid SubtypeIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        private readonly Thread worker;

        // Worker-thread state.
        private WasapiCapture recorder;
        private MMDevice recorderDevice;
        private volatile Pipeline pipeline;
        private volatile string deviceName;
        private volatile bool disposed;

        public MicCapture()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "rigPlay microphone" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public string DeviceName => deviceName;

        public string Start(string deviceId, int sampleRate, Action<short[], int> onSamples)
        {
            return Invoke(() => StartOnWorker(deviceId ?? "", sampleRate, onSamples), "the microphone worker is not responding");
        }

        public void Stop()
        {
            Invoke(() =>
            {
                StopOnWorker();
                return (string)null;
            }, null);
        }

        public bool HasDevice(string deviceId)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = Resolve(enumerator, deviceId ?? ""))
                {
                    return device != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The active recording devices, the Windows default first. Empty (never throws) without any.</summary>
        public static List<AudioDeviceInfo> ListDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                using (var e = new MMDeviceEnumerator())
                {
                    string defaultId = null;
                    try
                    {
                        if (e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                        {
                            using (var d = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) defaultId = d.ID;
                        }
                    }
                    catch
                    {
                    }
                    foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    {
                        try
                        {
                            list.Add(new AudioDeviceInfo { Id = device.ID, Name = device.FriendlyName, IsDefault = device.ID == defaultId });
                        }
                        catch
                        {
                        }
                        finally
                        {
                            device.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Listing the recording devices failed: " + ex.Message);
            }
            list.Sort((a, b) => a.IsDefault != b.IsDefault ? (a.IsDefault ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public void Dispose()
        {
            if (disposed) return;
            Invoke(() =>
            {
                StopOnWorker();
                return (string)null;
            }, null);
            disposed = true;
            work.CompleteAdding();
        }

        private string StartOnWorker(string deviceId, int sampleRate, Action<short[], int> onSamples)
        {
            StopOnWorker();
            MMDevice device = null;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    device = Resolve(enumerator, deviceId);
                }
                if (device == null) return deviceId.Length == 0 ? "no recording device on this PC" : "the chosen recording device is not connected and there is no default one";
                if (deviceId.Length > 0 && !string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                    Log.Info("The chosen microphone is not connected; capturing the Windows default recording device instead");

                var capture = new WasapiCapture(device, false, BufferMs);
                var format = capture.WaveFormat;
                var next = new Pipeline(format, sampleRate, onSamples);
                capture.DataAvailable += (s, e) => next.Push(e.Buffer, e.BytesRecorded);
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null && pipeline == next) Log.Warn("Microphone capture stopped: " + e.Exception.Message);
                };
                pipeline = next;
                recorder = capture;
                recorderDevice = device;
                deviceName = SafeName(device);
                device = null;
                capture.StartRecording();
                Log.Info("Microphone capture on \"" + deviceName + "\": " + format.SampleRate + " Hz x" + format.Channels + " " + format.Encoding
                    + " " + format.BitsPerSample + "-bit, sent at " + sampleRate + " Hz mono");
                return null;
            }
            catch (Exception ex)
            {
                StopOnWorker();
                return "the recording device could not be opened: " + ex.Message;
            }
            finally
            {
                device?.Dispose();
            }
        }

        private void StopOnWorker()
        {
            var current = recorder;
            recorder = null;
            pipeline = null;
            deviceName = null;
            if (current != null)
            {
                try { current.StopRecording(); } catch (Exception) { }
                try { current.Dispose(); } catch (Exception) { }
            }
            var device = recorderDevice;
            recorderDevice = null;
            try { device?.Dispose(); } catch (Exception) { }
        }

        private static MMDevice Resolve(MMDeviceEnumerator enumerator, string wantedId)
        {
            if (wantedId.Length > 0)
            {
                try
                {
                    var device = enumerator.GetDevice(wantedId);
                    if (device != null && device.DataFlow == DataFlow.Capture && device.State == DeviceState.Active) return device;
                    device?.Dispose();
                }
                catch (Exception)
                {
                    // Unplugged or removed: fall back to the default device.
                }
            }
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        private static string SafeName(MMDevice device)
        {
            try { return device.FriendlyName; } catch (Exception) { return "recording device"; }
        }

        private T Invoke<T>(Func<T> action, T onTimeout)
        {
            if (disposed) return onTimeout;
            if (Thread.CurrentThread == worker) return action();
            T result = onTimeout;
            using (var done = new ManualResetEventSlim(false))
            {
                try
                {
                    work.Add(() =>
                    {
                        try { result = action(); }
                        catch (Exception ex) { Log.Warn("Microphone device work failed: " + ex.Message); }
                        finally { done.Set(); }
                    });
                }
                catch (InvalidOperationException)
                {
                    return onTimeout;
                }
                if (!done.Wait(5000)) return onTimeout;
            }
            return result;
        }

        private void Run()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                try { action(); } catch (Exception ex) { Log.Warn("Microphone worker: " + ex.Message); }
            }
        }

        /// <summary>
        /// Device format to mono s16 at the wanted rate: every buffer is mixed down to mono float into a FIFO that feeds
        /// WdlResamplingSampleProvider, whose output is read until it runs dry. Runs on the capture thread.
        /// </summary>
        private sealed class Pipeline
        {
            private readonly int channels;
            private readonly int bytesPerSample;
            private readonly bool isFloat;
            private readonly FloatFifo fifo;
            private readonly ISampleProvider output;
            private readonly Action<short[], int> onSamples;
            private float[] floats = new float[4096];
            private short[] shorts = new short[4096];

            public Pipeline(WaveFormat format, int sampleRate, Action<short[], int> onSamples)
            {
                this.onSamples = onSamples;
                channels = Math.Max(1, format.Channels);
                bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
                isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                    || (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
                if (!isFloat && bytesPerSample != 2 && bytesPerSample != 3 && bytesPerSample != 4)
                    throw new NotSupportedException("recording format " + format + " is not supported");
                fifo = new FloatFifo(format.SampleRate);
                output = format.SampleRate == sampleRate ? (ISampleProvider)fifo : new WdlResamplingSampleProvider(fifo, sampleRate);
            }

            public void Push(byte[] buffer, int bytes)
            {
                var frameBytes = bytesPerSample * channels;
                var frames = bytes / frameBytes;
                if (frames <= 0) return;
                var mono = fifo.Reserve(frames);
                for (var f = 0; f < frames; f++)
                {
                    var sum = 0f;
                    var at = f * frameBytes;
                    for (var c = 0; c < channels; c++) sum += ReadSample(buffer, at + c * bytesPerSample);
                    mono[f] = sum / channels;
                }
                fifo.Commit(frames);

                while (true)
                {
                    var read = output.Read(floats, 0, floats.Length);
                    if (read <= 0) break;
                    if (shorts.Length < read) shorts = new short[read];
                    for (var i = 0; i < read; i++)
                    {
                        var v = floats[i] * 32767f;
                        shorts[i] = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)v;
                    }
                    onSamples(shorts, read);
                    if (read < floats.Length) break;
                }
            }

            private float ReadSample(byte[] b, int at)
            {
                if (isFloat)
                {
                    return bytesPerSample == 8 ? (float)BitConverter.ToDouble(b, at) : BitConverter.ToSingle(b, at);
                }
                switch (bytesPerSample)
                {
                    case 2: return (short)(b[at] | (b[at + 1] << 8)) / 32768f;
                    case 3: return ((b[at] << 8) | (b[at + 1] << 16) | (b[at + 2] << 24)) / 2147483648f;
                    default: return BitConverter.ToInt32(b, at) / 2147483648f;
                }
            }
        }

        /// <summary>A mono float FIFO as a sample provider: Read returns only what has been pushed, never pads.</summary>
        private sealed class FloatFifo : ISampleProvider
        {
            private float[] data = new float[8192];
            private int start;
            private int count;
            private float[] scratch = new float[4096];

            public FloatFifo(int sampleRate)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public float[] Reserve(int frames)
            {
                if (scratch.Length < frames) scratch = new float[frames];
                return scratch;
            }

            public void Commit(int frames)
            {
                if (count + frames > data.Length)
                {
                    var bigger = new float[Math.Max(data.Length * 2, count + frames)];
                    for (var i = 0; i < count; i++) bigger[i] = data[(start + i) % data.Length];
                    data = bigger;
                    start = 0;
                }
                for (var i = 0; i < frames; i++) data[(start + count + i) % data.Length] = scratch[i];
                count += frames;
            }

            public int Read(float[] buffer, int offset, int wanted)
            {
                var n = Math.Min(wanted, count);
                for (var i = 0; i < n; i++) buffer[offset + i] = data[(start + i) % data.Length];
                start = (start + n) % data.Length;
                count -= n;
                return n;
            }
        }
    }

    /// <summary>Creates the NAudio capture without letting a missing NAudio take the plugin down.</summary>
    public static class MicCaptureFactory
    {
        /// <summary>A <see cref="MicCapture"/>, or null when NAudio could not be loaded (logged).</summary>
        public static IMicCapture TryCreate()
        {
            try
            {
                return Create();
            }
            catch (Exception ex)
            {
                Log.Error("The microphone capture could not be created (NAudio missing?); the PC microphone is unavailable", ex);
                return null;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static IMicCapture Create()
        {
            return new MicCapture();
        }
    }
}
