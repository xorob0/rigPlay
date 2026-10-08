// SPDX-License-Identifier: GPL-3.0-only
// RigPlaySettingsTests.cs: defaults, Normalize() repairs, and the Newtonsoft.Json round trip SimHub's
// ReadCommonSettings / SaveCommonSettings put the settings through.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class RigPlaySettingsTests
    {
        private static PairedTablet Tablet(string id, string name, string token, DateTime pairedAt)
        {
            return new PairedTablet { Id = id, Name = name, TokenHash = string.IsNullOrWhiteSpace(token) ? token : RigPlayPlugin.Pairing.PairingTokens.Hash(token.Trim()), PairedAt = pairedAt };
        }

        [Fact]
        public void DefaultsUseTheProtocolPortsAndAreAlreadyNormal()
        {
            var settings = new RigPlaySettings();
            Assert.Equal(ProtocolDefaults.ControlPort, settings.ControlPort);
            Assert.Equal(ProtocolDefaults.DiscoveryPort, settings.DiscoveryPort);
            Assert.Equal(ProtocolDefaults.AudioPort, settings.AudioPort);
            Assert.Equal("", settings.SelectedDashboard);
            Assert.Equal("", settings.IdleDashboard);
            Assert.Equal("", settings.AudioDeviceId);
            Assert.Equal(RigPlaySettings.DefaultVolume, settings.Volume);
            Assert.False(settings.Muted);
            Assert.False(settings.AudioOpus); // PCM is the default; Opus is opt-in (spec §10.4)
            Assert.Empty(settings.PairedTablets);
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, settings.SchemaVersion);

            var before = JsonConvert.SerializeObject(settings);
            settings.Normalize();
            Assert.Equal(before, JsonConvert.SerializeObject(settings));
        }

        [Theory]
        [InlineData(0, 0, RigPlaySettings.MinAudioBufferMs, 0)]
        [InlineData(80, 750, 80, 750)]
        [InlineData(5000, 9000, RigPlaySettings.MaxAudioBufferMs, RigPlaySettings.MaxAudioBufferMs)]
        [InlineData(-5, -1, RigPlaySettings.MinAudioBufferMs, 0)]
        public void TheAudioBufferSettingsAreClamped(int minimum, int learned, int expectedMinimum, int expectedLearned)
        {
            var settings = new RigPlaySettings { AudioBufferMs = minimum, LearnedAudioBufferMs = learned }.Normalize();
            Assert.Equal(expectedMinimum, settings.AudioBufferMs);
            Assert.Equal(expectedLearned, settings.LearnedAudioBufferMs);
            Assert.Equal(RigPlayPlugin.Audio.JitterBuffer.DefaultTargetMs, new RigPlaySettings().AudioBufferMs);
            Assert.Equal(0, new RigPlaySettings().LearnedAudioBufferMs);
        }

        [Fact]
        public void TheTalkWatchDefaultsToCrewChiefLoweringTheMusicToAQuarter()
        {
            var settings = new RigPlaySettings();
            Assert.True(settings.TalkWatchEnabled);
            Assert.Equal(new[] { "CrewChiefV4" }, settings.TalkProcesses);
            Assert.Equal(RigPlaySettings.TalkModeDuck, settings.TalkMode);
            Assert.Equal(25, settings.TalkDuckVolume);
        }

        [Fact]
        public void TalkWatchValuesAreRepaired()
        {
            var settings = new RigPlaySettings
            {
                TalkProcesses = new List<string> { " CrewChiefV4.exe ", "discord", "DISCORD.EXE", "", "  ", null },
                TalkMode = "mute",
                TalkDuckVolume = 140,
            }.Normalize();
            Assert.Equal(new[] { "CrewChiefV4", "discord" }, settings.TalkProcesses);
            Assert.Equal(RigPlaySettings.TalkModeDuck, settings.TalkMode);
            Assert.Equal(100, settings.TalkDuckVolume);
            Assert.Equal(RigPlaySettings.TalkModePause, new RigPlaySettings { TalkMode = "pause", TalkDuckVolume = -3 }.Normalize().TalkMode);
            Assert.Equal(0, new RigPlaySettings { TalkDuckVolume = -3 }.Normalize().TalkDuckVolume);
            Assert.Empty(new RigPlaySettings { TalkProcesses = null }.Normalize().TalkProcesses);
            Assert.Equal("CrewChiefV4", RigPlaySettings.NormalizeProcessName("  crewchiefv4.EXE ".Replace("crewchiefv4", "CrewChiefV4")));
        }

        [Fact]
        public void TheThreeDefaultPortsAreDistinctAndUnprivileged()
        {
            var ports = new[] { ProtocolDefaults.ControlPort, ProtocolDefaults.DiscoveryPort, ProtocolDefaults.AudioPort };
            Assert.Equal(3, new HashSet<int>(ports).Count);
            Assert.All(ports, p => Assert.InRange(p, ProtocolDefaults.MinPort, ProtocolDefaults.MaxPort));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(80)]
        [InlineData(1023)]
        [InlineData(65536)]
        [InlineData(-1)]
        public void AnOutOfRangePortFallsBackToItsDefault(int port)
        {
            var settings = new RigPlaySettings { ControlPort = port, DiscoveryPort = port, AudioPort = port }.Normalize();
            Assert.Equal(ProtocolDefaults.ControlPort, settings.ControlPort);
            Assert.Equal(ProtocolDefaults.DiscoveryPort, settings.DiscoveryPort);
            Assert.Equal(ProtocolDefaults.AudioPort, settings.AudioPort);
        }

        [Fact]
        public void AValidCustomPortIsKept()
        {
            var settings = new RigPlaySettings { ControlPort = 20000, AudioPort = 20002 }.Normalize();
            Assert.Equal(20000, settings.ControlPort);
            Assert.Equal(20002, settings.AudioPort);
        }

        [Fact]
        public void ASchema1FileMovesFromThePlaceholderPortsToTheProtocolPorts()
        {
            // The file the plugin skeleton wrote on the test VM.
            var old = JsonConvert.DeserializeObject<RigPlaySettings>(
                "{\"SchemaVersion\":1,\"ControlPort\":18877,\"DiscoveryPort\":18878,\"AudioPort\":18879,\"SelectedDashboard\":\"\",\"Volume\":90,\"PairedTablets\":[]}").Normalize();
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, old.SchemaVersion);
            Assert.Equal(23711, old.ControlPort);
            Assert.Equal(23710, old.DiscoveryPort);
            Assert.Equal(23712, old.AudioPort);
            Assert.Equal(90, old.Volume);

            var custom = new RigPlaySettings { SchemaVersion = 1, ControlPort = 20000, AudioPort = 20001 }.Normalize();
            Assert.Equal(20000, custom.ControlPort);
            Assert.Equal(20001, custom.AudioPort);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(8888, 8888)]
        [InlineData(-5, 0)]
        [InlineData(70000, 0)]
        public void TheWebDashPortIsZeroForAutomaticOrAValidPort(int port, int expected)
        {
            Assert.Equal(expected, new RigPlaySettings { WebDashPort = port }.Normalize().WebDashPort);
        }

        [Fact]
        public void TheDiscoveryPortIsFixedByTheProtocol()
        {
            Assert.Equal(23710, ProtocolDefaults.DiscoveryPort);
            Assert.Equal(23711, ProtocolDefaults.ControlPort);
            Assert.Equal(23712, ProtocolDefaults.AudioPort);
            Assert.Equal(ProtocolDefaults.DiscoveryPort, new RigPlaySettings { DiscoveryPort = 20001 }.Normalize().DiscoveryPort);
        }

        [Fact]
        public void AControlPortOnTheDiscoveryPortIsReset()
        {
            var settings = new RigPlaySettings { ControlPort = 23710, AudioPort = 20002 }.Normalize();
            Assert.Equal(ProtocolDefaults.ControlPort, settings.ControlPort);
            Assert.Equal(ProtocolDefaults.AudioPort, settings.AudioPort);
        }

        [Fact]
        public void TheHostIdIsAStableLowerCaseV4Uuid()
        {
            var settings = new RigPlaySettings().Normalize();
            Assert.True(RigPlaySettings.IsValidHostId(settings.HostId), settings.HostId);
            var id = settings.HostId;
            Assert.Equal(id, settings.Normalize().HostId);
            var copy = JsonConvert.DeserializeObject<RigPlaySettings>(JsonConvert.SerializeObject(settings)).Normalize();
            Assert.Equal(id, copy.HostId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-uuid")]
        [InlineData("3F6C2A4E-8D1B-4C7A-9E55-0B2D7F1A6C90")]
        [InlineData("3f6c2a4e-8d1b-1c7a-9e55-0b2d7f1a6c90")]
        public void ABadHostIdIsRegenerated(string bad)
        {
            var settings = new RigPlaySettings { HostId = bad }.Normalize();
            Assert.True(RigPlaySettings.IsValidHostId(settings.HostId));
            Assert.NotEqual(bad, settings.HostId);
        }

        [Fact]
        public void CollidingPortsResetAllThreeToTheDefaults()
        {
            var settings = new RigPlaySettings { ControlPort = 20000, AudioPort = 20000 }.Normalize();
            Assert.Equal(ProtocolDefaults.ControlPort, settings.ControlPort);
            Assert.Equal(ProtocolDefaults.DiscoveryPort, settings.DiscoveryPort);
            Assert.Equal(ProtocolDefaults.AudioPort, settings.AudioPort);
        }

        [Theory]
        [InlineData(-20, 0)]
        [InlineData(0, 0)]
        [InlineData(55, 55)]
        [InlineData(100, 100)]
        [InlineData(250, 100)]
        public void VolumeIsClampedToZeroToHundred(int volume, int expected)
        {
            Assert.Equal(expected, new RigPlaySettings { Volume = volume }.Normalize().Volume);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(12, 12)]
        [InlineData(30, 30)]
        [InlineData(99, 30)]
        public void TheMicrophoneBoostIsClampedToZeroToThirtyDecibels(int boost, int expected)
        {
            Assert.Equal(expected, new RigPlaySettings { MicBoostDb = boost }.Normalize().MicBoostDb);
            var defaults = new RigPlaySettings();
            Assert.Equal(RigPlayPlugin.Audio.MicGainControl.DefaultBoostDb, defaults.MicBoostDb);
            Assert.Equal(20, defaults.MicBoostDb);
            Assert.True(defaults.MicAutoBoost);
        }

        [Fact]
        public void NullStringsBecomeEmptyAndNamesAreTrimmed()
        {
            var settings = new RigPlaySettings
            {
                SelectedDashboard = "  rigPlay Default  ",
                IdleDashboard = null,
                AudioDeviceId = null,
            }.Normalize();
            Assert.Equal("rigPlay Default", settings.SelectedDashboard);
            Assert.Equal("", settings.IdleDashboard);
            Assert.Equal("", settings.AudioDeviceId);
        }

        [Fact]
        public void TabletListIsRepaired()
        {
            var early = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
            var late = early.AddDays(3);
            var settings = new RigPlaySettings
            {
                PairedTablets = new List<PairedTablet>
                {
                    null,
                    Tablet("", "No id", "t", early),
                    Tablet("no-token", "No token", "  ", early),
                    Tablet(" tab-a ", "  ", " old ", early),
                    Tablet("tab-b", "Passenger", "b", early.AddDays(1)),
                    Tablet("tab-a", "Driver", "new", late),
                },
            }.Normalize();

            Assert.Equal(2, settings.PairedTablets.Count);
            Assert.Equal("tab-b", settings.PairedTablets[0].Id);
            var a = settings.FindTablet("tab-a");
            Assert.NotNull(a);
            Assert.Equal("Driver", a.Name);
            Assert.Equal(RigPlayPlugin.Pairing.PairingTokens.Hash("new"), a.TokenHash);
            Assert.Equal(late, a.PairedAt);
        }

        [Fact]
        public void ABlankTabletNameGetsTheDefaultName()
        {
            var settings = new RigPlaySettings
            {
                PairedTablets = new List<PairedTablet> { Tablet("x", "   ", "tok", DateTime.UtcNow) },
            }.Normalize();
            Assert.Equal(RigPlaySettings.DefaultTabletName, settings.PairedTablets[0].Name);
        }

        [Fact]
        public void ANullTabletListBecomesEmpty()
        {
            Assert.Empty(new RigPlaySettings { PairedTablets = null }.Normalize().PairedTablets);
        }

        [Fact]
        public void PairedAtIsStoredAsUtc()
        {
            var unspecified = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);
            var local = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Local);
            var settings = new RigPlaySettings
            {
                PairedTablets = new List<PairedTablet> { Tablet("u", "U", "t", unspecified), Tablet("l", "L", "t", local) },
            }.Normalize();
            Assert.All(settings.PairedTablets, t => Assert.Equal(DateTimeKind.Utc, t.PairedAt.Kind));
            Assert.Equal(unspecified.Ticks, settings.FindTablet("u").PairedAt.Ticks);
            Assert.Equal(local.ToUniversalTime(), settings.FindTablet("l").PairedAt);
        }

        [Fact]
        public void AnUnknownSchemaVersionIsReset()
        {
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, new RigPlaySettings { SchemaVersion = 0 }.Normalize().SchemaVersion);
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, new RigPlaySettings { SchemaVersion = 99 }.Normalize().SchemaVersion);
        }

        [Fact]
        public void JsonRoundTripKeepsEveryField()
        {
            var pairedAt = new DateTime(2026, 9, 30, 18, 45, 12, DateTimeKind.Utc);
            var original = new RigPlaySettings
            {
                HostName = "Sim rig",
                ControlPort = 21000,
                AudioPort = 21002,
                SelectedDashboard = "rigPlay GT",
                IdleDashboard = "rigPlay Idle",
                AudioDeviceId = "{0.0.0.00000000}.{guid}",
                Volume = 42,
                Muted = true,
                PairedTablets = new List<PairedTablet> { Tablet("tab-1", "Driver tablet", "secret", pairedAt) },
            }.Normalize();

            var json = JsonConvert.SerializeObject(original, Formatting.Indented);
            var copy = JsonConvert.DeserializeObject<RigPlaySettings>(json).Normalize();

            Assert.Equal(json, JsonConvert.SerializeObject(copy, Formatting.Indented));
            Assert.Equal(21000, copy.ControlPort);
            Assert.Equal("rigPlay Idle", copy.IdleDashboard);
            Assert.Equal(42, copy.Volume);
            Assert.True(copy.Muted);
            var tablet = Assert.Single(copy.PairedTablets);
            Assert.Equal(RigPlayPlugin.Pairing.PairingTokens.Hash("secret"), tablet.TokenHash);
            Assert.DoesNotContain("secret\"", json);
            Assert.Equal(pairedAt, tablet.PairedAt);
            Assert.Equal(DateTimeKind.Utc, tablet.PairedAt.Kind);
        }

        [Fact]
        public void AnEmptyOrPartialFileReadsAsDefaults()
        {
            var empty = JsonConvert.DeserializeObject<RigPlaySettings>("{}").Normalize();
            // Every field is the default except the host id, which is new for each fresh object.
            var defaults = new RigPlaySettings { HostId = empty.HostId };
            Assert.Equal(JsonConvert.SerializeObject(defaults), JsonConvert.SerializeObject(empty));

            var partial = JsonConvert.DeserializeObject<RigPlaySettings>(
                "{\"Volume\": 900, \"PairedTablets\": null, \"ControlPort\": 5, \"SomethingFromTheFuture\": 1}").Normalize();
            Assert.Equal(100, partial.Volume);
            Assert.Empty(partial.PairedTablets);
            Assert.Equal(ProtocolDefaults.ControlPort, partial.ControlPort);
        }

        [Fact]
        public void DeserialisingDoesNotDuplicateTablets()
        {
            // Newtonsoft reuses a list the constructor created; the default list must stay empty for this to hold.
            var json = JsonConvert.SerializeObject(new RigPlaySettings
            {
                PairedTablets = new List<PairedTablet> { Tablet("a", "A", "t", DateTime.UtcNow) },
            });
            Assert.Single(JsonConvert.DeserializeObject<RigPlaySettings>(json).PairedTablets);
        }
    }
}
