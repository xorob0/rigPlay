// SPDX-License-Identifier: GPL-3.0-only
// AudioGlueTests.cs: the audio receiver wired to the control server (#20, #24): state.audio reflects the bound
// receiver, audioStart / audioStop of a paired tablet drive its stream, audio is accepted only from paired IPs
// and only from the tablet that started the stream, and a closing session stops that tablet's streams.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RigPlayPlugin.Audio;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public sealed class AudioGlueTests : IDisposable
    {
        private sealed class Sink : IAudioSink
        {
            public readonly List<AudioStream> Active = new List<AudioStream>();
            public void StreamActivated(AudioStream stream) { lock (Active) Active.Add(stream); }
            public void StreamDeactivated(AudioStream stream) { lock (Active) Active.Remove(stream); }
            public int Count { get { lock (Active) return Active.Count; } }
        }

        private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.50");

        private readonly RigPlaySettings settings = new RigPlaySettings().Normalize();
        private readonly RigPlayHost host;
        private readonly Sink sink = new Sink();
        private readonly AudioReceiver receiver;

        public AudioGlueTests()
        {
            host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0", MachineName = "RIG-PC" }, null, ControlServerTests.FastNoWatchdog)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                ProbeEnabled = false,
            };
            host.Start();
            receiver = new AudioReceiver(0, sink);
            receiver.Start();
            Assert.True(receiver.Listening);
        }

        public void Dispose()
        {
            receiver.Dispose();
            host.Stop();
        }

        private StateMessage Pair(FakeTablet t, string tabletId = FakeTablet.DefaultId)
        {
            t.Hello(tabletId);
            t.Send(new PairRequestMessage());
            Assert.Equal(PairReasons.PinRequired, t.Expect<PairResultMessage>().Reason);
            Assert.True(FakeTablet.WaitFor(() => host.Pairing.Pending.Any(p => p.TabletId == tabletId)));
            t.Send(new PairRequestMessage { Pin = host.Pairing.Pending.Single(p => p.TabletId == tabletId).Pin });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            return t.Expect<StateMessage>();
        }

        private static byte[] Datagram(int seq, bool start)
        {
            return AudioHeader.Create((ushort)seq, AudioStreamType.Media, start, (uint)(seq * 240), 48000, 2).Encode(new short[480]);
        }

        [Fact]
        public void AttachingEnablesAudioInTheStateOfPairedTablets()
        {
            using (var t = new FakeTablet(host.Server.Port))
            {
                var before = Pair(t);
                Assert.False(before.Audio.Enabled);

                using (new AudioGlue(host, receiver))
                {
                    var state = t.Expect<StateMessage>();
                    Assert.True(state.Audio.Enabled);
                    Assert.Equal(receiver.BoundPort, state.Audio.Port);
                    Assert.Equal(new List<string> { "pcm_s16le" }, state.Audio.Formats);
                    Assert.False(receiver.AutoStartOnFirstFlag);
                }
            }
        }

        [Fact]
        public void OpusIsOfferedFirstOnlyWhileTheSettingIsOnAndAChangeIsPushed()
        {
            Assert.True(OpusSupport.Available);
            receiver.OpusEnabled = true;
            using (var t = new FakeTablet(host.Server.Port))
            {
                Pair(t);
                using (var glue = new AudioGlue(host, receiver))
                {
                    var state = t.Expect<StateMessage>();
                    Assert.Equal(new List<string> { "opus", "pcm_s16le" }, state.Audio.Formats);

                    // The setting goes off (AudioPipeline.ApplyOpus): the next state lists PCM only.
                    receiver.OpusEnabled = false;
                    glue.ListenerChanged();
                    Assert.Equal(new List<string> { "pcm_s16le" }, t.Expect<StateMessage>().Audio.Formats);

                    // And an opus audioStart is refused while it is off.
                    t.Send(new AudioStartMessage { Stream = "media", Format = "opus", SampleRate = 48000, Channels = 2 });
                    t.Send(new AudioStartMessage { Stream = "alt", Format = "pcm_s16le", SampleRate = 48000, Channels = 2 });
                    Assert.True(FakeTablet.WaitFor(() => receiver.IsStarted(AudioStreamType.Alt)));
                    Assert.False(receiver.IsStarted(AudioStreamType.Media));
                }
            }
        }

        [Fact]
        public void APairedTabletsStreamPlaysAndOtherSourcesAreDropped()
        {
            using (new AudioGlue(host, receiver))
            {
                // Before pairing, nothing is accepted from loopback, not even a start-flagged datagram.
                receiver.ProcessDatagram(Datagram(0, true), Datagram(0, true).Length, IPAddress.Loopback);
                Assert.False(receiver.IsStarted(AudioStreamType.Media));

                using (var t = new FakeTablet(host.Server.Port))
                {
                    Assert.True(Pair(t).Audio.Enabled);
                    t.Send(new AudioStartMessage { Stream = "media", Format = "pcm_s16le", SampleRate = 48000, Channels = 2 });
                    Assert.True(FakeTablet.WaitFor(() => receiver.IsStarted(AudioStreamType.Media)));
                    Assert.Equal(IPAddress.Loopback, receiver.GetStream(AudioStreamType.Media).Source);

                    var unpaired = Datagram(0, true);
                    receiver.ProcessDatagram(unpaired, unpaired.Length, Lan);
                    Assert.Equal(0, sink.Count);

                    var paired = Datagram(0, true);
                    receiver.ProcessDatagram(paired, paired.Length, IPAddress.Loopback);
                    Assert.Equal(1, sink.Count);

                    t.Send(new AudioStopMessage { Stream = "media" });
                    Assert.True(FakeTablet.WaitFor(() => !receiver.IsStarted(AudioStreamType.Media)));
                    Assert.Equal(0, sink.Count);

                    t.Send(new AudioStartMessage { Stream = "media", Format = "pcm_s16le", SampleRate = 48000, Channels = 2 });
                    Assert.True(FakeTablet.WaitFor(() => receiver.IsStarted(AudioStreamType.Media)));
                }
                // The last paired session closed: every stream stops.
                Assert.True(FakeTablet.WaitFor(() => !receiver.IsStarted(AudioStreamType.Media)));
            }
        }

        [Fact]
        public void AStreamBelongsToTheTabletThatStartedIt()
        {
            var a = IPAddress.Parse("192.168.1.21");
            var b = IPAddress.Parse("192.168.1.22");
            Assert.True(receiver.OnAudioStart("media", "pcm_s16le", 48000, 2, a));
            Assert.True(receiver.OnAudioStart("alt", "pcm_s16le", 24000, 1, b));

            var fromB = Datagram(0, true);
            receiver.ProcessDatagram(fromB, fromB.Length, b);
            Assert.Equal(0, sink.Count); // b did not start media

            receiver.OnAudioStop("media", b);
            Assert.True(receiver.IsStarted(AudioStreamType.Media));

            receiver.OnSourceLost(b);
            Assert.True(receiver.IsStarted(AudioStreamType.Media));
            Assert.False(receiver.IsStarted(AudioStreamType.Alt));

            receiver.OnSourceLost(a.MapToIPv6());
            Assert.False(receiver.IsStarted(AudioStreamType.Media));
        }
    }
}
