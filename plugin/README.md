# rigPlay SimHub plugin

The PC side of rigPlay: a SimHub plugin with its own page in SimHub's left menu. It discovers and pairs
rigPlay tablets, pushes the SimHub dashboard they show, and plays their CarPlay audio on this PC (epic #3).

```
plugin/
  RigPlay/          the plugin (net48), builds RigPlay.dll
  RigPlay.Tests/    xunit tests (net8.0) for the plugin's pure logic
  lib/              SimHub 9.12.6 assemblies the plugin compiles against (see lib/README.md)
  scripts/          package-plugin.sh (the release zip), make-icon.py (RigPlay/Resources/icon.png)
  tools/            AudioSender, a tablet-less audio source for testing
  INSTALL.md        install and troubleshooting for users; shipped in the zip
```

## Build and test

Needs the .NET 8 SDK; no Windows, Visual Studio or SimHub install.

```bash
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release   # -> plugin/RigPlay/bin/Release/net48/RigPlay.dll
bash plugin/scripts/package-plugin.sh    # builds, then -> build/rigPlay-plugin.zip (RigPlay.dll + INSTALL.md)
```

The release workflow publishes the same zip (`package-plugin.sh --no-build --out dist/rigPlay-plugin.zip`).
It holds no SimHub assemblies and no `.pdb`: SimHub ships everything the plugin references.

The plugin targets .NET Framework 4.8 through the `Microsoft.NETFramework.ReferenceAssemblies` package and
references WPF as plain assemblies, without `UseWPF` or XAML: the page is built in C# and picks up SimHub's own
controls and styles at runtime. That keeps the build cross-platform. The files the tests compile
(`ProtocolDefaults.cs`, `RigPlaySettings.cs`, `Theme.cs` and everything under `Core/`, `Protocol/`, `Net/`,
`Pairing/`, `Dashboards/` and `Telemetry/`)
must stay free of SimHub and WPF types; SimHub-facing glue lives in `RigPlay.cs`, `PluginBridge.cs` and the page.

## Tablet server

The plugin implements [`docs/protocol.md`](../docs/protocol.md); `RigPlay.Tests/ProtocolFixturesTests.cs` checks the
codec against every file in [`protocol/fixtures/`](../protocol/fixtures/).

| Part | File | What it does |
|---|---|---|
| Messages | `Protocol/Messages.cs` | Typed messages and `MessageCodec` (decode, validate, encode). |
| Audio header | `Protocol/AudioHeader.cs` | The 12-byte datagram header codec (§10.2). |
| Beacon | `Net/DiscoveryBeacon.cs` | UDP 23710 every second, to each interface's directed broadcast and 255.255.255.255. |
| Control server | `Net/ControlServer.cs`, `Net/ClientSession.cs` | TCP 23711 (configurable), newline JSON, hello/welcome, heartbeats, 5 s watchdog, one session per tablet, `shutdown` on exit, LAN peers only. |
| Pairing | `Pairing/PairingService.cs` | PIN shown on the page (6 digits, 120 s, single use, 3 attempts, 5 starts/min), token issue and resume. Only the token's SHA-256 is stored (`PairedTablets[].TokenHash`). |
| Dashboards | `Dashboards/DashboardCatalog.cs`, `Dashboards/WebDashProbe.cs` | Lists `<SimHub>\DashTemplates\*` (title from `<name>.djson.metadata`), builds `http://<ip>:<port>/Dash#<name>` (spec §11; `/Dash#<name>` is what SimHub's own dashboard list links to, `/dashboard/<name>` is a 404 on SimHub 9.12.6) with the address the tablet reached the PC on, probes `http://127.0.0.1:<port>/` every 10 s. The port is the page's override, else SimHub's `SimHubWebPort` setting, else 8888. |
| Host | `Core/RigPlayHost.cs` | Runs the above inside SimHub without depending on it; the page and the SimHub glue read it. |

## SimHub surface

What dashboards and input mappings can use (docs/protocol.md §16). Properties describe the *primary tablet*: the
paired tablet whose iPhone connected most recently, else the one that paired most recently (§12). Actions send a
`command` to it; with no primary tablet they do nothing (logged at debug level). The logic is in
`Core/SimHubSurface.cs` (`PrimaryTabletSelector`, `PlaybackClock`, `SurfaceActions`); `PluginBridge.cs` registers it.

| Name | Kind | Type | Value / effect | Without data |
|---|---|---|---|---|
| `RigPlay.TabletConnected` | property | bool | At least one paired tablet is connected (any tablet, not only the primary). | `false` |
| `RigPlay.PhoneConnected` | property | bool | `status.phoneConnected` | `false` |
| `RigPlay.Screen` | property | string | `carplay`, `dashboard`, `idle` or `off` (`status.screen`) | `off` |
| `RigPlay.NowPlaying.Title` | property | string | `status.nowPlaying.title` | `""` |
| `RigPlay.NowPlaying.Artist` | property | string | `status.nowPlaying.artist` | `""` |
| `RigPlay.NowPlaying.Album` | property | string | `status.nowPlaying.album` | `""` |
| `RigPlay.NowPlaying.App` | property | string | `status.nowPlaying.app` | `""` |
| `RigPlay.NowPlaying.Playing` | property | bool | `status.nowPlaying.playing` | `false` |
| `RigPlay.NowPlaying.Position` | property | double, s | Last `position` plus the time since that status arrived while playing, clamped to the duration. | `0` |
| `RigPlay.NowPlaying.Duration` | property | double, s | `status.nowPlaying.duration` (`0` for live streams) | `0` |
| `RigPlay.NowPlaying.ArtworkPath` | property | string | File with the last `artwork` (§6.14): `%TEMP%\rigPlay\artwork.jpg` (`.png` for PNG), replaced atomically | `""` |
| `RigPlay.Nav.Active` | property | bool | `status.nav` present: CarPlay route guidance is active (§6.7.1) | `false` |
| `RigPlay.Nav.Maneuver` | property | string | `status.nav.maneuver`, Apple's maneuver name in lowerCamel (`slightRightTurn`, `roundaboutExit2`, ...) | `""` |
| `RigPlay.Nav.Distance` | property | double, m | `status.nav.distanceM` | `0` |
| `RigPlay.Nav.Road` | property | string | `status.nav.road` | `""` |
| `RigPlay.Nav.Eta` | property | string | `status.nav.etaEpochS` as local `HH:mm` | `""` |
| `RigPlay.PlayPause` | action | | `command media playPause` | |
| `RigPlay.NextTrack` | action | | `command media next` | |
| `RigPlay.PreviousTrack` | action | | `command media previous` | |
| `RigPlay.Siri` | action | | `command media siri` | |
| `RigPlay.ShowDashboard` | action | | `command showDashboard` | |
| `RigPlay.ShowCarPlay` | action | | `command showCarPlay` | |
| `RigPlay.ToggleScreen` | action | | `showCarPlay` when the last `status.screen` is `dashboard`, otherwise `showDashboard` | |

Nav and artwork come from the tablet (#47): `status.nav` and the `artwork` message; `Core/ArtworkFile.cs` writes the
primary tablet's image next to the old one and swaps it in with `File.Replace`.

Bind an action in SimHub under Controls and events → the rigPlay entries; use a property in a dashboard as
`[RigPlay.NowPlaying.Title]`.

## Data to CarPlay (#40)

The plugin is an `IDataPlugin`: `RigPlay.DataUpdate` (60 Hz) copies each SimHub frame into a `TelemetryInput`
struct without allocating, and `Telemetry/TelemetrySampler.cs` keeps the latest frame, the heading and the fake-GPS
strategy. `Telemetry/TelemetrySender.cs` runs a 100 ms timer that sends `telemetry` (docs/protocol.md §6.9) to every
paired tablet that named feature `telemetry` and reports a connected iPhone, while the page's master switch is on, at
least one field is on, and a game runs. When the game stops it sends one `{"type":"telemetry","gameRunning":false}`.

The page's "Data to CarPlay" section has the master switch, one switch per field (speed, gear, heading, night mode,
fuel level, range, RPM, track name, session type; `gameRunning` is always sent), the position strategy
(`Telemetry/GpsStrategy.cs`) with its settings, and a live line with the last message sent. Settings live in
`RigPlaySettings.Telemetry` (schema 3; schema 4 adds `Tracks`, #44).

| Field | From SimHub |
|---|---|
| `speedMps` | `SpeedKmh` / 3.6 |
| `gear` | `Gear`: R → `R`, 1.. → `D`, N → `P` in the pit lane or box, else `N` |
| `heading` | `OrientationYaw` once it is non-zero in the session, else the direction of movement in `CarCoordinates` |
| `rpm`, `trackName`, `sessionType` | `Rpms`, `TrackNameWithConfig` (else `TrackName`), `SessionTypeName` |
| `night` (#45) | Page "Night mode": Always day / Always night, or Auto: the optional night property, else the in-game clock (iRacing `SessionTimeOfDay`, ACC `Graphics.Clock`; night 19:00–07:00), else headlights (ACC `LightsStage`, rF2/LMU `mHeadlights`) |
| `fuelPercent` (#46) | `FuelPercent`, else `Fuel` / `MaxFuel`; absent when the game publishes no fuel |
| `rangeKm` (#46) | `EstimatedFuelRemaingLaps` × `TrackLength` (else `ReportedTrackLength`) / 1000 |
| `lat`, `lon`, `alt` | The position strategy (below) |

Position strategies (`Telemetry/GpsStrategy.cs`, `IGpsStrategy`; `Update` runs per frame, `TryGetFix` at 10 Hz):

- **Off**: no position.
- **Fixed position** (#42, `FixedOriginStrategy`): always the origin typed on the page (latitude, longitude,
  altitude; pasting "lat, lon" from a map into the latitude box fills both). Maps shows the car there while speed,
  gear and heading are the sim's; heading is 0 while the game publishes no yaw.
- **Drive around the origin** (#43, `DeadReckoningStrategy.cs`): starts at the origin and integrates speed × heading
  on every frame (great-circle step, `GeoMath`). Back to the origin on game start, session restart, a new track or
  session type, pit exit, after standing still for the set time (default 30 s, 0 never), and beyond the drift radius
  (default 20 km); the page also has a "Back to the origin now" button.
- **Real track** (#44, `TrackGeoReferenceStrategy.cs`): the car on the real circuit. Per track (key: `TrackCode`,
  else the track name, normalised by `TrackKeys`) the user's `TrackCalibration` (in `RigPlaySettings.Telemetry.Tracks`,
  schema 4) wins over the shipped table `Resources/tracks.json` (embedded as `RigPlay.Tracks.json`, read by
  `ShippedTracks`; eight circuits' start/finish lines, approximate). Then: a recorded centreline interpolated by
  `TrackPositionPercent` (`CentrelineMap`, wraps across the line); else `CarCoordinates` through the affine mapping
  (`AffineMap`: origin + rotation · scale · (x, z), axis swap/flip, per-game defaults in `GameAxes`); else dead
  reckoning from the track's origin. `RigPlay.DataUpdate` adds `TrackPositionPercent` per frame and `TrackCode` and
  `GameName` once a second to `TelemetryInput`.
  The page rows (`TrackSection.cs`) show the track key, the calibration source (none / shipped / yours / with a
  recorded lap) and the placing mode, and edit origin, rotation, scale and axes; "Set origin to here" turns the
  car's current position (and world point) into the origin; "Start recording" records the next full lap from line
  to line every 0.5 % (`LapRecorder`, closes the loop by spreading the drift) and saves it as the centreline.
  How-to and caveats: `docs/TRACK_CALIBRATION.md`.

## Notes

A port that cannot be bound is shown in the page's Status section and logged; the plugin keeps running. To poke the
server by hand: `nc <pc> 23711`, then type
`{"type":"hello","tabletId":"nc","name":"nc","appVersion":"0","protocol":1}` and Enter; the plugin answers `welcome`.

## Install

[INSTALL.md](INSTALL.md) has the user steps. In short: copy `RigPlay.dll` next to `SimHubWPF.exe`
(`C:\Program Files (x86)\SimHub\`), `Unblock-File` it, start SimHub and accept the "new plugin found"
prompt. **rigPlay** then appears in the left menu. Settings are stored in
`PluginsData\Common\RigPlay.RigPlaySettings.json`; log lines are prefixed `[rigPlay]` in SimHub's log.

## Audio receiver (#24)

`RigPlay/Audio/` receives the tablet's audio datagrams (docs/protocol.md §10) on the audio UDP port and plays
them through NAudio's `WasapiOut` (shared mode) on the device picked on the page:

- `AudioHeader.cs` is the 12-byte header codec, tested against `protocol/fixtures/audio-header.json`; `OpusToc`
  in it reads an Opus packet's TOC byte so an opus datagram is checked and placed without a decoder.
- `OpusSupport.cs` decodes the optional Opus format (§10.4) with Concentus, the managed libopus, which ships as
  `Concentus.dll` next to `RigPlay.dll` (the one assembly SimHub does not have). Everything that touches it is
  behind `NoInlining`, so a plugin installed without the DLL still loads and offers PCM only.
- `JitterBuffer.cs` holds one stream: placed by timestamp, 80 ms target, 200 ms maximum, silence for gaps and
  underruns, late and duplicate datagrams dropped, reset on the start flag.
- `AudioReceiver.cs` runs the socket and the stream lifecycle. A stream starts with the `audioStart` of a paired
  tablet and belongs to that tablet's IP; datagrams from any other source are dropped and counted as rejected.
  It stops on that tablet's `audioStop`, when its session closes, or (from the mix) after 2 s without datagrams.
  `OpusEnabled` (the **Opus compression** setting, off by default) puts `opus` before `pcm_s16le` in
  `state.audio.formats` and lets `audioStart` name it; an opus stream decodes each packet on the receive thread
  into the same jitter buffer.
- `AudioGlue.cs` connects the receiver to the control server: `audioStart`/`audioStop`/session loss, the
  paired-IP source filter, and `state.audio` (`enabled` while the audio port is bound, even with no output
  device; the tablet then streams and the page shows that nothing plays). Changing the audio port on the page
  rebinds the receiver and pushes the new `state.audio.port`.
- `AudioOutput.cs` mixes the streams at 48 kHz stereo float (resampling where needed), ducks media by 12 dB
  while Siri or a call plays, applies volume and mute live, and follows device removal back to the Windows
  default. Without any output device it keeps pulling the mix in real time so the stats stay live, and logs
  that once.
- `AudioMath.cs`, `AudioStats.cs`, the receiver and the glue are pure and unit-tested; `AudioOutput.cs` and
  `AudioSection.cs` (the page section) need Windows and are not.

`tools/AudioSender/` streams a WAV file or a tone as spec datagrams, PCM or Opus, for testing without a tablet:

```bash
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 music.wav --loss 5
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 --tone 440 --seconds 10
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 music.wav --opus 96
```

The plugin plays these only after a tablet on the sender's address has paired and sent `audioStart` for the
stream (for example a scripted fake tablet on the same PC, then the sender to 127.0.0.1); anything else shows
up as "rejected" in the Audio section's datagram counter.

## PC microphone to the phone (#34)

The other direction (docs/protocol.md §6.13, §10.4): a tablet with feature `mic` sends `micStart` when the
phone opens its microphone for Siri or a call, and the plugin sends the PC microphone to the tablet's UDP port
(23713) as 5 ms datagrams with the §10.2 header, `streamType` 4, 1 channel, at the rate the tablet asked for.

- `MicSender.cs` (pure, unit-tested): `MicPacketizer` cuts mono s16 into 5 ms datagrams (seq and timestamp
  from 0 per `micStart`, start flag on the first); `MicSender` runs one stream at a time (the latest
  `micStart` takes it over) and stops on `micStop` from the owning session, when that session closes, after
  2 s without a line from it, or when **Microphone to the phone** is switched off; it keeps the page's stats
  and level meter. `MicGlue` connects it to the host's `MicStart`/`MicStop`/`PairedSessionClosed` events and
  sets `state.mic.enabled` (setting on and an input device present), pushed to tablets when it changes.
- `MicCapture.cs` (NAudio, Windows only): `WasapiCapture` in shared mode on the chosen input device
  (`MMDeviceEnumerator`, `DataFlow.Capture`; the Windows default recording device when none is chosen or the
  chosen one is unplugged), mixed down to mono and resampled with `WdlResamplingSampleProvider`. Without an
  input device `micStart` fails, the plugin logs it once and `state.mic.enabled` turns false, so tablets use
  their own microphone.
- `MicSection.cs`: the page's Microphone section (the switch, the input device picker, state, level, last event).
- Settings (schema 4): `MicEnabled` (default on: nothing is captured until a tablet asks) and `MicDeviceId`.
- No echo cancellation: with PC speakers a caller can hear themselves. Recommend a headset.
