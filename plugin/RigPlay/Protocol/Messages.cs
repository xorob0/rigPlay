// SPDX-License-Identifier: GPL-3.0-only
// Messages.cs: the JSON messages of docs/protocol.md as typed classes, and MessageCodec, which turns one line of
// the control channel (or a beacon datagram) into a message and back, enforcing every rule the spec puts on the
// content: required members, JSON types, ranges, enum values (§3, §4.3, §6, §7.1).
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RigPlayPlugin.Protocol
{
    /// <summary>The <c>type</c> values of protocol 1.</summary>
    public static class MessageTypes
    {
        public const string Beacon = "beacon";
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string PairRequest = "pairRequest";
        public const string PairResult = "pairResult";
        public const string Heartbeat = "heartbeat";
        public const string State = "state";
        public const string Status = "status";
        public const string Command = "command";
        public const string Telemetry = "telemetry";
        public const string Error = "error";
        public const string AudioStart = "audioStart";
        public const string AudioStop = "audioStop";
        public const string Artwork = "artwork";
        public const string MicStart = "micStart";
        public const string MicStop = "micStop";

        /// <summary>Types the tablet sends to the plugin on the control channel.</summary>
        public static readonly HashSet<string> TabletToPlugin = new HashSet<string>(StringComparer.Ordinal)
        {
            Hello, PairRequest, Heartbeat, Status, Error, AudioStart, AudioStop, Artwork, MicStart, MicStop,
        };

        /// <summary>Types only a session with feature <c>mic</c> may send (spec §6.13).</summary>
        public static readonly HashSet<string> MicFeature = new HashSet<string>(StringComparer.Ordinal) { MicStart, MicStop };
    }

    /// <summary>Feature strings (spec §7.3).</summary>
    public static class Features
    {
        public const string Telemetry = "telemetry";
        public const string IdleDashboard = "idleDashboard";
        public const string Mic = "mic";
    }

    /// <summary>Error codes (spec §14.1). A receiver accepts codes it does not know.</summary>
    public static class ErrorCodes
    {
        public const string UnsupportedProtocol = "unsupportedProtocol";
        public const string HelloRequired = "helloRequired";
        public const string NotPaired = "notPaired";
        public const string UnexpectedMessage = "unexpectedMessage";
        public const string BadMessage = "badMessage";
        public const string LineTooLong = "lineTooLong";
        public const string Replaced = "replaced";
        public const string Forgotten = "forgotten";
        public const string Shutdown = "shutdown";
        public const string CommandUnavailable = "commandUnavailable";
        public const string Internal = "internal";

        /// <summary>Codes spec §14.1 lists as fatal: fatal even when a peer leaves out <c>fatal: true</c> (senders always set it).</summary>
        public static readonly HashSet<string> Fatal = new HashSet<string>(StringComparer.Ordinal)
        {
            UnsupportedProtocol, HelloRequired, LineTooLong, Replaced, Forgotten, Shutdown,
        };
    }

    /// <summary><c>pairResult.reason</c> values (spec §6.4).</summary>
    public static class PairReasons
    {
        public const string PinRequired = "pinRequired";
        public const string WrongPin = "wrongPin";
        public const string PinExpired = "pinExpired";
        public const string TooManyAttempts = "tooManyAttempts";
        public const string Denied = "denied";
        public const string TokenInvalid = "tokenInvalid";

        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            PinRequired, WrongPin, PinExpired, TooManyAttempts, Denied, TokenInvalid,
        };
    }

    /// <summary><c>status.screen</c> values (spec §6.7).</summary>
    public static class Screens
    {
        public const string CarPlay = "carplay";
        public const string Dashboard = "dashboard";
        public const string Idle = "idle";
        public const string Off = "off";

        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal) { CarPlay, Dashboard, Idle, Off };
    }

    /// <summary><c>command.command</c> and <c>command.action</c> values (spec §6.8).</summary>
    public static class Commands
    {
        public const string Media = "media";
        public const string ShowDashboard = "showDashboard";
        public const string ShowCarPlay = "showCarPlay";

        public const string PlayPause = "playPause";
        public const string Next = "next";
        public const string Previous = "previous";
        public const string Siri = "siri";

        public static readonly HashSet<string> AllCommands = new HashSet<string>(StringComparer.Ordinal) { Media, ShowDashboard, ShowCarPlay };
        public static readonly HashSet<string> AllMediaActions = new HashSet<string>(StringComparer.Ordinal) { PlayPause, Next, Previous, Siri };
    }

    /// <summary>Audio stream names and formats (spec §6.11, §10.2).</summary>
    public static class AudioStreams
    {
        public const string Media = "media";
        public const string Alt = "alt";
        public const string Telephony = "telephony";
        public const string PcmS16Le = "pcm_s16le";
        public const string Opus = "opus";

        /// <summary>The datagram streamType of the PC microphone (spec §6.13, §10.4): the only value micStart / micStop allow.</summary>
        public const int MicStreamType = 4;

        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal) { Media, Alt, Telephony };
        /// <summary>The audioStart formats (§6.11, §10.4); the receiver decides separately whether it accepts opus.</summary>
        public static readonly HashSet<string> Formats = new HashSet<string>(StringComparer.Ordinal) { PcmS16Le, Opus };
        /// <summary>The micStart formats (§6.13, §10.5): the microphone is pcm_s16le only.</summary>
        public static readonly HashSet<string> MicFormats = new HashSet<string>(StringComparer.Ordinal) { PcmS16Le };
    }

    /// <summary>Base of every message. <see cref="Type"/> is the wire <c>type</c>.</summary>
    public abstract class Message
    {
        public abstract string Type { get; }

        public override string ToString()
        {
            return MessageCodec.Encode(this);
        }
    }

    public sealed class BeaconMessage : Message
    {
        public override string Type => MessageTypes.Beacon;
        public string Name { get; set; }
        public string HostId { get; set; }
        public string Version { get; set; }
        public string SimhubVersion { get; set; }
        public int ControlPort { get; set; }
        public int AudioPort { get; set; }
        public int Protocol { get; set; }
        public int? MinProtocol { get; set; }
    }

    public sealed class HelloMessage : Message
    {
        public override string Type => MessageTypes.Hello;
        public string TabletId { get; set; }
        public string Name { get; set; }
        public string AppVersion { get; set; }
        public int Protocol { get; set; }
        public int? MinProtocol { get; set; }
        public List<string> Features { get; set; }

        public int EffectiveMinProtocol => MinProtocol ?? 1;
        public IReadOnlyList<string> EffectiveFeatures => (IReadOnlyList<string>)Features ?? new string[0];
    }

    public sealed class WelcomeMessage : Message
    {
        public override string Type => MessageTypes.Welcome;
        public string HostId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string SimhubVersion { get; set; }
        public int Protocol { get; set; }
        public List<string> Features { get; set; } = new List<string>();
    }

    public enum PairRequestForm
    {
        Start,
        SubmitPin,
        Resume,
    }

    public sealed class PairRequestMessage : Message
    {
        public override string Type => MessageTypes.PairRequest;
        public string Token { get; set; }
        public string Pin { get; set; }

        public PairRequestForm Form => Token != null ? PairRequestForm.Resume : Pin != null ? PairRequestForm.SubmitPin : PairRequestForm.Start;
    }

    public sealed class PairResultMessage : Message
    {
        public override string Type => MessageTypes.PairResult;
        public bool Ok { get; set; }
        public string Token { get; set; }
        public string Reason { get; set; }
        public int? PinExpiresInSec { get; set; }
        public int? AttemptsLeft { get; set; }

        public static PairResultMessage Success(string token)
        {
            return new PairResultMessage { Ok = true, Token = token };
        }

        public static PairResultMessage Failure(string reason)
        {
            return new PairResultMessage { Ok = false, Reason = reason };
        }
    }

    public sealed class HeartbeatMessage : Message
    {
        public override string Type => MessageTypes.Heartbeat;
        public long? Seq { get; set; }
    }

    public sealed class DashboardServerInfo
    {
        public bool Reachable { get; set; }
        public int Port { get; set; }
    }

    public sealed class AudioInfo
    {
        public bool Enabled { get; set; }
        public int Port { get; set; }
        public List<string> Formats { get; set; } = new List<string> { AudioStreams.PcmS16Le };
    }

    /// <summary><c>state.mic</c> (spec §6.6): whether the plugin answers micStart with microphone audio.</summary>
    public sealed class MicInfo
    {
        public bool Enabled { get; set; }
    }

    public sealed class StateMessage : Message
    {
        public override string Type => MessageTypes.State;

        /// <summary>Required; null when no dashboard is selected on the PC.</summary>
        public string DashboardUrl { get; set; }

        public string IdleDashboardUrl { get; set; }
        public DashboardServerInfo DashboardServer { get; set; }
        public AudioInfo Audio { get; set; } = new AudioInfo();

        /// <summary>Optional; sent only to sessions with feature <c>mic</c> (spec §6.6).</summary>
        public MicInfo Mic { get; set; }
    }

    public sealed class NowPlaying
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string App { get; set; }
        public bool Playing { get; set; }
        public double Position { get; set; }
        public double? Duration { get; set; }
        public long UpdatedAt { get; set; }
    }

    /// <summary>CarPlay route guidance (spec §6.7 <c>status.nav</c>). Every member may be null.</summary>
    public sealed class NavInfo
    {
        /// <summary>Next maneuver: the lowerCamel name of Apple's RouteGuidanceManeuverType (<see cref="NavManeuvers.Known"/>), or any other string.</summary>
        public string Maneuver { get; set; }

        /// <summary>Whole metres to the next maneuver, ≥ 0.</summary>
        public int? DistanceM { get; set; }

        /// <summary>Road after the next maneuver.</summary>
        public string Road { get; set; }

        /// <summary>Estimated arrival, seconds since the Unix epoch (UTC), from the phone.</summary>
        public long? EtaEpochS { get; set; }
    }

    /// <summary>
    /// <c>status.nav.maneuver</c> values (spec §6.7.1): the lowerCamel names of Apple's RouteGuidanceManeuverType, in
    /// type order (0..53). Types newer than this table arrive as <c>noTurn</c>; receivers accept any string.
    /// </summary>
    public static class NavManeuvers
    {
        public static readonly string[] Known = BuildKnown();

        public const int MaxLength = 64;

        private static string[] BuildKnown()
        {
            var names = new List<string>
            {
                "noTurn", "leftTurn", "rightTurn", "straightAhead", "uTurn", "followRoad", "enterRoundabout", "exitRoundabout",
                "offRamp", "onRamp", "arriveEndOfNavigation", "startRoute", "arriveAtDestination", "keepLeft", "keepRight",
                "enterFerry", "exitFerry", "changeFerry", "startRouteWithUTurn", "uTurnAtRoundabout", "leftTurnAtEnd",
                "rightTurnAtEnd", "highwayOffRampLeft", "highwayOffRampRight", "arriveAtDestinationLeft", "arriveAtDestinationRight",
                "uTurnWhenPossible", "arriveEndOfDirections",
            };
            for (var i = 1; i <= 19; i++) names.Add("roundaboutExit" + i);
            names.AddRange(new[] { "sharpLeftTurn", "sharpRightTurn", "slightLeftTurn", "slightRightTurn", "changeHighway", "changeHighwayLeft", "changeHighwayRight" });
            return names.ToArray();
        }
    }

    public sealed class StatusMessage : Message
    {
        public override string Type => MessageTypes.Status;
        public bool PhoneConnected { get; set; }
        public string PhoneName { get; set; }
        public string Screen { get; set; } = Screens.Idle;

        /// <summary>Required; null when nothing is known.</summary>
        public NowPlaying NowPlaying { get; set; }

        /// <summary>Optional; null when no route guidance is active (spec §6.7).</summary>
        public NavInfo Nav { get; set; }
    }

    /// <summary><c>artwork.mime</c> values the plugin shows (spec §6.14).</summary>
    public static class ArtworkFormats
    {
        /// <summary>What the rigPlay tablet sends.</summary>
        public const string Jpeg = "image/jpeg";

        /// <summary>Also accepted.</summary>
        public const string Png = "image/png";

        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal) { Jpeg, Png };
    }

    /// <summary>Now-playing artwork from the tablet (spec §6.14). There is no "clear": the last image stays.</summary>
    public sealed class ArtworkMessage : Message
    {
        public override string Type => MessageTypes.Artwork;
        public string Mime { get; set; }
        public string Base64 { get; set; }

        /// <summary>The decoded image. Set by the decoder and by <see cref="Of"/>.</summary>
        public byte[] Bytes { get; set; }

        public static ArtworkMessage Of(string mime, byte[] bytes)
        {
            return new ArtworkMessage { Mime = mime, Bytes = bytes, Base64 = Convert.ToBase64String(bytes) };
        }
    }

    public sealed class CommandMessage : Message
    {
        public override string Type => MessageTypes.Command;
        public string Command { get; set; }

        /// <summary>Set for <c>media</c> only.</summary>
        public string Action { get; set; }

        public static CommandMessage MediaAction(string action)
        {
            return new CommandMessage { Command = Commands.Media, Action = action };
        }

        public static CommandMessage Of(string command)
        {
            return new CommandMessage { Command = command };
        }
    }

    public sealed class TelemetryMessage : Message
    {
        public override string Type => MessageTypes.Telemetry;
        public double? SpeedMps { get; set; }
        public string Gear { get; set; }
        public double? Heading { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public double? Alt { get; set; }
        public bool? Night { get; set; }
        public double? FuelPercent { get; set; }
        public double? RangeKm { get; set; }
        public double? Rpm { get; set; }
        public string TrackName { get; set; }
        public string SessionType { get; set; }
        public bool? GameRunning { get; set; }
    }

    public sealed class ErrorMessage : Message
    {
        public override string Type => MessageTypes.Error;
        public string Code { get; set; }
        public string Message { get; set; }
        public bool? Fatal { get; set; }
        public string RefType { get; set; }
        public int? MinProtocol { get; set; }
        public int? MaxProtocol { get; set; }

        /// <summary>The connection closes after this error: <c>fatal: true</c>, or a code that spec §14.1 lists as fatal.</summary>
        public bool IsFatal => Fatal == true || (Code != null && ErrorCodes.Fatal.Contains(Code));

        public static ErrorMessage Of(string code, string message, bool fatal = false, string refType = null)
        {
            return new ErrorMessage { Code = code, Message = message, Fatal = fatal ? true : (bool?)null, RefType = refType };
        }
    }

    public sealed class AudioStartMessage : Message
    {
        public override string Type => MessageTypes.AudioStart;
        public string Stream { get; set; }
        public string Format { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
    }

    public sealed class AudioStopMessage : Message
    {
        public override string Type => MessageTypes.AudioStop;
        public string Stream { get; set; }
    }

    /// <summary>Tablet → plugin (spec §6.13): send the PC microphone to the tablet's <see cref="Port"/>.</summary>
    public sealed class MicStartMessage : Message
    {
        public override string Type => MessageTypes.MicStart;
        public int StreamType { get; set; } = AudioStreams.MicStreamType;
        public string Format { get; set; } = AudioStreams.PcmS16Le;
        public int SampleRate { get; set; }
        public int Channels { get; set; } = 1;
        public int Port { get; set; }
    }

    /// <summary>Tablet → plugin (spec §6.13): the phone closed its microphone.</summary>
    public sealed class MicStopMessage : Message
    {
        public override string Type => MessageTypes.MicStop;
        public int StreamType { get; set; } = AudioStreams.MicStreamType;
    }

    /// <summary>Why a line did not decode to a valid message.</summary>
    public enum DecodeFailure
    {
        None,

        /// <summary>Not JSON, not an object, or no string <c>type</c>: logged and ignored (spec §14.2).</summary>
        Malformed,

        /// <summary>A <c>type</c> protocol 1 does not define: ignored without reply (spec §14.2).</summary>
        UnknownType,

        /// <summary>A known type with invalid content: answered with <c>badMessage</c> (spec §14.2).</summary>
        Invalid,
    }

    public sealed class DecodeResult
    {
        public Message Message { get; internal set; }
        public DecodeFailure Failure { get; internal set; }

        /// <summary>The <c>type</c> member when there was a string one, even for an invalid or unknown message.</summary>
        public string Type { get; internal set; }

        public string Reason { get; internal set; }

        public bool Ok => Failure == DecodeFailure.None;
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>Decodes and encodes protocol messages. Thread-safe: it holds no state.</summary>
    public static class MessageCodec
    {
        /// <summary>Decodes one line (without its terminator) or one beacon payload. Never throws.</summary>
        public static DecodeResult TryDecode(string text)
        {
            var result = new DecodeResult();
            JToken token;
            try
            {
                token = ParseStrict(text);
            }
            catch (Exception ex)
            {
                result.Failure = DecodeFailure.Malformed;
                result.Reason = "not JSON: " + ex.Message;
                return result;
            }

            var obj = token as JObject;
            if (obj == null)
            {
                result.Failure = DecodeFailure.Malformed;
                result.Reason = "not a JSON object";
                return result;
            }
            JToken typeToken;
            if (!obj.TryGetValue("type", out typeToken) || typeToken.Type != JTokenType.String)
            {
                result.Failure = DecodeFailure.Malformed;
                result.Reason = "no string type member";
                return result;
            }
            var type = (string)typeToken;
            result.Type = type;

            Func<JObject, Message> decoder;
            if (!Decoders.TryGetValue(type, out decoder))
            {
                result.Failure = DecodeFailure.UnknownType;
                result.Reason = "unknown type " + type;
                return result;
            }
            try
            {
                result.Message = decoder(obj);
            }
            catch (ProtocolException ex)
            {
                result.Failure = DecodeFailure.Invalid;
                result.Reason = ex.Message;
            }
            return result;
        }

        /// <summary>Decodes or throws <see cref="ProtocolException"/>.</summary>
        public static Message Decode(string text)
        {
            var result = TryDecode(text);
            if (!result.Ok) throw new ProtocolException(result.Failure + ": " + result.Reason);
            return result.Message;
        }

        /// <summary>Encodes a message as one line of JSON, without the terminating newline.</summary>
        public static string Encode(Message message)
        {
            return ToJson(message).ToString(Formatting.None);
        }

        /// <summary>The message as a JSON object. Optional members that are null are left out.</summary>
        public static JObject ToJson(Message message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var o = new JObject { ["type"] = message.Type };
            switch (message)
            {
                case BeaconMessage m:
                    o["name"] = m.Name;
                    o["hostId"] = m.HostId;
                    o["version"] = m.Version;
                    Opt(o, "simhubVersion", m.SimhubVersion);
                    o["controlPort"] = m.ControlPort;
                    o["audioPort"] = m.AudioPort;
                    o["protocol"] = m.Protocol;
                    Opt(o, "minProtocol", m.MinProtocol);
                    break;
                case HelloMessage m:
                    o["tabletId"] = m.TabletId;
                    o["name"] = m.Name;
                    o["appVersion"] = m.AppVersion;
                    o["protocol"] = m.Protocol;
                    Opt(o, "minProtocol", m.MinProtocol);
                    if (m.Features != null) o["features"] = new JArray(m.Features.Cast<object>().ToArray());
                    break;
                case WelcomeMessage m:
                    o["hostId"] = m.HostId;
                    o["name"] = m.Name;
                    o["version"] = m.Version;
                    Opt(o, "simhubVersion", m.SimhubVersion);
                    o["protocol"] = m.Protocol;
                    o["features"] = new JArray((m.Features ?? new List<string>()).Cast<object>().ToArray());
                    break;
                case PairRequestMessage m:
                    Opt(o, "token", m.Token);
                    Opt(o, "pin", m.Pin);
                    break;
                case PairResultMessage m:
                    o["ok"] = m.Ok;
                    Opt(o, "token", m.Token);
                    Opt(o, "reason", m.Reason);
                    Opt(o, "pinExpiresInSec", m.PinExpiresInSec);
                    Opt(o, "attemptsLeft", m.AttemptsLeft);
                    break;
                case HeartbeatMessage m:
                    Opt(o, "seq", m.Seq);
                    break;
                case StateMessage m:
                    o["dashboardUrl"] = m.DashboardUrl == null ? JValue.CreateNull() : new JValue(m.DashboardUrl);
                    Opt(o, "idleDashboardUrl", m.IdleDashboardUrl);
                    if (m.DashboardServer != null)
                        o["dashboardServer"] = new JObject { ["reachable"] = m.DashboardServer.Reachable, ["port"] = m.DashboardServer.Port };
                    var audio = m.Audio ?? new AudioInfo();
                    o["audio"] = new JObject
                    {
                        ["enabled"] = audio.Enabled,
                        ["port"] = audio.Port,
                        ["formats"] = new JArray((audio.Formats ?? new List<string>()).Cast<object>().ToArray()),
                    };
                    if (m.Mic != null) o["mic"] = new JObject { ["enabled"] = m.Mic.Enabled };
                    break;
                case StatusMessage m:
                    o["phoneConnected"] = m.PhoneConnected;
                    Opt(o, "phoneName", m.PhoneName);
                    o["screen"] = m.Screen;
                    if (m.NowPlaying == null)
                    {
                        o["nowPlaying"] = JValue.CreateNull();
                    }
                    else
                    {
                        var np = m.NowPlaying;
                        o["nowPlaying"] = new JObject
                        {
                            ["title"] = NullableString(np.Title),
                            ["artist"] = NullableString(np.Artist),
                            ["album"] = NullableString(np.Album),
                            ["app"] = NullableString(np.App),
                            ["playing"] = np.Playing,
                            ["position"] = np.Position,
                            ["duration"] = np.Duration.HasValue ? new JValue(np.Duration.Value) : JValue.CreateNull(),
                            ["updatedAt"] = np.UpdatedAt,
                        };
                    }
                    if (m.Nav != null)
                    {
                        var nav = new JObject();
                        Opt(nav, "maneuver", m.Nav.Maneuver);
                        Opt(nav, "distanceM", m.Nav.DistanceM);
                        Opt(nav, "road", m.Nav.Road);
                        Opt(nav, "etaEpochS", m.Nav.EtaEpochS);
                        o["nav"] = nav;
                    }
                    break;
                case ArtworkMessage m:
                    o["mime"] = m.Mime;
                    o["base64"] = m.Base64;
                    break;
                case CommandMessage m:
                    o["command"] = m.Command;
                    if (m.Command == Commands.Media) Opt(o, "action", m.Action);
                    break;
                case TelemetryMessage m:
                    Opt(o, "speedMps", m.SpeedMps);
                    Opt(o, "gear", m.Gear);
                    Opt(o, "heading", m.Heading);
                    Opt(o, "lat", m.Lat);
                    Opt(o, "lon", m.Lon);
                    Opt(o, "alt", m.Alt);
                    Opt(o, "night", m.Night);
                    Opt(o, "fuelPercent", m.FuelPercent);
                    Opt(o, "rangeKm", m.RangeKm);
                    Opt(o, "rpm", m.Rpm);
                    Opt(o, "trackName", m.TrackName);
                    Opt(o, "sessionType", m.SessionType);
                    Opt(o, "gameRunning", m.GameRunning);
                    break;
                case ErrorMessage m:
                    o["code"] = m.Code;
                    Opt(o, "message", m.Message);
                    Opt(o, "fatal", m.Fatal);
                    Opt(o, "refType", m.RefType);
                    Opt(o, "minProtocol", m.MinProtocol);
                    Opt(o, "maxProtocol", m.MaxProtocol);
                    break;
                case AudioStartMessage m:
                    o["stream"] = m.Stream;
                    o["format"] = m.Format;
                    o["sampleRate"] = m.SampleRate;
                    o["channels"] = m.Channels;
                    break;
                case AudioStopMessage m:
                    o["stream"] = m.Stream;
                    break;
                case MicStartMessage m:
                    o["streamType"] = m.StreamType;
                    o["format"] = m.Format;
                    o["sampleRate"] = m.SampleRate;
                    o["channels"] = m.Channels;
                    o["port"] = m.Port;
                    break;
                case MicStopMessage m:
                    o["streamType"] = m.StreamType;
                    break;
                default:
                    throw new ArgumentException("Unknown message class " + message.GetType().Name);
            }
            return o;
        }

        /// <summary>True when the two messages encode to the same JSON.</summary>
        public static bool Equivalent(Message a, Message b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(Encode(a), Encode(b), StringComparison.Ordinal);
        }

        // Parsing

        /// <summary>Parses exactly one JSON value; trailing content other than whitespace is an error.</summary>
        internal static JToken ParseStrict(string text)
        {
            if (text == null) throw new JsonReaderException("null input");
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = 64;
                var token = JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("additional content after the JSON value");
                }
                return token;
            }
        }

        private static readonly Dictionary<string, Func<JObject, Message>> Decoders = new Dictionary<string, Func<JObject, Message>>(StringComparer.Ordinal)
        {
            [MessageTypes.Beacon] = DecodeBeacon,
            [MessageTypes.Hello] = DecodeHello,
            [MessageTypes.Welcome] = DecodeWelcome,
            [MessageTypes.PairRequest] = DecodePairRequest,
            [MessageTypes.PairResult] = DecodePairResult,
            [MessageTypes.Heartbeat] = o => new HeartbeatMessage { Seq = OptInt(o, "seq", 0, 4294967295L) },
            [MessageTypes.State] = DecodeState,
            [MessageTypes.Status] = DecodeStatus,
            [MessageTypes.Command] = DecodeCommand,
            [MessageTypes.Telemetry] = DecodeTelemetry,
            [MessageTypes.Error] = DecodeError,
            [MessageTypes.AudioStart] = DecodeAudioStart,
            [MessageTypes.AudioStop] = o => new AudioStopMessage { Stream = ReqEnum(o, "stream", AudioStreams.All) },
            [MessageTypes.Artwork] = DecodeArtwork,
            [MessageTypes.MicStart] = DecodeMicStart,
            [MessageTypes.MicStop] = o => new MicStopMessage { StreamType = (int)ReqInt(o, "streamType", AudioStreams.MicStreamType, AudioStreams.MicStreamType) },
        };

        private static Message DecodeBeacon(JObject o)
        {
            var m = new BeaconMessage
            {
                Name = ReqString(o, "name"),
                HostId = ReqString(o, "hostId", 1, 128),
                Version = ReqString(o, "version"),
                SimhubVersion = OptString(o, "simhubVersion"),
                ControlPort = (int)ReqInt(o, "controlPort", 1, 65535),
                AudioPort = (int)ReqInt(o, "audioPort", 1, 65535),
                Protocol = (int)ReqInt(o, "protocol", 1, int.MaxValue),
                MinProtocol = (int?)OptInt(o, "minProtocol", 1, int.MaxValue),
            };
            if (m.MinProtocol > m.Protocol) throw new ProtocolException("minProtocol is greater than protocol");
            return m;
        }

        private static Message DecodeHello(JObject o)
        {
            var m = new HelloMessage
            {
                TabletId = ReqString(o, "tabletId", 1, 64),
                Name = ReqString(o, "name"),
                AppVersion = ReqString(o, "appVersion"),
                Protocol = (int)ReqInt(o, "protocol", 1, int.MaxValue),
                MinProtocol = (int?)OptInt(o, "minProtocol", 1, int.MaxValue),
                Features = OptStringArray(o, "features"),
            };
            if (m.MinProtocol > m.Protocol) throw new ProtocolException("minProtocol is greater than protocol");
            return m;
        }

        private static Message DecodeWelcome(JObject o)
        {
            return new WelcomeMessage
            {
                HostId = ReqString(o, "hostId", 1, 128),
                Name = ReqString(o, "name"),
                Version = ReqString(o, "version"),
                SimhubVersion = OptString(o, "simhubVersion"),
                Protocol = (int)ReqInt(o, "protocol", 1, int.MaxValue),
                Features = ReqStringArray(o, "features"),
            };
        }

        private static Message DecodePairRequest(JObject o)
        {
            var m = new PairRequestMessage
            {
                Token = OptString(o, "token", 1, 128),
                Pin = OptString(o, "pin"),
            };
            if (m.Pin != null && !IsSixDigits(m.Pin)) throw new ProtocolException("pin must be exactly 6 ASCII digits");
            if (m.Pin != null && m.Token != null) throw new ProtocolException("pairRequest has both token and pin");
            return m;
        }

        private static Message DecodePairResult(JObject o)
        {
            var m = new PairResultMessage
            {
                Ok = ReqBool(o, "ok"),
                Token = OptString(o, "token", 1, 128),
                Reason = OptEnum(o, "reason", PairReasons.All),
                PinExpiresInSec = (int?)OptInt(o, "pinExpiresInSec", 1, int.MaxValue),
                AttemptsLeft = (int?)OptInt(o, "attemptsLeft", 0, int.MaxValue),
            };
            if (m.Ok && m.Token == null) throw new ProtocolException("ok: true requires token");
            if (m.Ok && m.Reason != null) throw new ProtocolException("ok: true must not carry reason");
            if (!m.Ok && m.Reason == null) throw new ProtocolException("ok: false requires reason");
            if (!m.Ok && m.Token != null) throw new ProtocolException("ok: false must not carry token");
            return m;
        }

        private static Message DecodeState(JObject o)
        {
            var m = new StateMessage
            {
                DashboardUrl = ReqHttpUrlOrNull(o, "dashboardUrl"),
                IdleDashboardUrl = OptHttpUrl(o, "idleDashboardUrl"),
            };
            var server = OptObject(o, "dashboardServer");
            if (server != null)
            {
                m.DashboardServer = new DashboardServerInfo
                {
                    Reachable = ReqBool(server, "reachable"),
                    Port = (int)ReqInt(server, "port", 1, 65535),
                };
            }
            var audio = ReqObject(o, "audio");
            m.Audio = new AudioInfo
            {
                Enabled = ReqBool(audio, "enabled"),
                Port = (int)ReqInt(audio, "port", 1, 65535),
                Formats = ReqStringArray(audio, "formats"),
            };
            var mic = OptObject(o, "mic");
            if (mic != null) m.Mic = new MicInfo { Enabled = ReqBool(mic, "enabled") };
            return m;
        }

        private static Message DecodeStatus(JObject o)
        {
            var m = new StatusMessage
            {
                PhoneConnected = ReqBool(o, "phoneConnected"),
                PhoneName = OptString(o, "phoneName"),
                Screen = ReqEnum(o, "screen", Screens.All),
            };
            var np = ReqObjectOrNull(o, "nowPlaying");
            if (np != null)
            {
                m.NowPlaying = new NowPlaying
                {
                    Title = ReqStringOrNull(np, "title"),
                    Artist = ReqStringOrNull(np, "artist"),
                    Album = ReqStringOrNull(np, "album"),
                    App = ReqStringOrNull(np, "app"),
                    Playing = ReqBool(np, "playing"),
                    Position = ReqDecimal(np, "position", 0, double.MaxValue),
                    Duration = ReqPositiveDecimalOrNull(np, "duration"),
                    UpdatedAt = ReqInt(np, "updatedAt", 0, long.MaxValue),
                };
            }
            m.Nav = DecodeNav(o);
            return m;
        }

        /// <summary>
        /// status.nav, validated leniently like telemetry (spec §6.7): not an object means no route guidance; a member
        /// with the wrong type or out of range is null; the rest of the status is used either way.
        /// </summary>
        private static NavInfo DecodeNav(JObject o)
        {
            var nav = Member(o, "nav") as JObject;
            if (nav == null) return null;
            var maneuver = LenientString(nav, "maneuver");
            if (maneuver != null && (maneuver.Length == 0 || maneuver.Length > NavManeuvers.MaxLength)) maneuver = null;
            var road = LenientString(nav, "road");
            if (string.IsNullOrWhiteSpace(road)) road = null;
            // Whole metres on the wire; a decimal is accepted and rounded.
            var distance = LenientDecimal(nav, "distanceM", 0, int.MaxValue, true);
            long? eta = null;
            var etaToken = Member(nav, "etaEpochS");
            if (etaToken != null && etaToken.Type == JTokenType.Integer)
            {
                try
                {
                    var value = Convert.ToInt64(((JValue)etaToken).Value, CultureInfo.InvariantCulture);
                    if (value >= 0) eta = value;
                }
                catch (OverflowException) { }
            }
            return new NavInfo
            {
                Maneuver = maneuver,
                DistanceM = distance.HasValue ? (int?)Math.Round(distance.Value, MidpointRounding.AwayFromZero) : null,
                Road = road,
                EtaEpochS = eta,
            };
        }

        private static Message DecodeArtwork(JObject o)
        {
            var mime = ReqString(o, "mime", 1, 255);
            var data = ReqString(o, "base64", 1, int.MaxValue);
            if (!ArtworkFormats.All.Contains(mime)) throw new ProtocolException("mime value " + mime + " is not image/jpeg or image/png");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                throw new ProtocolException("base64 is not valid base64");
            }
            if (bytes.Length == 0) throw new ProtocolException("base64 holds no data");
            return new ArtworkMessage { Mime = mime, Base64 = data, Bytes = bytes };
        }

        private static Message DecodeCommand(JObject o)
        {
            var m = new CommandMessage { Command = ReqEnum(o, "command", Commands.AllCommands) };
            // action is ignored for commands other than media (spec §6.8).
            if (m.Command == Commands.Media) m.Action = ReqEnum(o, "action", Commands.AllMediaActions);
            return m;
        }

        private static Message DecodeTelemetry(JObject o)
        {
            // Validated field by field: a bad field is treated as null and the rest is used (spec §6.9).
            return new TelemetryMessage
            {
                SpeedMps = LenientDecimal(o, "speedMps", 0, double.MaxValue, true),
                Gear = LenientEnum(o, "gear", GearValues),
                Heading = LenientDecimal(o, "heading", 0, 360, false),
                Lat = LenientDecimal(o, "lat", -90, 90, true),
                Lon = LenientDecimal(o, "lon", -180, 180, true),
                Alt = LenientDecimal(o, "alt", double.MinValue, double.MaxValue, true),
                Night = LenientBool(o, "night"),
                FuelPercent = LenientDecimal(o, "fuelPercent", 0, 100, true),
                RangeKm = LenientDecimal(o, "rangeKm", 0, double.MaxValue, true),
                Rpm = LenientDecimal(o, "rpm", 0, double.MaxValue, true),
                TrackName = LenientString(o, "trackName"),
                SessionType = LenientString(o, "sessionType"),
                GameRunning = LenientBool(o, "gameRunning"),
            };
        }

        private static readonly HashSet<string> GearValues = new HashSet<string>(StringComparer.Ordinal) { "P", "R", "N", "D" };

        private static Message DecodeError(JObject o)
        {
            var m = new ErrorMessage
            {
                Code = ReqString(o, "code"),
                Message = OptString(o, "message"),
                Fatal = OptBool(o, "fatal"),
                RefType = OptString(o, "refType"),
                MinProtocol = (int?)OptInt(o, "minProtocol", 1, int.MaxValue),
                MaxProtocol = (int?)OptInt(o, "maxProtocol", 1, int.MaxValue),
            };
            if (m.Code == ErrorCodes.UnsupportedProtocol && (m.MinProtocol == null || m.MaxProtocol == null))
                throw new ProtocolException("unsupportedProtocol requires minProtocol and maxProtocol");
            return m;
        }

        private static Message DecodeAudioStart(JObject o)
        {
            var m = new AudioStartMessage
            {
                Stream = ReqEnum(o, "stream", AudioStreams.All),
                Format = ReqEnum(o, "format", AudioStreams.Formats),
                SampleRate = (int)ReqInt(o, "sampleRate", 8000, 48000),
                Channels = (int)ReqInt(o, "channels", 1, 2),
            };
            if (m.SampleRate % 100 != 0) throw new ProtocolException("sampleRate must be a multiple of 100");
            if (m.Format == AudioStreams.Opus && !IsOpusRate(m.SampleRate)) throw new ProtocolException("sampleRate for opus must be 8000, 12000, 16000, 24000 or 48000");
            return m;
        }

        /// <summary>The sample rates an Opus stream may announce (§10.4).</summary>
        public static bool IsOpusRate(int hz)
        {
            return hz == 8000 || hz == 12000 || hz == 16000 || hz == 24000 || hz == 48000;
        }

        private static Message DecodeMicStart(JObject o)
        {
            var m = new MicStartMessage
            {
                StreamType = (int)ReqInt(o, "streamType", AudioStreams.MicStreamType, AudioStreams.MicStreamType),
                Format = ReqEnum(o, "format", AudioStreams.MicFormats),
                SampleRate = (int)ReqInt(o, "sampleRate", 8000, 48000),
                Channels = (int)ReqInt(o, "channels", 1, 1),
                Port = (int)ReqInt(o, "port", 1, 65535),
            };
            if (m.SampleRate % 100 != 0) throw new ProtocolException("sampleRate must be a multiple of 100");
            return m;
        }

        // Member readers. Each throws ProtocolException with the member name when the member breaks the rules.

        private static JToken Member(JObject o, string name)
        {
            JToken t;
            return o.TryGetValue(name, out t) ? t : null;
        }

        private static bool IsAbsentOrNull(JToken t)
        {
            return t == null || t.Type == JTokenType.Null;
        }

        private static string ReqString(JObject o, string name, int minLength = 0, int maxLength = int.MaxValue)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            return AsString(t, name, minLength, maxLength);
        }

        private static string OptString(JObject o, string name, int minLength = 0, int maxLength = int.MaxValue)
        {
            var t = Member(o, name);
            return IsAbsentOrNull(t) ? null : AsString(t, name, minLength, maxLength);
        }

        /// <summary>Required, but may be null (spec §3: present with the value null).</summary>
        private static string ReqStringOrNull(JObject o, string name)
        {
            var t = Member(o, name);
            if (t == null) throw new ProtocolException(name + " is required (may be null)");
            return t.Type == JTokenType.Null ? null : AsString(t, name, 0, int.MaxValue);
        }

        private static string AsString(JToken t, string name, int minLength, int maxLength)
        {
            if (t.Type != JTokenType.String) throw new ProtocolException(name + " must be a string");
            var s = (string)t;
            if (s.Length < minLength || s.Length > maxLength) throw new ProtocolException(name + " length must be " + minLength + "-" + maxLength);
            return s;
        }

        private static string ReqEnum(JObject o, string name, HashSet<string> values)
        {
            var s = ReqString(o, name);
            if (!values.Contains(s)) throw new ProtocolException(name + " value " + s + " is not defined");
            return s;
        }

        private static string OptEnum(JObject o, string name, HashSet<string> values)
        {
            var s = OptString(o, name);
            if (s != null && !values.Contains(s)) throw new ProtocolException(name + " value " + s + " is not defined");
            return s;
        }

        private static long ReqInt(JObject o, string name, long min, long max)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            return AsInt(t, name, min, max);
        }

        private static long? OptInt(JObject o, string name, long min, long max)
        {
            var t = Member(o, name);
            return IsAbsentOrNull(t) ? (long?)null : AsInt(t, name, min, max);
        }

        private static long AsInt(JToken t, string name, long min, long max)
        {
            if (t.Type != JTokenType.Integer) throw new ProtocolException(name + " must be an integer");
            long value;
            try
            {
                value = Convert.ToInt64(((JValue)t).Value, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw new ProtocolException(name + " is out of range");
            }
            if (value < min || value > max) throw new ProtocolException(name + " must be in " + min + "-" + max);
            return value;
        }

        private static bool ReqBool(JObject o, string name)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            if (t.Type != JTokenType.Boolean) throw new ProtocolException(name + " must be a boolean");
            return (bool)t;
        }

        private static bool? OptBool(JObject o, string name)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) return null;
            if (t.Type != JTokenType.Boolean) throw new ProtocolException(name + " must be a boolean");
            return (bool)t;
        }

        private static double ReqDecimal(JObject o, string name, double min, double max)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            double value;
            if (!TryDecimal(t, out value)) throw new ProtocolException(name + " must be a finite number");
            if (value < min || value > max) throw new ProtocolException(name + " is out of range");
            return value;
        }

        private static double? ReqPositiveDecimalOrNull(JObject o, string name)
        {
            var t = Member(o, name);
            if (t == null) throw new ProtocolException(name + " is required (may be null)");
            if (t.Type == JTokenType.Null) return null;
            double value;
            if (!TryDecimal(t, out value)) throw new ProtocolException(name + " must be a finite number");
            if (value <= 0) throw new ProtocolException(name + " must be greater than 0");
            return value;
        }

        private static bool TryDecimal(JToken t, out double value)
        {
            value = 0;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return false;
            try
            {
                value = Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return false;
            }
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static JObject ReqObject(JObject o, string name)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            var obj = t as JObject;
            if (obj == null) throw new ProtocolException(name + " must be an object");
            return obj;
        }

        private static JObject OptObject(JObject o, string name)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) return null;
            var obj = t as JObject;
            if (obj == null) throw new ProtocolException(name + " must be an object");
            return obj;
        }

        private static JObject ReqObjectOrNull(JObject o, string name)
        {
            var t = Member(o, name);
            if (t == null) throw new ProtocolException(name + " is required (may be null)");
            if (t.Type == JTokenType.Null) return null;
            var obj = t as JObject;
            if (obj == null) throw new ProtocolException(name + " must be an object or null");
            return obj;
        }

        private static List<string> ReqStringArray(JObject o, string name)
        {
            var t = Member(o, name);
            if (IsAbsentOrNull(t)) throw new ProtocolException(name + " is required");
            return AsStringArray(t, name);
        }

        private static List<string> OptStringArray(JObject o, string name)
        {
            var t = Member(o, name);
            return IsAbsentOrNull(t) ? null : AsStringArray(t, name);
        }

        private static List<string> AsStringArray(JToken t, string name)
        {
            var array = t as JArray;
            if (array == null) throw new ProtocolException(name + " must be an array");
            var list = new List<string>();
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String) throw new ProtocolException(name + " must contain strings only");
                list.Add((string)item);
            }
            return list;
        }

        private static string ReqHttpUrlOrNull(JObject o, string name)
        {
            var t = Member(o, name);
            if (t == null) throw new ProtocolException(name + " is required (may be null)");
            return t.Type == JTokenType.Null ? null : AsHttpUrl(t, name);
        }

        private static string OptHttpUrl(JObject o, string name)
        {
            var t = Member(o, name);
            return IsAbsentOrNull(t) ? null : AsHttpUrl(t, name);
        }

        private static string AsHttpUrl(JToken t, string name)
        {
            var s = AsString(t, name, 1, int.MaxValue);
            Uri uri;
            // http is what the plugin sends; https is accepted too, for a future TLS-fronted web dash server.
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ProtocolException(name + " must be an absolute http or https URL");
            return s;
        }

        private static double? LenientDecimal(JObject o, string name, double min, double maxInclusiveOrExclusive, bool maxInclusive)
        {
            var t = Member(o, name);
            double value;
            if (IsAbsentOrNull(t) || !TryDecimal(t, out value)) return null;
            if (value < min) return null;
            if (maxInclusive ? value > maxInclusiveOrExclusive : value >= maxInclusiveOrExclusive) return null;
            return value;
        }

        private static string LenientEnum(JObject o, string name, HashSet<string> values)
        {
            var s = LenientString(o, name);
            return s != null && values.Contains(s) ? s : null;
        }

        private static string LenientString(JObject o, string name)
        {
            var t = Member(o, name);
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        private static bool? LenientBool(JObject o, string name)
        {
            var t = Member(o, name);
            return t != null && t.Type == JTokenType.Boolean ? (bool)t : (bool?)null;
        }

        internal static bool IsSixDigits(string s)
        {
            if (s == null || s.Length != 6) return false;
            foreach (var c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        // Writers

        private static void Opt(JObject o, string name, string value)
        {
            if (value != null) o[name] = value;
        }

        private static void Opt(JObject o, string name, int? value)
        {
            if (value.HasValue) o[name] = value.Value;
        }

        private static void Opt(JObject o, string name, long? value)
        {
            if (value.HasValue) o[name] = value.Value;
        }

        private static void Opt(JObject o, string name, double? value)
        {
            // Not finite is not JSON (spec §3): left out, which the receiver reads as null.
            if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)) o[name] = value.Value;
        }

        private static void Opt(JObject o, string name, bool? value)
        {
            if (value.HasValue) o[name] = value.Value;
        }

        private static JToken NullableString(string value)
        {
            return value == null ? JValue.CreateNull() : new JValue(value);
        }
    }
}
