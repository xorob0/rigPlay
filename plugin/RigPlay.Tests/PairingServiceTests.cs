// SPDX-License-Identifier: GPL-3.0-only
// PairingServiceTests.cs: the PIN and token rules of docs/protocol.md §8 and §15, on a manual clock.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using RigPlayPlugin.Pairing;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class PairingServiceTests
    {
        private const string Tab = "tablet-1";

        private readonly ManualClock clock = new ManualClock();
        private readonly RigPlaySettings settings = new RigPlaySettings().Normalize();
        private int saves;
        private readonly PairingService service;
        private string nextPin = "482913";

        public PairingServiceTests()
        {
            service = new PairingService(settings, () => saves++, clock) { PinGenerator = () => nextPin };
        }

        private string PairWithPin()
        {
            Assert.Equal(PairReasons.PinRequired, service.Start(Tab, "Lenovo").Reason);
            var ok = service.SubmitPin(Tab, "Lenovo", nextPin);
            Assert.True(ok.Ok);
            return ok.Token;
        }

        [Fact]
        public void StartAnswersPinRequiredWith120Seconds()
        {
            var r = service.Start(Tab, "Lenovo");
            Assert.False(r.Ok);
            Assert.Equal(PairReasons.PinRequired, r.Reason);
            Assert.Equal(120, r.PinExpiresInSec);
            var p = Assert.Single(service.Pending);
            Assert.Equal("482913", p.Pin);
            Assert.Equal("Lenovo", p.TabletName);
            Assert.Equal(120, service.SecondsLeft(p));
            // The PIN never goes over the network: it is not in the answer.
            Assert.DoesNotContain("482913", MessageCodec.Encode(r));
        }

        [Fact]
        public void TheRightPinPairsAndStoresOnlyTheHash()
        {
            var token = PairWithPin();
            Assert.Equal(43, token.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            var stored = Assert.Single(settings.PairedTablets);
            Assert.Equal(Tab, stored.Id);
            Assert.Equal("Lenovo", stored.Name);
            Assert.Equal(PairingTokens.Hash(token), stored.TokenHash);
            Assert.NotEqual(token, stored.TokenHash);
            Assert.Equal(clock.UtcNow, stored.PairedAt);
            Assert.True(saves >= 1);
            Assert.Empty(service.Pending);
            Assert.DoesNotContain(token, Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        }

        [Fact]
        public void ThePinIsSingleUse()
        {
            PairWithPin();
            Assert.Equal(PairReasons.PinExpired, service.SubmitPin(Tab, "Lenovo", nextPin).Reason);
        }

        [Fact]
        public void WrongPinsCountDownThenInvalidateThePin()
        {
            service.Start(Tab, "Lenovo");
            var first = service.SubmitPin(Tab, "Lenovo", "000000");
            Assert.Equal(PairReasons.WrongPin, first.Reason);
            Assert.Equal(2, first.AttemptsLeft);
            var second = service.SubmitPin(Tab, "Lenovo", "111111");
            Assert.Equal(1, second.AttemptsLeft);
            Assert.Equal(PairReasons.TooManyAttempts, service.SubmitPin(Tab, "Lenovo", "222222").Reason);
            // Even the right PIN is useless now.
            Assert.Equal(PairReasons.PinExpired, service.SubmitPin(Tab, "Lenovo", nextPin).Reason);
            Assert.Empty(settings.PairedTablets);
        }

        [Fact]
        public void ThePinExpiresAfter120Seconds()
        {
            service.Start(Tab, "Lenovo");
            clock.Advance(119999);
            Assert.Single(service.Pending);
            clock.Advance(1);
            Assert.Empty(service.Pending);
            Assert.Equal(PairReasons.PinExpired, service.SubmitPin(Tab, "Lenovo", nextPin).Reason);
        }

        [Fact]
        public void SubmittingWithoutStartIsPinExpired()
        {
            Assert.Equal(PairReasons.PinExpired, service.SubmitPin(Tab, "Lenovo", "123456").Reason);
        }

        [Fact]
        public void ANewStartReplacesThePendingPin()
        {
            service.Start(Tab, "Lenovo");
            service.SubmitPin(Tab, "Lenovo", "000000");
            nextPin = "135790";
            service.Start(Tab, "Lenovo");
            var p = Assert.Single(service.Pending);
            Assert.Equal("135790", p.Pin);
            Assert.Equal(3, p.AttemptsLeft);
            Assert.Equal(PairReasons.WrongPin, service.SubmitPin(Tab, "Lenovo", "482913").Reason);
        }

        [Fact]
        public void EachTabletHasItsOwnPin()
        {
            service.Start("a", "A");
            nextPin = "222222";
            service.Start("b", "B");
            Assert.Equal(2, service.Pending.Count);
            Assert.Equal(PairReasons.WrongPin, service.SubmitPin("a", "A", "222222").Reason);
            Assert.True(service.SubmitPin("b", "B", "222222").Ok);
        }

        [Fact]
        public void AtMostFiveStartsPerMinute()
        {
            for (var i = 0; i < 5; i++) Assert.Equal(PairReasons.PinRequired, service.Start("t" + i, "T").Reason);
            Assert.Equal(PairReasons.Denied, service.Start("t5", "T").Reason);
            clock.Advance(59999);
            Assert.Equal(PairReasons.Denied, service.Start("t5", "T").Reason);
            clock.Advance(1);
            Assert.Equal(PairReasons.PinRequired, service.Start("t5", "T").Reason);
        }

        [Fact]
        public void ResumeWithTheTokenPairsWithoutAPin()
        {
            var token = PairWithPin();
            var r = service.Resume(Tab, "Lenovo", token);
            Assert.True(r.Ok);
            Assert.Equal(token, r.Token);
        }

        [Fact]
        public void ResumeRenamesTheTablet()
        {
            var token = PairWithPin();
            service.Resume(Tab, "Driver tablet", token);
            Assert.Equal("Driver tablet", settings.FindTablet(Tab).Name);
        }

        [Fact]
        public void AWrongTokenOrAnotherTabletsTokenIsTokenInvalid()
        {
            var token = PairWithPin();
            Assert.Equal(PairReasons.TokenInvalid, service.Resume(Tab, "Lenovo", token + "x").Reason);
            Assert.Equal(PairReasons.TokenInvalid, service.Resume("other-tablet", "Other", token).Reason);
            Assert.Equal(PairReasons.TokenInvalid, service.Resume("never-seen", "X", "abc").Reason);
        }

        [Fact]
        public void DenyDiscardsThePin()
        {
            service.Start(Tab, "Lenovo");
            Assert.True(service.Deny(Tab));
            Assert.False(service.Deny(Tab));
            Assert.Equal(PairReasons.PinExpired, service.SubmitPin(Tab, "Lenovo", nextPin).Reason);
        }

        [Fact]
        public void ForgetDeletesTheHashSoTheTokenStopsWorking()
        {
            var token = PairWithPin();
            Assert.True(service.Forget(Tab));
            Assert.Empty(settings.PairedTablets);
            Assert.Equal(PairReasons.TokenInvalid, service.Resume(Tab, "Lenovo", token).Reason);
            Assert.False(service.Forget(Tab));
        }

        [Fact]
        public void PairingAgainReplacesTheOldToken()
        {
            var first = PairWithPin();
            var second = PairWithPin();
            Assert.Single(settings.PairedTablets);
            Assert.Equal(PairReasons.TokenInvalid, service.Resume(Tab, "Lenovo", first).Reason);
            Assert.True(service.Resume(Tab, "Lenovo", second).Ok);
        }

        [Fact]
        public void ChangedIsRaisedForPinsAndForget()
        {
            var count = 0;
            service.Changed += () => count++;
            service.Start(Tab, "Lenovo");
            Assert.Equal(1, count);
            service.SubmitPin(Tab, "Lenovo", nextPin);
            service.Forget(Tab);
            Assert.True(count >= 3);
        }

        [Fact]
        public void GeneratedPinsAreSixDigitsAndVary()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var pins = Enumerable.Range(0, 2000).Select(_ => PairingTokens.NewPin(rng)).ToList();
                Assert.All(pins, p => Assert.Matches("^[0-9]{6}$", p));
                Assert.True(pins.Distinct().Count() > 1900);
                // Leading zeros happen (about 10 % of PINs start with 0).
                Assert.Contains(pins, p => p[0] == '0');
            }
        }

        [Fact]
        public void TokensAreRandomAndHashedWithSha256()
        {
            Assert.NotEqual(PairingTokens.NewToken(), PairingTokens.NewToken());
            // SHA-256("abc")
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PairingTokens.Hash("abc"));
            Assert.True(PairingTokens.FixedTimeEquals("abc", "abc"));
            Assert.False(PairingTokens.FixedTimeEquals("abc", "abd"));
            Assert.False(PairingTokens.FixedTimeEquals("abc", "abcd"));
            Assert.False(PairingTokens.FixedTimeEquals(null, "abc"));
        }
    }
}
