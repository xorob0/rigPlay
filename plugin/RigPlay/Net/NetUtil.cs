// SPDX-License-Identifier: GPL-3.0-only
// NetUtil.cs: address helpers shared by the beacon, the control server and the URL builder: the LAN-only rule of
// spec §15, IPv4-mapped address normalisation (§11), and the directed broadcast addresses of the PC's interfaces
// (§4.1).
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RigPlayPlugin.Net
{
    public static class NetUtil
    {
        /// <summary>An IPv4-mapped IPv6 address as plain IPv4; anything else unchanged.</summary>
        public static IPAddress Normalize(IPAddress address)
        {
            if (address == null) return null;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            return address;
        }

        /// <summary>
        /// Spec §15: control connections and audio are accepted only from loopback (127.0.0.0/8), link-local
        /// (169.254.0.0/16) and private (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16) IPv4 addresses.
        /// </summary>
        public static bool IsAllowedRemote(IPAddress address)
        {
            address = Normalize(address);
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            if (b[0] == 127) return true;
            if (b[0] == 10) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        /// <summary>The directed broadcast address of <paramref name="ip"/> in a subnet with <paramref name="mask"/>.</summary>
        public static IPAddress DirectedBroadcast(IPAddress ip, IPAddress mask)
        {
            var a = ip.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var r = new byte[4];
            for (var i = 0; i < 4; i++) r[i] = (byte)(a[i] | ~m[i]);
            return new IPAddress(r);
        }

        /// <summary>
        /// The directed broadcast addresses for the given (address, mask) pairs, skipping loopback, masks of /31 and
        /// /32 (no broadcast address) and duplicates.
        /// </summary>
        public static List<IPAddress> DirectedBroadcasts(IEnumerable<KeyValuePair<IPAddress, IPAddress>> addresses)
        {
            var result = new List<IPAddress>();
            foreach (var pair in addresses)
            {
                var ip = pair.Key;
                var mask = pair.Value;
                if (ip == null || mask == null || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var m = mask.GetAddressBytes();
                if (m.Length != 4) continue;
                var prefix = m.Sum(x => CountBits(x));
                if (prefix >= 31 || prefix == 0) continue;
                var broadcast = DirectedBroadcast(ip, mask);
                if (!result.Contains(broadcast)) result.Add(broadcast);
            }
            return result;
        }

        /// <summary>IPv4 address and mask of every up, non-loopback interface of this machine.</summary>
        public static List<KeyValuePair<IPAddress, IPAddress>> LocalIPv4Addresses()
        {
            var list = new List<KeyValuePair<IPAddress, IPAddress>>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Listing the network interfaces failed: " + ex.Message);
                return list;
            }
            foreach (var nic in interfaces)
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
                        IPAddress mask = null;
                        try { mask = unicast.IPv4Mask; } catch (Exception) { }
                        list.Add(new KeyValuePair<IPAddress, IPAddress>(unicast.Address, mask));
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Debug("Skipping interface " + nic.Name + ": " + ex.Message);
                }
            }
            return list;
        }

        private static int CountBits(byte b)
        {
            var n = 0;
            for (; b != 0; b >>= 1) n += b & 1;
            return n;
        }
    }
}
