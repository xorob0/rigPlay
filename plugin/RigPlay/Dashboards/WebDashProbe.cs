// SPDX-License-Identifier: GPL-3.0-only
// WebDashProbe.cs: is SimHub's web dash server answering? GET http://127.0.0.1:<port>/ with a 1 s timeout, every
// 10 s (docs/protocol.md §11). Any HTTP response counts as reachable. The result goes to state.dashboardServer.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Dashboards
{
    public sealed class WebDashProbe : IDisposable
    {
        public const int DefaultIntervalMs = 10000;
        public const int DefaultTimeoutMs = 1000;

        private readonly Func<int> port;
        private readonly int intervalMs;
        private readonly int timeoutMs;
        private readonly object sync = new object();
        private Timer timer;
        private int probing;

        public WebDashProbe(Func<int> port, int intervalMs = DefaultIntervalMs, int timeoutMs = DefaultTimeoutMs)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.intervalMs = intervalMs;
            this.timeoutMs = timeoutMs;
        }

        /// <summary>The last result; null before the first probe finished.</summary>
        public DashboardServerInfo Current { get; private set; }

        /// <summary>The result changed (reachability or port). Raised on a pool thread.</summary>
        public event Action Changed;

        public void Start()
        {
            lock (sync)
            {
                if (timer != null) return;
                timer = new Timer(_ => ProbeNow(), null, 0, intervalMs);
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                timer?.Dispose();
                timer = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Probes now (skipped when a probe is already running) and updates <see cref="Current"/>.</summary>
        public void ProbeNow()
        {
            if (Interlocked.Exchange(ref probing, 1) != 0) return;
            try
            {
                int p;
                try { p = port(); } catch (Exception) { p = ProtocolDefaults.WebDashPort; }
                var reachable = Probe(p, timeoutMs);
                var previous = Current;
                Current = new DashboardServerInfo { Reachable = reachable, Port = p };
                if (previous == null || previous.Reachable != reachable || previous.Port != p)
                {
                    PluginLog.Info("Web dash server on port " + p + ": " + (reachable ? "reachable" : "not reachable"));
                    var handler = Changed;
                    if (handler != null)
                    {
                        try { handler(); } catch (Exception ex) { PluginLog.Error("A web dash change handler failed", ex); }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref probing, 0);
            }
        }

        /// <summary>True when http://127.0.0.1:<paramref name="port"/>/ answers with an HTTP status line in time.</summary>
        public static bool Probe(int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(IPAddress.Loopback, port);
                    if (!connect.Wait(timeoutMs) || !client.Connected) return false;
                    client.ReceiveTimeout = timeoutMs;
                    client.SendTimeout = timeoutMs;
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: 127.0.0.1:" + port + "\r\nUser-Agent: rigPlay\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    var buffer = new byte[16];
                    var read = 0;
                    while (read < 5)
                    {
                        var n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return read >= 5 && Encoding.ASCII.GetString(buffer, 0, 5) == "HTTP/";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
