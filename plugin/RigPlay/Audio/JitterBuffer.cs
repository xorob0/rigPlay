// SPDX-License-Identifier: GPL-3.0-only
// JitterBuffer.cs: the per-stream play-out buffer of the audio receiver (docs/protocol.md §10.3). Datagrams are
// pushed from the network thread in any order; the audio output pulls PCM at its own clock. Each datagram is
// placed by its timestamp (sample clock), so reordering within the buffer depth is free, a gap plays as
// silence, and a datagram that arrives after its play-out time is dropped.
//
// Depth. After a reset (start flag, audioStart, underrun) the buffer holds back play-out until it has the target
// depth. The target is learned from the network: an underrun means the link stalled for longer than the depth we
// held, so when the delayed datagrams arrive the buffer measures how long the stall was (the depth it drained plus
// the silence it had to play) and raises the target to that stall and a quarter more, up to a cap. A Wi-Fi link
// that stalled for 600 ms once will do it again, and the next time 750 ms of depth absorbs it without a sound.
// Not every underrun is a stall: when the phone pauses the music the datagrams stop too. A stall ends with the
// held-back datagrams arriving in a burst, their timestamps continuing where the audio stopped; a pause ends with
// datagrams arriving at their normal pace, or with the timestamp jumping over the pause (the sender declares the
// gap, §10.2). Only a burst teaches the buffer anything. Nothing lowers the target: a stream lives minutes, and a
// dropout costs more than latency on music.
//
// Excess. Depth above the target is latency that protects nothing, so it is trimmed quietly: while the buffer is
// more than about 100 ms above the target it plays 1.5 % faster (linear interpolation; a third of a semitone,
// unnoticed on music under engine noise) until it is back within 30 ms of it. Only when the depth exceeds the
// target by a full second (a stall far longer than anything learned, or a sender clock running away) does it skip
// ahead to the target, which is an audible jump. Pure: no SimHub, WPF or NAudio types (compiled into
// RigPlay.Tests). Thread-safe.
using System;
using System.Collections.Generic;

namespace RigPlayPlugin.Audio
{
    public sealed class JitterBuffer
    {
        /// <summary>Initial depth to fill before play-out starts (§10.3: 60–120 ms is enough on a quiet LAN).</summary>
        public const int DefaultTargetMs = 80;

        /// <summary>
        /// The most the target grows to. Two seconds covers the stalls a tablet's Wi-Fi produces when it scans,
        /// roams or sleeps; beyond that the link is broken rather than slow.
        /// </summary>
        public const int DefaultMaxTargetMs = 2000;

        /// <summary>Depth above the target at which the buffer skips ahead (an audible jump) instead of trimming.</summary>
        public const int DefaultSkipSlackMs = 1000;

        /// <summary>Depth above the target at which the quiet catch-up starts.</summary>
        public const int DefaultCatchUpStartMs = 100;

        /// <summary>Depth above the target at which the catch-up stops.</summary>
        public const int DefaultCatchUpStopMs = 30;

        /// <summary>Play-out speed while catching up: 1.5 % faster trims 15 ms of excess per second.</summary>
        public const double CatchUpRatio = 1.015;

        /// <summary>After an underrun the target becomes the measured stall times this.</summary>
        public const double StallGrowthFactor = 1.25;

        private sealed class Packet
        {
            public long Start;
            public int Frames;
            public byte[] Data;

            public long End
            {
                get { return Start + Frames; }
            }
        }

        private readonly object gate = new object();
        private readonly SortedList<long, Packet> packets = new SortedList<long, Packet>();

        // Free-list of Packet (with their Data arrays) retired from <see cref="packets"/>, under gate, to spare the
        // network thread an allocation per datagram. A Packet is recycled ONLY once it has left packets
        // (DropBeforeLocked, skip-ahead, ResetLocked), so no array still readable by Read is ever reused. Capped so a
        // burst does not pin an unbounded amount of memory. startPayload is never pooled here: it can stay referenced
        // after a reset. The two counters prove the reuse to the tests.
        private readonly Stack<Packet> pool = new Stack<Packet>();
        private readonly int poolCap;
        private long poolHits;
        private long poolAllocations;

        // The largest End (Start + Frames) among the datagrams held, kept incrementally so the real-time path never
        // scans. End is not monotone in timestamp order (a short datagram can arrive after a long one), so it is a
        // running maximum: a new datagram can only raise it, and dropping the oldest datagrams never lowers it (the
        // maximum belongs to the most recent). It drops away only when the buffer empties (packets.Count == 0, where
        // the callers read 0) or on a reset.
        private long maxEnd;

