// SPDX-License-Identifier: GPL-3.0-only
// AudioGlue.cs: connects the audio receiver (#24) to the control server's sessions (#20), spec §6.6 and §10.1.
// - state.audio: enabled while the receiver's UDP port is bound, even when no output device could be opened (the
//   tablet then streams and the page shows "received, not played"); port and formats come from the receiver
//   (opus first while its Opus setting is on and Concentus is present, §10.4).
// - audioStart / audioStop of a Paired session start and stop that tablet's stream; datagrams are accepted only
//   from the IP of a Paired session (SourceFilter), and a stream only from the tablet that started it.
// - A Paired session closing stops the streams of that tablet. When it was the last Paired session, every stream
//   stops (OnLinkLost), so nothing started without an owner can keep playing.
// No SimHub, NAudio or WPF types (compiled into RigPlay.Tests).
using System;
using System.Net;

namespace RigPlayPlugin.Audio
{
    public sealed class AudioGlue : IDisposable
    {
        private readonly RigPlayHost host;
        private readonly AudioReceiver receiver;
        private bool attached;

        /// <summary>Wires <paramref name="receiver"/> to <paramref name="host"/> and pushes the new state.audio to paired tablets.</summary>
        public AudioGlue(RigPlayHost host, AudioReceiver receiver)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

            receiver.AutoStartOnFirstFlag = false;
            receiver.SourceFilter = host.IsPairedAddress;
            host.AudioEnabled = () => receiver.Listening;
            host.AudioPort = () => receiver.Listening ? receiver.BoundPort : (int?)null;
            host.AudioFormats = receiver.SupportedFormats;
            host.AudioStart += OnAudioStart;
            host.AudioStop += OnAudioStop;
            host.SessionLost += OnSessionLost;
            attached = true;
            host.PushState();
        }

        /// <summary>The receiver was rebound (audio port changed) or its formats changed (Opus setting): tablets get the new state.audio.</summary>
        public void ListenerChanged()
        {
            if (attached) host.PushState();
        }

        private void OnAudioStart(string stream, string format, int sampleRate, int channels, IPAddress source)
        {
            receiver.OnAudioStart(stream, format, sampleRate, channels, source);
        }

        private void OnAudioStop(string stream, IPAddress source)
        {
            receiver.OnAudioStop(stream, source);
        }

        private void OnSessionLost(IPAddress source)
        {
            // The host raises this after removing the session, so PairedAddresses no longer holds it.
            if (host.PairedAddresses.Count == 0) receiver.OnLinkLost();
            else receiver.OnSourceLost(source);
        }

        public void Dispose()
        {
            if (!attached) return;
            attached = false;
            host.AudioStart -= OnAudioStart;
            host.AudioStop -= OnAudioStop;
            host.SessionLost -= OnSessionLost;
            host.AudioEnabled = () => false;
            host.AudioPort = null;
            host.AudioFormats = null;
            receiver.SourceFilter = address => false;
            // No PushState: this runs in End, right before the sessions get `shutdown`.
        }
    }
}
