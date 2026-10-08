// SPDX-License-Identifier: GPL-3.0-only
// PairingIntegrationTests.cs: pairing end to end through RigPlayHost with a fake tablet over loopback TCP (#21
// acceptance): the right PIN pairs, a wrong one is refused, the token resumes without a PIN, Deny and Forget reach
// the tablet, and an unpaired tablet gets no state.
using System;
using System.Linq;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public sealed class PairingIntegrationTests : IDisposable
    {
        private readonly RigPlaySettings settings = new RigPlaySettings().Normalize();
        private readonly RigPlayHost host;
        private int saves;

        public PairingIntegrationTests()
        {
            host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0", MachineName = "RIG-PC", SaveSettings = () => saves++ },
                null, ControlServerTests.FastNoWatchdog)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                // The web dash probe's first result pushes a state to paired sessions at an arbitrary moment (and it
                // would probe a real port 8888 on this machine). These tests assert on the exact sequence of lines a
                // paired tablet receives, so a state from the probe would interleave at random; DashboardTests covers it.
                ProbeEnabled = false,
            };
            host.Start();
            Assert.True(host.Server.Status.Listening, host.Server.Status.Error);
        }

        public void Dispose()
        {
            host.Stop();
        }

        private FakeTablet Connect()
        {
            return new FakeTablet(host.Server.Port);
        }

        private string ShownPin()
        {
            Assert.True(FakeTablet.WaitFor(() => host.Pairing.Pending.Count == 1));
            return host.Pairing.Pending.Single().Pin;
        }

        private string PairWithPin(FakeTablet t)
        {
            Assert.Equal(settings.HostId, t.Hello().HostId);
            t.Send(new PairRequestMessage());
            Assert.Equal(PairReasons.PinRequired, t.Expect<PairResultMessage>().Reason);
            t.Send(new PairRequestMessage { Pin = ShownPin() });
            var ok = t.Expect<PairResultMessage>();
            Assert.True(ok.Ok);
            t.Expect<StateMessage>();
            return ok.Token;
        }

        [Fact]
        public void TheRightPinPairsAWrongOneIsRefusedAndTheTokenResumes()
        {
            string token;
            using (var t = Connect())
            {
                t.Hello();
                t.Send(new PairRequestMessage());
                Assert.Equal(PairReasons.PinRequired, t.Expect<PairResultMessage>().Reason);
                var pin = ShownPin();
                var wrong = pin == "000000" ? "000001" : "000000";
                t.Send(new PairRequestMessage { Pin = wrong });
                var refused = t.Expect<PairResultMessage>();
                Assert.Equal(PairReasons.WrongPin, refused.Reason);
                Assert.Equal(2, refused.AttemptsLeft);
                t.Send(new PairRequestMessage { Pin = pin });
                var ok = t.Expect<PairResultMessage>();
                Assert.True(ok.Ok);
                token = ok.Token;
                var state = t.Expect<StateMessage>();
                Assert.NotNull(state.Audio);
                Assert.Equal(settings.AudioPort, state.Audio.Port);
            }
            Assert.Equal("Lenovo Tab P11", settings.FindTablet(FakeTablet.DefaultId).Name);
            Assert.True(saves > 0);

            using (var again = Connect())
            {
                again.Hello();
                again.Send(new PairRequestMessage { Token = token });
                Assert.True(again.Expect<PairResultMessage>().Ok);
                again.Expect<StateMessage>();
                Assert.Empty(host.Pairing.Pending);
            }
        }

        [Fact]
        public void DenyOnThePcSendsDenied()
        {
            using (var t = Connect())
            {
                t.Hello();
                t.Send(new PairRequestMessage());
                t.Expect<PairResultMessage>();
                ShownPin();
                host.DenyPairing(FakeTablet.DefaultId);
                Assert.Equal(PairReasons.Denied, t.Expect<PairResultMessage>().Reason);
                t.Send(new PairRequestMessage { Pin = "123456" });
                Assert.Equal(PairReasons.PinExpired, t.Expect<PairResultMessage>().Reason);
            }
        }

        [Fact]
        public void ForgetClosesTheLiveSessionWithForgotten()
        {
            using (var t = Connect())
            {
                var token = PairWithPin(t);
                host.ForgetTablet(FakeTablet.DefaultId);
                Assert.True(t.ExpectError(ErrorCodes.Forgotten).Fatal == true);
                t.ExpectClosed();
                Assert.Empty(settings.PairedTablets);

                using (var again = Connect())
                {
                    again.Hello();
                    again.Send(new PairRequestMessage { Token = token });
                    Assert.Equal(PairReasons.TokenInvalid, again.Expect<PairResultMessage>().Reason);
                }
            }
        }

        [Fact]
        public void AnUnpairedTabletGetsNoStateAndItsStatusIsRefused()
        {
            using (var t = Connect())
            {
                t.Hello();
                t.Send(new PairRequestMessage { Token = "made-up" });
                Assert.Equal(PairReasons.TokenInvalid, t.Expect<PairResultMessage>().Reason);
                host.PushState();
                t.Send(new StatusMessage { PhoneConnected = true, Screen = Screens.CarPlay });
                Assert.Equal(MessageTypes.Status, t.ExpectError(ErrorCodes.NotPaired).RefType);
                Assert.Empty(host.Server.PairedSessions);
            }
        }

        [Fact]
        public void AudioHooksSeeOnlyPairedSessions()
        {
            string started = null;
            System.Net.IPAddress from = null, lost = null;
            host.AudioStart += (stream, format, rate, channels, ip) => { started = stream + "/" + format + "/" + rate + "/" + channels; from = ip; };
            host.SessionLost += ip => lost = ip;
            Assert.False(host.IsPairedAddress(System.Net.IPAddress.Loopback));
            using (var t = Connect())
            {
                PairWithPin(t);
                Assert.True(host.IsPairedAddress(System.Net.IPAddress.Loopback));
                Assert.Contains(System.Net.IPAddress.Loopback, host.PairedAddresses);
                t.SendLine("{\"type\":\"audioStart\",\"stream\":\"telephony\",\"format\":\"pcm_s16le\",\"sampleRate\":16000,\"channels\":1}");
                Assert.True(FakeTablet.WaitFor(() => started != null));
                Assert.Equal("telephony/pcm_s16le/16000/1", started);
                Assert.Equal(System.Net.IPAddress.Loopback, from);
            }
            Assert.True(FakeTablet.WaitFor(() => lost != null));
            Assert.False(host.IsPairedAddress(System.Net.IPAddress.Loopback));
        }

        [Fact]
        public void APendingPinDisappearsWhenItsSessionCloses()
        {
            using (var t = Connect())
            {
                t.Hello();
                t.Send(new PairRequestMessage());
                t.Expect<PairResultMessage>();
                ShownPin();
            }
            Assert.True(FakeTablet.WaitFor(() => host.Pairing.Pending.Count == 0));
        }
    }
}