        // Sequence and timestamp tracking, unwrapped to 64 bits within one epoch (between resets).
        private bool haveSeq;
        private long firstSeq;
        private long highestSeq;
        private bool haveTimestamp;
        private long lastTimestamp;
        private bool epochFromStart;
        private byte[] startPayload;

        // Sequence numbers seen in this epoch, by seq modulo the window, to tell a duplicate from a reordered datagram.
        private const int SeenWindow = 1024;
        private readonly long[] seen = new long[SeenWindow];

        private bool playing;
        private long readPos;
        // Fractional part of the play-out position while catching up (0 ≤ readFrac < 1).
        private double readFrac;
        private bool catchingUp;
        // After an underrun: datagrams ending at or before this position are late even while refilling.
        private long floor = long.MinValue;
        // The depth right after the newest datagram: when the next one is late, this is what there was to play.
        private long depthAtLastPush;
        // After an underrun: the depth that drained, the silence played until the first datagram came back, and the
        // silence played while refilling after it, to size the next target and to tell a stall from a pause.
        private bool measuringStall;
        private long stallDrainedFrames;
        private long stallSilenceFrames;
        private long refillSilenceFrames;

        private readonly int skipSlackFrames;
        private readonly int catchUpStartFrames;
        private readonly int catchUpStopFrames;
        private readonly short[] frameA;
        private readonly short[] frameB;

        // Counters, cumulative since construction (resets do not clear them, so rates stay continuous).
        private long received;
        private long lost;
        private long late;
        private long duplicates;
        private long silenceFrames;
        private long underruns;
        private long overflowFrames;
        private long overflows;
        private long resets;
        private long expected;
        private long longestStallFrames;
        private long catchUpFrames;

        public JitterBuffer(int sampleRate, int channels, int targetMs = DefaultTargetMs, int maxTargetMs = DefaultMaxTargetMs,
            int skipSlackMs = DefaultSkipSlackMs, int catchUpStartMs = DefaultCatchUpStartMs, int catchUpStopMs = DefaultCatchUpStopMs)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (targetMs < 0) throw new ArgumentOutOfRangeException(nameof(targetMs));
            if (maxTargetMs < 0) throw new ArgumentOutOfRangeException(nameof(maxTargetMs));
            if (skipSlackMs < 1) throw new ArgumentOutOfRangeException(nameof(skipSlackMs));
            if (catchUpStopMs < 0 || catchUpStartMs <= catchUpStopMs || catchUpStartMs >= skipSlackMs) throw new ArgumentOutOfRangeException(nameof(catchUpStartMs));
            SampleRate = sampleRate;
            Channels = channels;
            BlockAlign = 2 * channels;
            TargetFrames = Frames(targetMs);
            MaxTargetFrames = Math.Max(TargetFrames, Frames(maxTargetMs));
            skipSlackFrames = Math.Max(1, Frames(skipSlackMs));
            catchUpStartFrames = Frames(catchUpStartMs);
            catchUpStopFrames = Frames(catchUpStopMs);
            frameA = new short[channels];
            frameB = new short[channels];
            for (var i = 0; i < seen.Length; i++) seen[i] = long.MinValue;
            // The depth tops out at the skip ceiling (MaxTargetFrames + skipSlackFrames); even at one frame per
            // datagram no more than that many Packet can be held, so capping the free-list there bounds it without
            // ever starving the hot path. The floor keeps the pool useful for small, high-rate configs.
            poolCap = (int)Math.Min(4096, Math.Max(64, (long)MaxTargetFrames + skipSlackFrames));
        }

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>Bytes per frame (s16, interleaved).</summary>
        public int BlockAlign { get; }

        /// <summary>Depth the buffer fills to before play-out starts. Grows after every underrun (see <see cref="MaxTargetFrames"/>).</summary>
        public int TargetFrames { get; private set; }

        public double TargetMs
        {
            get { return TargetFrames * 1000.0 / SampleRate; }
        }

        /// <summary>The most the target grows to.</summary>
        public int MaxTargetFrames { get; }

        /// <summary>Depth above which the buffer skips ahead to <see cref="TargetFrames"/>: the target plus the skip slack.</summary>
        public int MaxFrames
        {
            get { lock (gate) return MaxFramesLocked(); }
        }

