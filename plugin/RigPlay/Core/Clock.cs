// SPDX-License-Identifier: GPL-3.0-only
// Clock.cs: time sources. Timeouts and extrapolation use a monotonic clock (immune to wall-clock changes); the
// pairing date uses the wall clock. Tests substitute a manual clock.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Diagnostics;
using System.Threading;

namespace RigPlayPlugin
{
    public interface IClock
    {
        /// <summary>Monotonic milliseconds from an arbitrary origin.</summary>
        long NowMs { get; }

        /// <summary>Wall clock, UTC.</summary>
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public long NowMs => Watch.ElapsedMilliseconds;

        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>A clock the test moves by hand.</summary>
    public sealed class ManualClock : IClock
    {
        private long now;

        public ManualClock(long startMs = 1000000, DateTime? utc = null)
        {
            now = startMs;
            Origin = utc ?? new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            OriginMs = startMs;
        }

        private DateTime Origin { get; }
        private long OriginMs { get; }

        public long NowMs => Interlocked.Read(ref now);

        public DateTime UtcNow => Origin.AddMilliseconds(NowMs - OriginMs);

        public void Advance(long ms)
        {
            Interlocked.Add(ref now, ms);
        }
    }
}
