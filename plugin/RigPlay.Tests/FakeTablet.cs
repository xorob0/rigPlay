// SPDX-License-Identifier: GPL-3.0-only
// FakeTablet.cs: an in-process tablet for the integration tests: a TcpClient that writes protocol lines and reads
// the plugin's answers, skipping heartbeats unless asked for them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    internal sealed class FakeTablet : IDisposable
    {
        public const string DefaultId = "9b1e4d2c-5a7f-4e3b-8c61-2f0a9d4b7e18";

        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;

        public FakeTablet(int port, int readTimeoutMs = 10000)
        {
            client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.NoDelay = true;
            client.ReceiveTimeout = readTimeoutMs;
            var stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        /// <summary>Every line received, heartbeats included, for assertions.</summary>
        public List<string> Received { get; } = new List<string>();

        public int HeartbeatsReceived { get; private set; }

        public void SendLine(string line)
        {
            writer.Write(line + "\n");
        }

        public void Send(Message message)
        {
            SendLine(MessageCodec.Encode(message));
        }

        public static HelloMessage NewHello(string tabletId = DefaultId, int protocol = 1, int? minProtocol = 1, string name = "Lenovo Tab P11")
        {
            return new HelloMessage
            {
                TabletId = tabletId,
                Name = name,
                AppVersion = "0.3.0",
                Protocol = protocol,
                MinProtocol = minProtocol,
                Features = new List<string> { Features.Telemetry, Features.IdleDashboard },
            };
        }

        public WelcomeMessage Hello(string tabletId = DefaultId, string name = "Lenovo Tab P11")
        {
            Send(NewHello(tabletId, name: name));
            return Expect<WelcomeMessage>();
        }

        /// <summary>The next line, or null at EOF. Throws on timeout.</summary>
        public string ReadLine()
        {
            var line = reader.ReadLine();
            if (line != null) Received.Add(line);
            return line;
        }

        /// <summary>The next message that is not a heartbeat; null at EOF.</summary>
        public Message Next()
        {
            while (true)
            {
                var line = ReadLine();
                if (line == null) return null;
                var message = MessageCodec.Decode(line);
                if (message is HeartbeatMessage)
                {
                    HeartbeatsReceived++;
                    continue;
                }
                return message;
            }
        }

        public T Expect<T>() where T : Message
        {
            var message = Next();
            Assert.NotNull(message);
            return Assert.IsType<T>(message);
        }

        public ErrorMessage ExpectError(string code)
        {
            var error = Expect<ErrorMessage>();
            Assert.Equal(code, error.Code);
            return error;
        }

        /// <summary>Reads (skipping heartbeats) until EOF; fails if a non-heartbeat message arrives first.</summary>
        public void ExpectClosed()
        {
            try
            {
                var message = Next();
                Assert.True(message == null, "expected the connection to close, got " + (message == null ? "" : MessageCodec.Encode(message)));
            }
            catch (IOException)
            {
                // A reset counts as closed.
            }
        }

        /// <summary>Reads lines for <paramref name="ms"/>, answering nothing; returns the non-heartbeat messages seen.</summary>
        public List<Message> Drain(int ms)
        {
            var seen = new List<Message>();
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            var old = client.ReceiveTimeout;
            while (DateTime.UtcNow < until)
            {
                client.ReceiveTimeout = Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds);
                try
                {
                    var m = Next();
                    if (m == null) break;
                    seen.Add(m);
                }
                catch (IOException)
                {
                    break;
                }
            }
            client.ReceiveTimeout = old;
            return seen;
        }

        public void Dispose()
        {
            try { client.Close(); } catch { }
        }

        /// <summary>Polls a condition for up to <paramref name="ms"/>. The default is generous: it only bounds how long a
        /// failing test takes, and a busy CI runner can be slow to schedule the network threads.</summary>
        public static bool WaitFor(Func<bool> condition, int ms = 10000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }
    }
}
