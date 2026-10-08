// SPDX-License-Identifier: GPL-3.0-only
// AudioPipeline.cs: wires the audio receiver (UDP + jitter buffers) to the NAudio output, points their logs at
// SimHub's log and starts them, together with the talk watch (TalkMonitor, #58) that lowers the media mix while
// another program on the PC talks. The plugin creates one in Init and disposes it in End; AudioGlue connects its
// receiver to the control server's sessions (audioStart / audioStop / session loss, state.audio). Constructing it never throws: without NAudio or a
// sound card the receiver still runs and the page shows why nothing plays.
using System;
using System.Runtime.CompilerServices;

namespace RigPlayPlugin.Audio
{
    public sealed class AudioPipeline : IDisposable
    {
        private readonly Func<RigPlaySettings> settings;

        /// <param name="settings">The live settings (port, device, volume, mute), read on every use so changes apply at once.</param>
        public AudioPipeline(Func<RigPlaySettings> settings)
        {
            this.settings = settings;
            AudioLog.Info = Log.Info;
            AudioLog.Warn = Log.Warn;

            var s = settings();
            var port = s?.AudioPort ?? ProtocolDefaults.AudioPort;
            Receiver = new AudioReceiver(port, new SinkProxy(this), s?.AudioBufferMs ?? JitterBuffer.DefaultTargetMs);
            if (s != null && s.LearnedAudioBufferMs > 0)
            {
                Receiver.InheritLearnedTarget(s.LearnedAudioBufferMs);
                Log.Info("Audio buffer: streams start with the " + s.LearnedAudioBufferMs + " ms learned on this network last time (minimum " + s.AudioBufferMs + " ms)");
            }
            Receiver.TargetLearned += OnTargetLearned;
            Receiver.OpusEnabled = s?.AudioOpus ?? false;
            if (Receiver.OpusEnabled && !OpusSupport.Available) Log.Warn("Opus is on in the settings but cannot be decoded: " + OpusSupport.UnavailableReason + ". Tablets are offered PCM only.");
            try
            {
                output = CreateOutput(settings, Receiver);
            }
            catch (Exception ex)
            {
                Log.Error("The audio output could not be created (NAudio missing?); audio is received but not played", ex);
                Receiver.ReportError("The audio output is unavailable: " + ex.Message);
                Receiver.OutputStatus = () => "Unavailable";
            }
            try
            {
                talkMonitor = CreateTalkMonitor(settings, this);
            }
            catch (Exception ex)
            {
                Log.Warn("The talk watch (#58) could not start: " + ex.Message);
            }
            Receiver.Start();
        }

        public AudioReceiver Receiver { get; }

        // Typed as object so that this class loads even when NAudio cannot (AudioOutput references it).
        private object output;
        private object talkMonitor;

        /// <summary>Null when NAudio could not be loaded.</summary>
        public AudioOutput Output
        {
            get { return output as AudioOutput; }
        }

        /// <summary>The watch on other programs' speech (#58); null when NAudio could not be loaded.</summary>
        public TalkMonitor TalkMonitor
        {
            get { return talkMonitor as TalkMonitor; }
        }

        /// <summary>
        /// A watched program started (true) or stopped (false) talking. Raised on the monitor's thread after the output
        /// has been told (duck mode); RigPlay.cs runs the pause mode on it.
        /// </summary>
        public event Action<bool> TalkingChanged;

        /// <summary>One line for the page about the talk watch.</summary>
        public string TalkStatus()
        {
            var m = TalkMonitor;
            return m == null ? "Unavailable (NAudio)" : m.Status();
        }

        private void OnTalkingChanged(bool talking)
        {
            Output?.SetExternalTalking(talking);
            try { TalkingChanged?.Invoke(talking); } catch (Exception ex) { Log.Warn("A talk listener failed: " + ex.Message); }
        }

        /// <summary>The latest stats snapshot, refreshed every 500 ms.</summary>
        public AudioStats Stats
        {
            get { return Receiver.Stats; }
        }

        // Control channel API, for the control server (#20). See AudioReceiver for the semantics.

        /// <summary>audioStart (§6.11) with the JSON values, e.g. ("media", "pcm_s16le", 48000, 2). False when invalid.</summary>
        public bool OnAudioStart(string stream, string format, int sampleRate, int channels)
        {
            return Receiver.OnAudioStart(stream, format, sampleRate, channels);
        }

        public bool OnAudioStart(AudioStreamType stream, int sampleRate, int channels, AudioFormat format)
        {
            return Receiver.OnAudioStart(stream, sampleRate, channels, format);
        }