        /// <summary>True while play-out runs; false while (re)filling to the target depth.</summary>
        public bool IsPlaying
        {
            get { lock (gate) return playing; }
        }

        /// <summary>True while the buffer plays slightly faster to trim depth above the target.</summary>
        public bool IsCatchingUp
        {
            get { lock (gate) return catchingUp; }
        }

        /// <summary>Frames between the play-out position and the end of the newest datagram.</summary>
        public int BufferedFrames
        {
            get { lock (gate) return BufferedFramesLocked(); }
        }

        public double BufferedMs
        {
            get { return BufferedFrames * 1000.0 / SampleRate; }
        }

        /// <summary>
        /// Adds one datagram. <paramref name="payload"/> holds whole s16 frames (it is copied). A datagram with the
        /// start flag begins a new epoch: the buffer is reset first, unless it is a repeat of the start datagram
        /// that began the current one. Returns false when the datagram was dropped (late or duplicate).
        /// </summary>
        public bool Push(ushort seq, uint timestamp, bool start, byte[] payload, int offset, int count)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var frames = count / BlockAlign;
            if (frames <= 0) return false;

            lock (gate)
            {
                if (start)
                {
                    if (IsRepeatedStartLocked(seq, timestamp, payload, offset, frames * BlockAlign))
                    {
                        // UDP duplicated the start datagram: not a new epoch.
                        duplicates++;
                        return false;
                    }
                    ResetLocked();
                    epochFromStart = true;
                    startPayload = new byte[frames * BlockAlign];
                    Buffer.BlockCopy(payload, offset, startPayload, 0, startPayload.Length);
                }

                var extSeq = UnwrapSeq(seq);
                var extTs = UnwrapTimestamp(timestamp);
                if (haveSeq)
                {
                    if (extSeq <= highestSeq - SeenWindow || seen[Slot(extSeq)] == extSeq)
                    {
                        duplicates++;
                        return false;
                    }
                }
                seen[Slot(extSeq)] = extSeq;
                received++;
                if (!haveSeq)
                {
                    haveSeq = true;
                    firstSeq = extSeq;
                    highestSeq = extSeq;
                    expected++;
                }
                else if (extSeq > highestSeq)
                {
                    var gap = extSeq - highestSeq - 1;
                    lost += gap;
                    expected += gap + 1;
                    highestSeq = extSeq;
                }
                else
                {
                    // Out of order: it was counted as lost when the gap opened.
                    if (lost > 0) lost--;
                }

                if (!haveTimestamp || extTs > lastTimestamp)
                {
                    haveTimestamp = true;
                    lastTimestamp = extTs;
                }

                var end = extTs + frames;
                if (end <= floor || (playing && end <= readPos))
                {
                    late++;
                    return false;
                }
                if (packets.ContainsKey(extTs))
                {
                    duplicates++;
                    return false;
                }

                packets.Add(extTs, RentLocked(extTs, frames, payload, offset));
                if (end > maxEnd) maxEnd = end;

                if (!playing)
                {
                    // Filling: play-out starts at the oldest datagram held.
                    readPos = Math.Max(packets.Keys[0], floor);
                    readFrac = 0;
                    // The old target is back: now the stall can be measured, and the target it teaches is what the
                    // refill then waits for, so the depth really is a quarter more than the stall when play resumes.
                    if (measuringStall && BufferedFramesLocked() >= TargetFrames) LearnFromStallLocked();
                    if (BufferedFramesLocked() >= TargetFrames) playing = true;
                }
                else if (BufferedFramesLocked() > MaxFramesLocked())
                {
                    // Far too deep (a stall far longer than anything learned, or a sender clock running away): skip
                    // ahead to the target depth. Audible, so the ceiling is a full second above the target.
                    var newPos = maxEnd - TargetFrames;
                    overflowFrames += newPos - readPos;
                    overflows++;
                    readPos = newPos;
                    readFrac = 0;
                    catchingUp = false;
                    DropBeforeLocked(readPos);
                }
                depthAtLastPush = BufferedFramesLocked();
                return true;
            }
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with <paramref name="count"/> bytes of play-out (rounded down to whole frames;
        /// any remainder is zeroed). Always returns <paramref name="count"/>: silence while filling, for gaps and on
        /// underrun, so an output mixer never sees the stream end.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / BlockAlign;
            var written = 0;
            lock (gate)
            {
                if (!playing)
                {
                    Array.Clear(buffer, offset, count);
                    if (measuringStall)
                    {
                        if (packets.Count == 0) stallSilenceFrames += frames;
                        else refillSilenceFrames += frames;
                    }
                    return count;
                }

                UpdateCatchUpLocked();
                while (written < frames)
                {
                    DropBeforeLocked(readPos);
                    var need = frames - written;
                    var dst = offset + written * BlockAlign;
                    if (packets.Count == 0)
                    {
                        // Underrun: nothing left. Play silence and refill to the target before resuming. The depth we
                        // held was not enough for this stall: measure it while refilling and hold more next time.
                        Array.Clear(buffer, dst, need * BlockAlign);
                        silenceFrames += need;
                        underruns++;
                        playing = false;
                        catchingUp = false;
                        readFrac = 0;
                        floor = readPos;
                        measuringStall = true;
                        stallDrainedFrames = depthAtLastPush > 0 ? depthAtLastPush : TargetFrames;
                        stallSilenceFrames = need;
                        refillSilenceFrames = 0;
                        written = frames;
                        break;
                    }
                    if (catchingUp)
                    {
                        ReadInterpolatedFrameLocked(buffer, dst);
                        written++;
                        continue;
                    }
                    var packet = packets.Values[0];
                    if (packet.Start > readPos)
                    {
                        // A gap (lost or skipped audio): silence up to the next datagram.
                        var gap = (int)Math.Min(need, packet.Start - readPos);
                        Array.Clear(buffer, dst, gap * BlockAlign);
                        silenceFrames += gap;
                        readPos += gap;
                        written += gap;
                        continue;
                    }
                    var from = (int)(readPos - packet.Start);
                    var take = Math.Min(need, packet.Frames - from);
                    Buffer.BlockCopy(packet.Data, from * BlockAlign, buffer, dst, take * BlockAlign);
                    readPos += take;
                    written += take;
                }
            }
            var tail = count - frames * BlockAlign;
            if (tail > 0) Array.Clear(buffer, offset + frames * BlockAlign, tail);
            return count;
        }

