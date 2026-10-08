// SPDX-License-Identifier: GPL-3.0-only
// DiscoveryBeacon.cs: the UDP beacon of spec §4. Every second it sends the beacon JSON to the directed broadcast
// address of every up, non-loopback IPv4 interface (Windows sends a limited broadcast out of one interface only)
// and to 255.255.255.255, all on the fixed discovery port 23710.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Net
{
    public sealed class BeaconStatus
    {
        public bool Running { get; set; }

        /// <summary>The last send error, or null when the last round went out.</summary>
        public string Error { get; set; }

        /// <summary>Where the last round went.</summary>
        public List<IPEndPoint> Targets { get; set; } = new List<IPEndPoint>();

        public long Sent { get; set; }
    }

    public sealed class DiscoveryBeacon : IDisposable
    {
        private const int InterfaceRefreshMs = 10000;

        private readonly Func<BeaconMessage> payload;
        private readonly int port;
        private readonly int intervalMs;
        private readonly IClock clock;
        private readonly object sync = new object();
        private UdpClient socket;
        private Timer timer;
        private List<IPAddress> broadcasts = new List<IPAddress>();
        private long broadcastsAt = long.MinValue / 2;
        private string lastLoggedError;
        private long sent;

        public DiscoveryBeacon(Func<BeaconMessage> payload, int port = ProtocolDefaults.DiscoveryPort, int intervalMs = ProtocolDefaults.BeaconIntervalMs, IClock clock = null)
        {
            this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
            this.port = port;
            this.intervalMs = intervalMs;
            this.clock = clock ?? SystemClock.Instance;
            Status = new BeaconStatus();
        }

        /// <summary>Also send to these endpoints (tests send to loopback).</summary>
        public List<IPEndPoint> ExtraTargets { get; } = new List<IPEndPoint>();

        /// <summary>Send to the interfaces' broadcast addresses and 255.255.255.255. Tests turn it off.</summary>
        public bool Broadcast { get; set; } = true;

        public BeaconStatus Status { get; private set; }

        public bool Start()
        {
            lock (sync)
            {
                if (socket != null) return true;
                try
                {
                    socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                }
                catch (Exception ex)
                {
                    Status = new BeaconStatus { Running = false, Error = "Could not open the beacon socket: " + ex.Message };
                    PluginLog.Error("Discovery beacon could not start: " + ex.Message);
                    return false;
                }
                Status = new BeaconStatus { Running = true };
                timer = new Timer(_ => SendOnce(), null, 0, intervalMs);
            }
            PluginLog.Info("Discovery beacon started on UDP " + port + " every " + intervalMs + " ms");
            return true;
        }

        public void Stop()
        {
            lock (sync)
            {
                if (socket == null) return;
                try { timer?.Dispose(); } catch { }
                timer = null;
                try { socket.Close(); } catch { }
                socket = null;
                Status = new BeaconStatus { Running = false, Sent = sent };
            }
            PluginLog.Info("Discovery beacon stopped after " + sent + " datagram(s)");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>The payload bytes: UTF-8 JSON, no trailing newline, at most 1024 bytes (spec §4.1).</summary>
        public static byte[] Encode(BeaconMessage beacon)
        {
            var bytes = Encoding.UTF8.GetBytes(MessageCodec.Encode(beacon));
            if (bytes.Length > ProtocolDefaults.MaxBeaconBytes)
            {
                // Only the name can make it long; shorten it rather than send an oversized beacon.
                var shorter = new BeaconMessage
                {
                    Name = beacon.Name.Length > 64 ? beacon.Name.Substring(0, 64) : beacon.Name,
                    HostId = beacon.HostId,
                    Version = beacon.Version,
                    SimhubVersion = beacon.SimhubVersion != null && beacon.SimhubVersion.Length > 32 ? null : beacon.SimhubVersion,
                    ControlPort = beacon.ControlPort,
                    AudioPort = beacon.AudioPort,
                    Protocol = beacon.Protocol,
                    MinProtocol = beacon.MinProtocol,
                };
                bytes = Encoding.UTF8.GetBytes(MessageCodec.Encode(shorter));
            }
            return bytes;
        }

        /// <summary>Sends one round of beacons now.</summary>
        public void SendOnce()
        {
            UdpClient s;
            lock (sync) s = socket;
            if (s == null) return;

            byte[] bytes;
            try
            {
                bytes = Encode(payload());
            }
            catch (Exception ex)
            {
                LogOnce("Building the beacon failed: " + ex.Message);
                return;
            }

            var targets = Targets();
            string error = null;
            foreach (var target in targets)
            {
                try
                {
                    s.Send(bytes, bytes.Length, target);
                    Interlocked.Increment(ref sent);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    error = "Sending the beacon to " + target + " failed: " + ex.Message;
                }
            }
            if (error != null) LogOnce(error);
            else lastLoggedError = null;
            Status = new BeaconStatus { Running = true, Error = error, Targets = targets, Sent = Interlocked.Read(ref sent) };
        }

        private List<IPEndPoint> Targets()
        {
            var list = new List<IPEndPoint>();
            if (Broadcast)
            {
                var now = clock.NowMs;
                if (now - broadcastsAt >= InterfaceRefreshMs)
                {
                    broadcasts = NetUtil.DirectedBroadcasts(NetUtil.LocalIPv4Addresses());
                    broadcastsAt = now;
                }
                list.AddRange(broadcasts.Select(a => new IPEndPoint(a, port)));
                list.Add(new IPEndPoint(IPAddress.Broadcast, port));
            }
            list.AddRange(ExtraTargets);
            return list;
        }

        private void LogOnce(string message)
        {
            if (message == lastLoggedError) return;
            lastLoggedError = message;
            PluginLog.Warn(message);
        }
    }
}
