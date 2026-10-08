// SPDX-License-Identifier: GPL-3.0-only
// GpsStrategyTests.cs: the fake-GPS strategies of #42 (fixed origin) and #43 (dead reckoning), docs/protocol.md §6.9.
using RigPlayPlugin.Protocol;
using RigPlayPlugin.Telemetry;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public partial class GpsStrategyTests
    {
        // Strategy A: fixed origin (#42)

        [Fact]
        public void TheFixedStrategySendsTheOriginWithTheSimsSpeedAndHeading()
        {
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Fixed, OriginLat = 48.8584, OriginLon = 2.2945, OriginAlt = 35 };
            var sampler = new TelemetrySampler();
            Assert.Null(sampler.Build(settings, 0.0).Lat); // no frame yet: no game, no position

            var f = TelemetryTests.Frame(kmh: 72, yaw: 0);
            sampler.Update(ref f, 1.0);
            var m = sampler.Build(settings, 1.0);
            Assert.Equal(48.8584, m.Lat);
            Assert.Equal(2.2945, m.Lon);
            Assert.Equal(35.0, m.Alt);
            Assert.Equal(20.0, m.SpeedMps);
            // No yaw from the game (0 so far): the heading is 0 so the position has a course.
            Assert.Equal(0.0, m.Heading);

            // Driving does not move it; yaw turns it.
            for (var i = 1; i <= 120; i++)
            {
                f.YawDeg = 90;
                sampler.Update(ref f, 1.0 + i / 60.0);
            }
            m = sampler.Build(settings, 3.0);
            Assert.Equal(48.8584, m.Lat);
            Assert.Equal(2.2945, m.Lon);
            Assert.Equal(90.0, m.Heading);

            // The position is sent even with the heading switch off, and survives the codec.
            settings.SendHeading = false;
            m = sampler.Build(settings, 3.0);
            Assert.Null(m.Heading);
            var decoded = (TelemetryMessage)MessageCodec.Decode(MessageCodec.Encode(m));
            Assert.Equal(48.8584, decoded.Lat);

            // A new origin applies to the next message.
            settings.OriginLat = -33.8568;
            settings.OriginLon = 151.2153;
            Assert.Equal(-33.8568, sampler.Build(settings, 3.0).Lat);

            // Off again: no position, no made-up heading.
            settings.GpsStrategy = GpsStrategies.Off;
            settings.SendHeading = true;
            var off = sampler.Build(settings, 3.0);
            Assert.Null(off.Lat);
            Assert.Null(off.Lon);
            Assert.Null(off.Alt);
            Assert.Equal(90.0, off.Heading);
        }

        [Fact]
        public void TheStrategyIsRebuiltOnlyWhenItsSettingsChange()
        {
            // Found on the VM: with the strategy off, every message rebuilt (and logged) the strategy.
            var settings = new TelemetrySettings();
            var sampler = new TelemetrySampler();
            var f = TelemetryTests.Frame();
            sampler.Update(ref f, 1.0);
            for (var i = 0; i < 5; i++) sampler.Build(settings, 1.0);
            settings.OriginLat = 10; // irrelevant while off
            sampler.Build(settings, 1.0);
            Assert.Equal(0, sampler.StrategyChanges);
            settings.GpsStrategy = GpsStrategies.DeadReckoning;
            for (var i = 0; i < 3; i++) sampler.Build(settings, 1.0);
            Assert.Equal(1, sampler.StrategyChanges);
            settings.OriginLat = 11;
            sampler.Build(settings, 1.0);
            sampler.Build(settings, 1.0);
            Assert.Equal(2, sampler.StrategyChanges);
            settings.GpsStrategy = GpsStrategies.Off;
            sampler.Build(settings, 1.0);
            sampler.Build(settings, 1.0);
            Assert.Equal(3, sampler.StrategyChanges);
        }

        [Theory]
        [InlineData("50.3356, 6.9475", 50.3356, 6.9475)]
        [InlineData("50.3356 6.9475", 50.3356, 6.9475)]
        [InlineData(" -33.8568,151.2153 ", -33.8568, 151.2153)]
        [InlineData("1e1, -1.5", 10, -1.5)]
        public void APastedLatLonIsRead(string text, double lat, double lon)
        {
            double a, b;
            Assert.True(GeoText.TryParseLatLon(text, out a, out b));
            Assert.Equal(lat, a, 9);
            Assert.Equal(lon, b, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData("50.3356")]
        [InlineData("91, 0")]
        [InlineData("0, 181")]
        [InlineData("a, b")]
        [InlineData("1, 2, 3")]
        [InlineData("NaN, 0")]
        public void ABadLatLonIsRefused(string text)
        {
            double a, b;
            Assert.False(GeoText.TryParseLatLon(text, out a, out b));
        }

        [Fact]
        public void TheOriginIsRepairedWhenOutOfRange()
        {
            var s = new TelemetrySettings { OriginLat = 95, OriginLon = 10, OriginAlt = double.NaN }.Normalize();
            Assert.Equal(TelemetrySettings.DefaultOriginLat, s.OriginLat);
            Assert.Equal(TelemetrySettings.DefaultOriginLon, s.OriginLon);
            Assert.Equal(TelemetrySettings.DefaultOriginAlt, s.OriginAlt);
            var kept = new TelemetrySettings { GpsStrategy = GpsStrategies.Fixed, OriginLat = -89.5, OriginLon = -179.5, OriginAlt = -20 }.Normalize();
            Assert.Equal(GpsStrategies.Fixed, kept.GpsStrategy);
            Assert.Equal(-89.5, kept.OriginLat);
            Assert.Equal(-179.5, kept.OriginLon);
            Assert.Equal(-20.0, kept.OriginAlt);
        }
    }
}
