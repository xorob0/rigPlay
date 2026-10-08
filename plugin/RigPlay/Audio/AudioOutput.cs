// SPDX-License-Identifier: GPL-3.0-only
// AudioOutput.cs: the NAudio side of the audio receiver. Each active stream is pulled from its JitterBuffer,
// converted to float, made stereo and resampled to the 48 kHz mix (WdlResamplingSampleProvider) when its
// format differs, given its ducking gain, and mixed (MixingSampleProvider); the master volume and mute from
// the settings are applied live on top, and the mix plays through WasapiOut in shared mode on the selected
// device (MMDeviceEnumerator; the Windows default when the setting is empty or the device is gone). The device
// opens when the first stream becomes active and closes when the last one leaves. Without any output device
// (a PC or VM with no sound card) a "null pump" pulls the mix in real time instead, so the buffers and the
// stats behave exactly as with a device, and the condition is logged once. All device work happens on one
// MTA worker thread. Kept thin: the arithmetic lives in AudioMath and JitterBuffer, which are unit-tested.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RigPlayPlugin.Audio
{
    /// <summary>An output device for the picker.</summary>
    public sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;

        public override string ToString()
        {
            return Name;
        }
    }

    public sealed class AudioOutput : IAudioSink, IDisposable
    {
        /// <summary>
        /// WASAPI buffer latency in shared mode. The mix is pulled by NAudio's playback thread inside SimHub's
        /// process; this is how long that thread may be held off (GC, a busy game reader) before the device runs
        /// dry, which would be a dropout no counter on the page shows. 100 ms is still well under the jitter buffer.
        /// </summary>
        public const int DeviceLatencyMs = 100;

        private readonly Func<RigPlaySettings> settings;
        private readonly MixingSampleProvider mixer;
        private readonly GainSampleProvider master;
        private readonly Dictionary<AudioStream, ISampleProvider> chains = new Dictionary<AudioStream, ISampleProvider>();
        private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        private readonly Thread worker;

        // Worker-thread state.
        private MMDeviceEnumerator enumerator;
        private DeviceNotifications notifications;
        private WasapiOut player;
        private string playerDeviceId;
        private NullPump pump;
        private bool loggedNoDevice;

        private volatile bool altActive;
        private volatile bool telephonyActive;
        private volatile bool externalTalking;
        private volatile string status = "Idle";
        private volatile bool disposed;

        public AudioOutput(Func<RigPlaySettings> settings)
        {
            this.settings = settings;
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioMath.MixSampleRate, AudioMath.MixChannels)) { ReadFully = true };
            master = new GainSampleProvider(mixer, MasterGain);
            worker = new Thread(WorkLoop) { IsBackground = true, Name = "rigPlay audio output" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            Post(RegisterNotifications);
        }

        /// <summary>Device failures and recoveries, for the page's "last error" line.</summary>
        public event Action<string> ErrorReported;

        /// <summary>One line for the page: what the output is doing.</summary>
        public string Status()
        {
            return status;
        }

        /// <summary>
        /// A watched program (CrewChief, #58) started or stopped talking: in duck mode the media chains lower themselves to
        /// the settings' talk volume on their next buffer (ramped, so no click). Any thread.
        /// </summary>
        public void SetExternalTalking(bool talking)
        {
            externalTalking = talking;
        }

        public bool ExternalTalking
        {
            get { return externalTalking; }
        }

        /// <summary>The selected device changed in the settings: reopen on it if something is playing.</summary>
        public void DeviceSettingChanged()
        {
            Post(() =>
            {
                if (player != null || pump != null) Reopen("output device changed in the settings");
            });
        }

        // IAudioSink, called by the receiver (any thread).

        public void StreamActivated(AudioStream stream)
        {
            var chain = BuildChain(stream);
            lock (chains)
            {
                ISampleProvider old;
                if (chains.TryGetValue(stream, out old)) mixer.RemoveMixerInput(old);
                chains[stream] = chain;
                UpdateDuckingLocked();
            }
            mixer.AddMixerInput(chain);
            Post(EnsurePlaying);
        }

        public void StreamDeactivated(AudioStream stream)
        {
            bool empty;
            lock (chains)
            {
                ISampleProvider chain;
                if (chains.TryGetValue(stream, out chain))
                {
                    mixer.RemoveMixerInput(chain);
                    chains.Remove(stream);
                }
                UpdateDuckingLocked();
                empty = chains.Count == 0;
            }
            if (empty) Post(StopPlayback);
        }

        /// <summary>The active render devices, the Windows default first. Empty (never throws) without audio.</summary>
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
                        if (e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                        {
                            using (var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) defaultId = d.ID;
                        }
                    }
                    catch
                    {
                    }
                    foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
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
                Log.Warn("Listing the audio output devices failed: " + ex.Message);
            }
            list.Sort((a, b) => a.IsDefault != b.IsDefault ? (a.IsDefault ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public void Dispose()
        {
            if (disposed) return;
            Post(() =>
            {
                StopPlayback();
                try
                {
                    if (notifications != null) enumerator?.UnregisterEndpointNotificationCallback(notifications);
                }
                catch
                {
                }
                try { enumerator?.Dispose(); } catch { }
                enumerator = null;
            });
            disposed = true;
            work.CompleteAdding();
            try { worker.Join(2000); } catch { }
        }

        // Chain

        private ISampleProvider BuildChain(AudioStream stream)
        {
            ISampleProvider chain = new JitterWaveProvider(stream.Buffer).ToSampleProvider();
            if (stream.Channels == 1) chain = new MonoToStereoSampleProvider(chain);
            if (stream.SampleRate != AudioMath.MixSampleRate) chain = new WdlResamplingSampleProvider(chain, AudioMath.MixSampleRate);
            var type = stream.Type;
            return new GainSampleProvider(chain, () => AudioMath.StreamGain(type, altActive, telephonyActive, externalTalking, TalkDuckGain()));
        }

        private float TalkDuckGain()
        {
            var s = settings();
            if (s == null || !s.TalkWatchEnabled || s.TalkMode != RigPlaySettings.TalkModeDuck) return 1f;
            return AudioMath.TalkDuckGain(s.TalkDuckVolume);
        }

        private void UpdateDuckingLocked()
        {
            var alt = false;
            var tel = false;
            foreach (var s in chains.Keys)
            {
                if (s.Type == AudioStreamType.Alt) alt = true;
                if (s.Type == AudioStreamType.Telephony) tel = true;
            }
            altActive = alt;
            telephonyActive = tel;
        }

        private float MasterGain()
        {
            var s = settings();
            return s == null ? 1f : AudioMath.VolumeToGain(s.Volume, s.Muted);
        }

        // Worker thread

        private void Post(Action action)
        {
            if (disposed) return;
            try { work.Add(action); } catch (InvalidOperationException) { }
        }

        private void WorkLoop()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("Audio output task failed", ex);
                    Report("Audio output: " + ex.Message);
                }
            }
        }

        private void RegisterNotifications()
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                notifications = new DeviceNotifications(this);
                enumerator.RegisterEndpointNotificationCallback(notifications);
            }
            catch (Exception ex)
            {
                Log.Warn("Audio device notifications are unavailable: " + ex.Message);
                notifications = null;
            }
        }

        private bool HasStreams()
        {
            lock (chains) return chains.Count > 0;
        }

        private void EnsurePlaying()
        {
            if (player != null || pump != null || !HasStreams()) return;

            string wantedId = settings()?.AudioDeviceId ?? "";
            MMDevice device = null;
            try
            {
                device = ResolveDevice(wantedId);
            }
            catch (Exception ex)
            {
                Log.Warn("Looking up the audio output device failed: " + ex.Message);
            }

            if (device != null)
            {
                string name = SafeName(device);
                try
                {
                    var p = new WasapiOut(device, AudioClientShareMode.Shared, true, DeviceLatencyMs);
                    p.Init(new SampleToWaveProvider(master));
                    p.PlaybackStopped += OnPlaybackStopped;
                    p.Play();
                    player = p;
                    playerDeviceId = device.ID;
                    loggedNoDevice = false;
                    status = "Playing on " + name;
                    Log.Info("Audio output playing on \"" + name + "\" (WASAPI shared, " + DeviceLatencyMs + " ms)");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("Audio output could not open \"" + name + "\": " + ex.Message);
                    Report("Output device \"" + name + "\" could not be opened: " + ex.Message);
                }
            }

            // No usable device: keep pulling in real time so buffers and stats stay live.
            if (!loggedNoDevice)
            {
                loggedNoDevice = true;
                Log.Warn("No audio output device is available (WASAPI): rigPlay keeps receiving audio and counting stats, but nothing is played");
                Report("No audio output device: audio is received but not played");
            }
            status = "No output device: receiving only";
            pump = new NullPump(master);
            pump.Start();
        }

        private MMDevice ResolveDevice(string wantedId)
        {
            if (enumerator == null) enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(wantedId))
            {
                try
                {
                    var d = enumerator.GetDevice(wantedId);
                    if (d != null && d.State == DeviceState.Active) return d;
                }
                catch
                {
                }
                Log.Warn("The selected audio output device is not available; using the Windows default");
            }
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        private void StopPlayback()
        {
            var p = player;
            player = null;
            playerDeviceId = null;
            if (p != null)
            {
                p.PlaybackStopped -= OnPlaybackStopped;
                try { p.Stop(); } catch (Exception ex) { Log.Warn("Stopping the audio output failed: " + ex.Message); }
                try { p.Dispose(); } catch { }
                Log.Info("Audio output stopped");
            }
            var n = pump;
            pump = null;
            n?.Stop();
            status = "Idle";
        }

        private void Reopen(string reason)
        {
            Log.Info("Audio output reopening: " + reason);
            StopPlayback();
            EnsurePlaying();
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            Log.Warn("Audio output stopped with an error (device removed?): " + e.Exception.Message);
            Report("Output device error: " + e.Exception.Message);
            Post(() =>
            {
                if (ReferenceEquals(player, sender)) Reopen("the device failed");
            });
        }

        private void OnDeviceChanged(string deviceId, bool defaultChanged)
        {
            Post(() =>
            {
                if (player == null && pump == null) return;
                var usingDefault = string.IsNullOrEmpty(settings()?.AudioDeviceId);
                if (pump != null)
                {
                    // A device appeared while none was available.
                    Reopen("an audio device became available");
                }
                else if (defaultChanged && usingDefault)
                {
                    Reopen("the Windows default device changed");
                }
                else if (!defaultChanged && string.Equals(deviceId, playerDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    Reopen("the output device was removed or disabled");
                }
            });
        }

        private void Report(string message)
        {
            try { ErrorReported?.Invoke(message); } catch { }
        }

        private static string SafeName(MMDevice device)
        {
            try { return device.FriendlyName; } catch { return device.ID; }
        }

        /// <summary>IMMNotificationClient: forwards device changes to the worker; never touches COM itself.</summary>
        private sealed class DeviceNotifications : IMMNotificationClient
        {
            private readonly AudioOutput owner;

            public DeviceNotifications(AudioOutput owner)
            {
                this.owner = owner;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            {
                owner.OnDeviceChanged(deviceId, false);
            }

            public void OnDeviceAdded(string pwstrDeviceId)
            {
                owner.OnDeviceChanged(pwstrDeviceId, false);
            }

            public void OnDeviceRemoved(string deviceId)
            {
                owner.OnDeviceChanged(deviceId, false);
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia) owner.OnDeviceChanged(defaultDeviceId, true);
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
            {
            }
        }
    }

    /// <summary>A stream's jitter buffer as a 16-bit PCM wave provider for NAudio.</summary>
    internal sealed class JitterWaveProvider : IWaveProvider
    {
        private readonly JitterBuffer buffer;

        public JitterWaveProvider(JitterBuffer buffer)
        {
            this.buffer = buffer;
            WaveFormat = new WaveFormat(buffer.SampleRate, 16, buffer.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] destination, int offset, int count)
        {
            return buffer.Read(destination, offset, count);
        }
    }

    /// <summary>Applies a gain read from a function on every buffer, ramped (AudioMath.ApplyGainRamp) to avoid clicks.</summary>
    internal sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly Func<float> target;
        private float current = float.NaN;

        public GainSampleProvider(ISampleProvider source, Func<float> target)
        {
            this.source = source;
            this.target = target;
        }

        public WaveFormat WaveFormat
        {
            get { return source.WaveFormat; }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            float t;
            try { t = target(); } catch { t = 1f; }
            if (float.IsNaN(current)) current = t;
            current = AudioMath.ApplyGainRamp(buffer, offset, read, WaveFormat.Channels, current, t);
            return read;
        }
    }

    /// <summary>Pulls a sample provider in real time and discards the result: the output when there is no device.</summary>
    internal sealed class NullPump
    {
        private const int ChunkMs = 10;
        private readonly ISampleProvider source;
        private Thread thread;
        private volatile bool running;

        public NullPump(ISampleProvider source)
        {
            this.source = source;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "rigPlay audio null output" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            var t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(500);
        }

        private void Run()
        {
            var format = source.WaveFormat;
            var buffer = new float[format.SampleRate * format.Channels * ChunkMs / 1000];
            var framesPerChunk = buffer.Length / format.Channels;
            var clock = Stopwatch.StartNew();
            long framesDone = 0;
            while (running)
            {
                var due = clock.ElapsedMilliseconds * format.SampleRate / 1000;
                if (due - framesDone > format.SampleRate) framesDone = due - framesPerChunk; // fell far behind: do not catch up in a burst
                while (framesDone + framesPerChunk <= due && running)
                {
                    try { source.Read(buffer, 0, buffer.Length); } catch (Exception ex) { Log.Warn("Null audio output read failed: " + ex.Message); }
                    framesDone += framesPerChunk;
                }
                Thread.Sleep(ChunkMs / 2);
            }
        }
    }
}