        /// <summary>Discards everything buffered and waits for the target depth again. Counters and the learned target are kept.</summary>
        public void Reset()
        {
            lock (gate) ResetLocked();
        }

        /// <summary>
        /// Puts the target back to <paramref name="targetMs"/> (the page's "Forget"): what is buffered above it is trimmed
        /// quietly from here on, and the next underrun teaches afresh.
        /// </summary>
        public void ResetTarget(int targetMs)
        {
            if (targetMs < 0) return;
            lock (gate) TargetFrames = Math.Min(MaxTargetFrames, Frames(targetMs));
        }

        /// <summary>
        /// Starts from a target another buffer learned (the same network: a restarted stream, or the value saved from
        /// the last run). A value below the current target changes nothing; one above <see cref="MaxTargetFrames"/> is clamped.
        /// </summary>
        public void InheritTarget(double targetMs)
        {
            var frames = (int)(SampleRate * targetMs / 1000.0);
            lock (gate)
            {
                if (frames > TargetFrames) TargetFrames = Math.Min(MaxTargetFrames, frames);
            }
        }

        public JitterBufferCounters Counters
        {
            get
            {
                lock (gate)
                {
                    return new JitterBufferCounters
                    {
                        Received = received,
                        Expected = expected,
                        Lost = lost,
                        Late = late,
                        Duplicates = duplicates,
                        SilenceFrames = silenceFrames,
                        Underruns = underruns,
                        OverflowFrames = overflowFrames,
                        Overflows = overflows,
                        Resets = resets,
                        BufferedFrames = BufferedFramesLocked(),
                        TargetFrames = TargetFrames,
                        Playing = playing,
                        CatchingUp = catchingUp,
                        CatchUpFrames = catchUpFrames,
                        LongestStallFrames = longestStallFrames,
                        PoolHits = poolHits,
                        PoolAllocations = poolAllocations,
                    };
                }
            }
        }

        // Depth learning

