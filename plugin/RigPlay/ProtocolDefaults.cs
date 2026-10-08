// SPDX-License-Identifier: GPL-3.0-only
// ProtocolDefaults.cs: the constants of the rigPlay protocol between the plugin and a tablet. docs/protocol.md
// owns these numbers (§2 ports, §5.1 framing, §7 versions, §8 PIN, §9 liveness); keep the two in step.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
namespace RigPlayPlugin
{
    public static class ProtocolDefaults
    {
        /// <summary>UDP broadcast, plugin → LAN: the beacon. Fixed; not configurable (spec §2).</summary>
        public const int DiscoveryPort = 23710;

        /// <summary>TCP, plugin listens: the control channel. Configurable, advertised in the beacon.</summary>
        public const int ControlPort = 23711;

        /// <summary>UDP, plugin listens: the tablet's CarPlay audio. Configurable, advertised in the beacon and state.</summary>
        public const int AudioPort = 23712;

        /// <summary>UDP on the tablet, reserved for the PC microphone (micStart/micStop). Not used in protocol 1.</summary>
        public const int MicPort = 23713;

        /// <summary>SimHub's web dash server when SimHub's settings say nothing else (spec §11).</summary>
        public const int WebDashPort = 8888;

        /// <summary>Lowest port a user may configure; below this are the privileged ports.</summary>
        public const int MinPort = 1024;

        public const int MaxPort = 65535;

        /// <summary>Highest protocol version this plugin speaks.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Lowest protocol version this plugin speaks.</summary>
        public const int MinProtocolVersion = 1;

        /// <summary>Maximum control-channel line length in bytes, terminator excluded (spec §5.1).</summary>
        public const int MaxLineBytes = 65536;

        /// <summary>Maximum beacon datagram size in bytes (spec §4.1).</summary>
        public const int MaxBeaconBytes = 1024;

        /// <summary>Beacon period and heartbeat period, milliseconds (spec §4, §9).</summary>
        public const int BeaconIntervalMs = 1000;
        public const int HeartbeatIntervalMs = 1000;

        /// <summary>Link lost after this long without a received line (spec §9).</summary>
        public const int WatchdogMs = 5000;

        /// <summary>A connection that has not sent hello within this long of accept is closed (spec §9).</summary>
        public const int HelloTimeoutMs = 5000;

        /// <summary>An Unpaired session that has not sent a pairRequest within this long of welcome is closed (spec §9).</summary>
        public const int PairRequestTimeoutMs = 10000;

        /// <summary>PIN rules (spec §8).</summary>
        public const int PinValiditySec = 120;
        public const int PinMaxAttempts = 3;
        public const int PairStartsPerMinute = 5;
    }
}
