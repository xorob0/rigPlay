// SPDX-License-Identifier: GPL-3.0-only
// DiscoveryBeaconTests.cs: the beacon payload (spec §4.3) and its 1 Hz sending, received on loopback.
using System.Net;
using System.Net.Sockets;
using System.Text;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class DiscoveryBeaconTests
    {
        private static BeaconMessage Sample(string name = "RIG-PC")
        {
            return new BeaconMessage
            {
                Name = name,
                HostId = ControlServerTests.HostId,
                Version = "0.1.0",
                SimhubVersion = "9.12.6",
                ControlPort = 23711,
                AudioPort = 23712,
                Protocol = 1,
                MinProtocol = 1,
            };
        }

        [Fact]
        public void ThePayloadIsTheBeaconJsonWithoutNewline()
        {
            var bytes = DiscoveryBeacon.Encode(Sample());
            var text = Encoding.UTF8.GetString(bytes);
            Assert.False(text.EndsWith("\n"));
            Assert.Equal((byte)'{', bytes[0]); // no byte-order mark
            var decoded = Assert.IsType<BeaconMessage>(MessageCodec.Decode(text));
            Assert.Equal(23711, decoded.ControlPort);
            Assert.Equal(ControlServerTests.HostId, decoded.HostId);
        }

        [Fact]
        public void AnOverlongNameIsShortenedToFitIn1024Bytes()
        {
            var bytes = DiscoveryBeacon.Encode(Sample(new string('n', 2000)));
            Assert.True(bytes.Length <= ProtocolDefaults.MaxBeaconBytes);
            Assert.IsType<BeaconMessage>(MessageCodec.Decode(Encoding.UTF8.GetString(bytes)));
        }

        [Fact]
        public void BeaconsArriveEverySecond()
        {
            using (var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                listener.Client.ReceiveTimeout = 3000;
                var port = ((IPEndPoint)listener.Client.LocalEndPoint).Port;
                using (var beacon = new DiscoveryBeacon(() => Sample(), port, 200) { Broadcast = false })
                {
                    beacon.ExtraTargets.Add(new IPEndPoint(IPAddress.Loopback, port));
                    Assert.True(beacon.Start());
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    for (var i = 0; i < 3; i++)
                    {
                        var datagram = listener.Receive(ref from);
                        var beaconMessage = Assert.IsType<BeaconMessage>(MessageCodec.Decode(Encoding.UTF8.GetString(datagram)));
                        Assert.Equal("RIG-PC", beaconMessage.Name);
                    }
                    Assert.True(beacon.Status.Running);
                    Assert.Null(beacon.Status.Error);
                    beacon.Stop();
                    Assert.False(beacon.Status.Running);
                }
            }
        }
    }
}