        /// <summary>
        /// The old target is buffered again after an underrun. The stall was the depth that drained plus the silence
        /// played until the datagrams came back, less any part of it the sender declared as a gap (a pause, not a
        /// stall). If the refill itself took real time, the datagrams came at their normal pace: the source had paused,
        /// and more depth would not have helped. Otherwise raise the target to the stall and a quarter more, up to the
        /// cap; play-out then resumes once that is buffered, and the skip ceiling has moved up with it.
        /// </summary>
        private void LearnFromStallLocked()
        {
            measuringStall = false;
            var gap = floor == long.MinValue || packets.Count == 0 ? 0 : Math.Max(0, packets.Keys[0] - floor);
            var stall = stallDrainedFrames + stallSilenceFrames + refillSilenceFrames - gap;
            var paced = refillSilenceFrames > TargetFrames / 2;
            if (stall <= 0 || paced) return;
            if (stall > longestStallFrames) longestStallFrames = stall;
            if (TargetFrames >= MaxTargetFrames) return;
            TargetFrames = (int)Math.Min(MaxTargetFrames, (long)Math.Ceiling(stall * StallGrowthFactor));
        }

        private void UpdateCatchUpLocked()
        {
            var excess = BufferedFramesLocked() - TargetFrames;
            if (!catchingUp && excess > catchUpStartFrames)
            {
                catchingUp = true;
                readFrac = 0;
            }
            else if (catchingUp && excess <= catchUpStopFrames)
            {
                catchingUp = false;
                readFrac = 0;
            }
        }

        /// <summary>
        /// One output frame at the fractional position readPos + readFrac, interpolated between the input frames at
        /// readPos and readPos + 1 (silence where there is no datagram), then the position advances by the catch-up ratio.
        /// </summary>
        private void ReadInterpolatedFrameLocked(byte[] buffer, int dst)
        {
            var hasA = FrameAtLocked(readPos, frameA);
            var hasB = FrameAtLocked(readPos + 1, frameB);
            if (!hasA && !hasB) silenceFrames++;
            var frac = (float)readFrac;
            for (var c = 0; c < Channels; c++)
            {
                var a = frameA[c];
                var b = frameB[c];
                var v = (int)Math.Round(a + (b - a) * frac);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                buffer[dst + 2 * c] = (byte)v;
                buffer[dst + 2 * c + 1] = (byte)(v >> 8);
            }
            catchUpFrames++;
            readFrac += CatchUpRatio;
            while (readFrac >= 1)
            {
                readFrac -= 1;
                readPos++;
            }
        }

        /// <summary>The frame at <paramref name="pos"/> into <paramref name="dst"/>; false (and zeros) in a gap.</summary>
        private bool FrameAtLocked(long pos, short[] dst)
        {
            var values = packets.Values;
            // After DropBeforeLocked(readPos) the frame is in one of the first datagrams, or in a gap.
            for (var i = 0; i < values.Count && i < 3; i++)
            {
                var p = values[i];
                if (pos < p.Start) break;
                if (pos < p.End)
                {
                    var at = (int)(pos - p.Start) * BlockAlign;
                    for (var c = 0; c < Channels; c++) dst[c] = (short)(p.Data[at + 2 * c] | (p.Data[at + 2 * c + 1] << 8));
                    return true;
                }
            }
            Array.Clear(dst, 0, dst.Length);
            return false;
        }

        private int MaxFramesLocked()
        {
            return (int)Math.Min(int.MaxValue, (long)TargetFrames + skipSlackFrames);
        }

        private void ResetLocked()
        {
            var values = packets.Values;
            for (var i = 0; i < values.Count; i++) RecycleLocked(values[i]);
            packets.Clear();
            maxEnd = 0;
            playing = false;
            catchingUp = false;
            readPos = 0;
            readFrac = 0;
            floor = long.MinValue;
            measuringStall = false;
            for (var i = 0; i < seen.Length; i++) seen[i] = long.MinValue;
            haveSeq = false;
            haveTimestamp = false;
            epochFromStart = false;
            startPayload = null;
            resets++;
        }

        private int BufferedFramesLocked()
        {
            if (packets.Count == 0) return 0;
            var buffered = maxEnd - readPos;
            return buffered <= 0 ? 0 : (int)Math.Min(int.MaxValue, buffered);
        }

        /// <summary>
        /// A start datagram identical to the one that began this epoch, arriving within the first few datagrams.
        /// A real restart (a new audioStart) also has seq 0 and timestamp 0, so the payload must match too.
        /// </summary>
        private bool IsRepeatedStartLocked(ushort seq, uint timestamp, byte[] payload, int offset, int count)
        {
            const int RepeatWindow = 8;
            if (!epochFromStart || !haveSeq || startPayload == null) return false;
            if (seq != (ushort)firstSeq || timestamp != 0 || highestSeq - firstSeq >= RepeatWindow) return false;
            if (count != startPayload.Length) return false;
            for (var i = 0; i < count; i++)
            {
                if (payload[offset + i] != startPayload[i]) return false;
            }
            return true;
        }