        /// <summary>audioStop (§6.12), e.g. ("media").</summary>
        public void OnAudioStop(string stream)
        {
            Receiver.OnAudioStop(stream);
        }

        public void OnAudioStop(AudioStreamType stream)
        {
            Receiver.OnAudioStop(stream);
        }

        /// <summary>Link loss (§9): stops every stream.</summary>
        public void OnLinkLost()
        {
            Receiver.OnLinkLost();
        }

        /// <summary>The receiver was rebound by <see cref="ApplyPort"/>; state.audio may have changed.</summary>
        public event Action PortChanged;

        /// <summary>
        /// The learned buffer depth in the settings changed (a stream learned a deeper one, or the page forgot it);
        /// the plugin saves the settings. Raised on the receiver's stats thread.
        /// </summary>
        public event Action LearnedBufferChanged;

        /// <summary>The minimum buffer setting changed: streams started from now on begin there.</summary>
        public void ApplyBufferSetting()
        {
            var s = settings();
            if (s != null) Receiver.SetTargetMs(s.AudioBufferMs);
        }

        /// <summary>Forgets the learned depth: in the settings and in the receiver, so new streams start at the minimum again.</summary>
        public void ForgetLearnedBuffer()
        {
            var s = settings();
            if (s != null) s.LearnedAudioBufferMs = 0;
            Receiver.ForgetLearnedTarget();
            Log.Info("Audio buffer: the learned depth was forgotten; streams start at " + (s?.AudioBufferMs ?? JitterBuffer.DefaultTargetMs) + " ms again");
            try { LearnedBufferChanged?.Invoke(); } catch (Exception ex) { Log.Warn("A buffer listener failed: " + ex.Message); }
        }

        private void OnTargetLearned(double ms)
        {
            var s = settings();
            if (s == null) return;
            var rounded = (int)Math.Ceiling(ms);
            if (rounded <= s.LearnedAudioBufferMs) return;
            s.LearnedAudioBufferMs = rounded;
            try { LearnedBufferChanged?.Invoke(); } catch (Exception ex) { Log.Warn("A buffer listener failed: " + ex.Message); }
        }

        /// <summary>The audio port setting changed: rebinds the receiver and raises <see cref="PortChanged"/>.</summary>
        public void ApplyPort()
        {
            var s = settings();
            if (s == null) return;
            Receiver.Rebind(s.AudioPort);
            try { PortChanged?.Invoke(); } catch (Exception ex) { Log.Warn("A port change listener failed: " + ex.Message); }
        }

        /// <summary>The receiver's formats changed (<see cref="ApplyOpus"/>); state.audio.formats must be pushed again.</summary>
        public event Action FormatsChanged;

        /// <summary>The Opus setting changed: the receiver offers (or stops offering) opus and raises <see cref="FormatsChanged"/>.</summary>
        public void ApplyOpus()
        {
            var s = settings();
            if (s == null) return;
            Receiver.OpusEnabled = s.AudioOpus;
            if (s.AudioOpus && !OpusSupport.Available) Log.Warn("Opus was turned on but cannot be decoded: " + OpusSupport.UnavailableReason + ". Tablets are offered PCM only.");
            try { FormatsChanged?.Invoke(); } catch (Exception ex) { Log.Warn("A format change listener failed: " + ex.Message); }
        }

        public void Dispose()
        {
            Receiver.TargetLearned -= OnTargetLearned;
            try { (talkMonitor as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Stopping the talk watch failed: " + ex.Message); }
            try { Receiver.Dispose(); } catch (Exception ex) { Log.Warn("Stopping the audio receiver failed: " + ex.Message); }
            try { (output as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Stopping the audio output failed: " + ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object CreateTalkMonitor(Func<RigPlaySettings> settings, AudioPipeline owner)
        {
            var monitor = new TalkMonitor(settings);
            monitor.TalkingChanged += owner.OnTalkingChanged;
            return monitor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object CreateOutput(Func<RigPlaySettings> settings, AudioReceiver receiver)
        {
            var created = new AudioOutput(settings);
            created.ErrorReported += receiver.ReportError;
            receiver.OutputStatus = created.Status;
            return created;
        }

        /// <summary>Forwards the receiver's sink calls to the output once it exists (and drops them when it does not).</summary>
        private sealed class SinkProxy : IAudioSink
        {
            private readonly AudioPipeline owner;

            public SinkProxy(AudioPipeline owner)
            {
                this.owner = owner;
            }

            public void StreamActivated(AudioStream stream)
            {
                (owner.output as IAudioSink)?.StreamActivated(stream);
            }

            public void StreamDeactivated(AudioStream stream)
            {
                (owner.output as IAudioSink)?.StreamDeactivated(stream);
            }
        }
    }
}
