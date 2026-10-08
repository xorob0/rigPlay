// SPDX-License-Identifier: GPL-3.0-only
// TelemetrySender.cs: the 10 Hz timer that sends `telemetry` (docs/protocol.md §6.9). A message goes to every Paired
// session that has feature `telemetry` and whose last status says a phone is connected, while the master switch is on,
// at least one field is enabled and a game runs. When the game stops it sends one last message with
// gameRunning: false, then nothing until the game runs again.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Telemetry
{
    public sealed class TelemetrySender : IDisposable
    {
        /// <summary>100 ms: at most 10 messages per second (spec §6.9).</summary>
        public const int IntervalMs = 100;

        private readonly Func<ControlServer> server;
        private readonly Func<RigPlaySettings> settings;
        private readonly object tickLock = new object();
        private Timer timer;
        private bool streaming;

        public TelemetrySender(TelemetrySampler sampler, Func<ControlServer> server, Func<RigPlaySettings> settings)
        {
            Sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            this.server = server ?? throw new ArgumentNullException(nameof(server));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public TelemetrySampler Sampler { get; }

        /// <summary>Messages sent (one per session per tick), for the page.</summary>
        public long MessagesSent { get; private set; }

        /// <summary>The last message sent, encoded; null before the first.</summary>
        public string LastMessage { get; private set; }

        /// <summary>Sessions the last tick sent to.</summary>
        public int Targets { get; private set; }

        /// <summary>Why nothing is being sent, for the page; null while sending.</summary>
        public string Idle { get; private set; } = "Not started";

        public void Start()
        {
            lock (tickLock)
            {
                if (timer != null) return;
                timer = new Timer(_ => SafeTick(), null, IntervalMs, IntervalMs);
            }
        }

        public void Stop()
        {
            lock (tickLock)
            {
                timer?.Dispose();
                timer = null;
                streaming = false;
                Idle = "Not started";
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>The sessions that get telemetry now (spec §6.9).</summary>
        public static List<ClientSession> Eligible(ControlServer server)
        {
            if (server == null) return new List<ClientSession>();
            return server.PairedSessions
                .Where(s => s.HasFeature(Features.Telemetry) && s.LastStatus != null && s.LastStatus.PhoneConnected)
                .ToList();
        }

        private void SafeTick()
        {
            try { Tick(); } catch (Exception ex) { PluginLog.Error("Sending telemetry failed", ex); }
        }

        /// <summary>One timer tick; returns how many sessions got a message. Tests call it directly.</summary>
        public int Tick()
        {
            // Under tickLock: decide streaming/idle, pick the targets and build the message, then encode it exactly
            // once. The network I/O (session.Send, which can block on a stalled tablet) happens after the lock, so a
            // slow send cannot hold the next tick or Stop() behind it. The streaming flag flips under the lock, so two
            // concurrent ticks never emit two gameRunning: false.
            List<ClientSession> targets;
            string encoded;
            lock (tickLock)
            {
                var s = settings()?.Telemetry ?? new TelemetrySettings();
                if (!s.Enabled) return Quiet("Off");
                if (!s.AnyFieldEnabled()) return Quiet("No field is switched on");
                targets = Eligible(server());
                if (targets.Count == 0) return Quiet("No paired tablet with a connected iPhone asks for telemetry");

                var message = Sampler.Build(s);
                if (message.GameRunning != true)
                {
                    if (!streaming) return Quiet("No game running");
                    // The game stopped: one last message so the tablet stops at once instead of waiting for staleness.
                    streaming = false;
                    Idle = "No game running";
                    message = new TelemetryMessage { GameRunning = false };
                }
                else
                {
                    streaming = true;
                    Idle = null;
                }
                encoded = MessageCodec.Encode(message);
                LastMessage = encoded;
            }
            return Send(targets, encoded);
        }

        private int Quiet(string why)
        {
            streaming = false;
            Idle = why;
            Targets = 0;
            return 0;
        }

        private int Send(List<ClientSession> targets, string encoded)
        {
            var sent = 0;
            foreach (var session in targets)
            {
                if (session.SendEncoded(encoded)) sent++;
            }
            MessagesSent += sent;
            Targets = sent;
            return sent;
        }
    }
}
