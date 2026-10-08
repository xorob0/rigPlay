// SPDX-License-Identifier: GPL-3.0-only
// SimHubSurface.cs: the logic behind the plugin's SimHub properties and actions (docs/protocol.md §12, §16): which
// tablet is primary, what the properties read, how the playback position is extrapolated, and which command an
// action sends. RigPlay's PluginBridge only attaches these to SimHub.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin
{
    /// <summary>What the selector needs to know about one session.</summary>
    public struct TabletCandidate
    {
        public int SessionId;
        public bool Paired;
        public bool PhoneConnected;

        /// <summary>Order in which phoneConnected became true (larger is more recent).</summary>
        public long PhoneConnectedOrder;

        /// <summary>Order in which the session paired (larger is more recent).</summary>
        public long PairedOrder;
    }

    public static class PrimaryTabletSelector
    {
        /// <summary>
        /// Spec §12: the Paired session whose phoneConnected became true most recently; if no session reports a phone,
        /// the Paired session that paired most recently. 0 when there is no Paired session.
        /// </summary>
        public static int Select(IEnumerable<TabletCandidate> candidates)
        {
            var paired = candidates.Where(c => c.Paired).ToList();
            if (paired.Count == 0) return 0;
            var withPhone = paired.Where(c => c.PhoneConnected).ToList();
            if (withPhone.Count > 0) return withPhone.OrderByDescending(c => c.PhoneConnectedOrder).ThenByDescending(c => c.PairedOrder).First().SessionId;
            return paired.OrderByDescending(c => c.PairedOrder).First().SessionId;
        }
    }

    public static class PlaybackClock
    {
        /// <summary>
        /// Spec §6.7: while playing, position + (now − receivedAt), clamped to duration when known; otherwise the
        /// reported position. Times are the plugin's monotonic milliseconds.
        /// </summary>
        public static double Position(NowPlaying nowPlaying, long receivedAtMs, long nowMs)
        {
            if (nowPlaying == null) return 0;
            var position = nowPlaying.Position;
            if (nowPlaying.Playing && nowMs > receivedAtMs) position += (nowMs - receivedAtMs) / 1000.0;
            if (nowPlaying.Duration.HasValue && position > nowPlaying.Duration.Value) position = nowPlaying.Duration.Value;
            return position < 0 ? 0 : position;
        }
    }

    /// <summary>The SimHub actions of spec §16.2.</summary>
    public enum SurfaceAction
    {
        PlayPause,
        NextTrack,
        PreviousTrack,
        Siri,
        ShowDashboard,
        ShowCarPlay,
        ToggleScreen,
    }

    public static class SurfaceActions
    {
        /// <summary>Action names as SimHub shows them, after the plugin prefix: RigPlay.&lt;name&gt;.</summary>
        public static string Name(SurfaceAction action)
        {
            return action.ToString();
        }

        /// <summary>The command an action sends, given the primary tablet's last status.screen (spec §16.2).</summary>
        public static CommandMessage CommandFor(SurfaceAction action, string lastScreen)
        {
            switch (action)
            {
                case SurfaceAction.PlayPause: return CommandMessage.MediaAction(Commands.PlayPause);
                case SurfaceAction.NextTrack: return CommandMessage.MediaAction(Commands.Next);
                case SurfaceAction.PreviousTrack: return CommandMessage.MediaAction(Commands.Previous);
                case SurfaceAction.Siri: return CommandMessage.MediaAction(Commands.Siri);
                case SurfaceAction.ShowDashboard: return CommandMessage.Of(Commands.ShowDashboard);
                case SurfaceAction.ShowCarPlay: return CommandMessage.Of(Commands.ShowCarPlay);
                case SurfaceAction.ToggleScreen:
                    return CommandMessage.Of(lastScreen == Screens.Dashboard ? Commands.ShowCarPlay : Commands.ShowDashboard);
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
    }

    /// <summary>
    /// The primary tablet as the properties see it: an immutable snapshot taken when sessions or status change; only
    /// the position moves between snapshots, extrapolated on read.
    /// </summary>
    public sealed class SurfaceSnapshot
    {
        public static readonly SurfaceSnapshot Empty = new SurfaceSnapshot();

        public bool TabletConnected { get; set; }
        public int PrimarySessionId { get; set; }
        public bool PhoneConnected { get; set; }
        public string Screen { get; set; } = Screens.Off;
        public NowPlaying NowPlaying { get; set; }
        public long StatusReceivedAtMs { get; set; }

        /// <summary>status.nav of the primary tablet (spec §6.7); null when no route guidance is active.</summary>
        public NavInfo Nav { get; set; }

        /// <summary>The ETA as local "HH:mm", computed once per status so that reading the property does not allocate.</summary>
        public string NavEta { get; set; } = "";

        /// <summary>The artwork file of the primary tablet (spec §6.14); "" when there is none.</summary>
        public string ArtworkPath { get; set; } = "";

        public string Title => NowPlaying?.Title ?? "";
        public string Artist => NowPlaying?.Artist ?? "";
        public string Album => NowPlaying?.Album ?? "";
        public string App => NowPlaying?.App ?? "";
        public bool Playing => NowPlaying != null && NowPlaying.Playing;
        public double Duration => NowPlaying?.Duration ?? 0;

        public bool NavActive => Nav != null;
        public string NavManeuver => Nav?.Maneuver ?? "";
        public double NavDistance => Nav?.DistanceM ?? 0;
        public string NavRoad => Nav?.Road ?? "";

        /// <summary>An ETA (seconds since the epoch) as local "HH:mm"; "" when unknown or out of range.</summary>
        public static string FormatEta(long? etaEpochS, TimeZoneInfo zone = null)
        {
            if (etaEpochS == null || etaEpochS < 0 || etaEpochS > 253402300799L) return "";
            var utc = DateTimeOffset.FromUnixTimeSeconds(etaEpochS.Value);
            var local = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local);
            return local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        public double PositionAt(long nowMs)
        {
            return PlaybackClock.Position(NowPlaying, StatusReceivedAtMs, nowMs);
        }
    }
}