        private static int Slot(long extSeq)
        {
            return (int)(((extSeq % SeenWindow) + SeenWindow) % SeenWindow);
        }

        private void DropBeforeLocked(long position)
        {
            while (packets.Count > 0 && packets.Values[0].End <= position)
            {
                var p = packets.Values[0];
                packets.RemoveAt(0);
                RecycleLocked(p);
            }
        }

        /// <summary>
        /// A Packet for one datagram, taken from the free-list when it holds one (its Data reused if it is already big
        /// enough, reallocated once otherwise) or newly allocated. The payload is copied in; the caller owns placing it
        /// in <see cref="packets"/>. Under gate.
        /// </summary>
        private Packet RentLocked(long start, int frames, byte[] payload, int offset)
        {
            var length = frames * BlockAlign;
            Packet p;
            if (pool.Count > 0)
            {
                p = pool.Pop();
                poolHits++;
                if (p.Data.Length < length) p.Data = new byte[length];
            }
            else
            {
                p = new Packet { Data = new byte[length] };
                poolAllocations++;
            }
            p.Start = start;
            p.Frames = frames;
            Buffer.BlockCopy(payload, offset, p.Data, 0, length);
            return p;
        }

        /// <summary>
        /// Returns a Packet that has just left <see cref="packets"/> to the free-list, keeping its Data for reuse, up to
        /// the pool cap. Called ONLY after the Packet is out of packets, so Read can never see a recycled buffer.
        /// Under gate.
        /// </summary>
        private void RecycleLocked(Packet p)
        {
            if (pool.Count >= poolCap) return;
            pool.Push(p);
        }

        private int Frames(int ms)
        {
            return (int)Math.Min(int.MaxValue, (long)SampleRate * ms / 1000);
        }

        /// <summary>RFC 1982 unwrap of a 16-bit sequence number around the highest one seen.</summary>
        private long UnwrapSeq(ushort seq)
        {
            if (!haveSeq) return seq;
            var delta = (short)(seq - (ushort)highestSeq);
            return highestSeq + delta;
        }

        /// <summary>Unwrap of a 32-bit sample clock around the newest timestamp seen.</summary>
        private long UnwrapTimestamp(uint timestamp)
        {
            if (!haveTimestamp) return timestamp;
            var delta = (int)(timestamp - (uint)lastTimestamp);
            return lastTimestamp + delta;
        }
    }

    /// <summary>A snapshot of a jitter buffer's cumulative counters.</summary>
    public struct JitterBufferCounters
    {
        /// <summary>Distinct datagrams pushed, including late ones; duplicates are not counted.</summary>
        public long Received;
        /// <summary>Datagrams the sequence numbers say were sent (received + lost, within epochs).</summary>
        public long Expected;
        /// <summary>Sequence numbers never received (a reordered datagram that turns up is taken back off).</summary>
        public long Lost;
        /// <summary>Datagrams dropped because their play-out time had passed.</summary>
        public long Late;
        public long Duplicates;
        /// <summary>Frames played as silence: gaps and underruns (not the initial fill).</summary>
        public long SilenceFrames;
        public long Underruns;
        /// <summary>Frames skipped because the buffer exceeded its maximum depth.</summary>
        public long OverflowFrames;
        /// <summary>Skip-ahead events (each one is an audible jump).</summary>
        public long Overflows;
        public long Resets;
        public int BufferedFrames;
        /// <summary>The current (adaptive) target depth.</summary>
        public int TargetFrames;
        public bool Playing;
        /// <summary>Playing slightly faster to trim depth above the target.</summary>
        public bool CatchingUp;
        /// <summary>Output frames produced while catching up (1.5 % of them are the trimmed excess).</summary>
        public long CatchUpFrames;
        /// <summary>The longest stall measured at an underrun: the depth that drained plus the silence played.</summary>
        public long LongestStallFrames;
        /// <summary>Datagrams whose Packet was taken from the free-list rather than allocated (proof of reuse).</summary>
        public long PoolHits;
        /// <summary>Datagrams for which a new Packet had to be allocated (the free-list was empty).</summary>
        public long PoolAllocations;
    }
}
