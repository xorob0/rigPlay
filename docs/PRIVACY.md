# Privacy and diagnostics

rigPlay has no account, no server and no analytics. The tablet talks to the iPhone directly (Wi-Fi Direct,
Bluetooth or USB) and to the SimHub plugin on your home network. Nothing is uploaded automatically. The
iPhone's CarPlay apps have their own internet and privacy behaviour.

## What is stored

On the tablet, in app storage: settings, the selected iPhone, pairing data for the iPhone and for the PC
(including the token the PC issued), and bounded diagnostic logs. Authentication and pairing material are
kept out of Android backup. Uninstalling removes app storage; reports you saved to Downloads stay until you
delete them.

On the PC, in SimHub's `PluginsData\Common\RigPlay.RigPlaySettings.json`: the plugin's settings, the chosen
dashboards and audio device, and for each paired tablet its ID, name, pairing date and a SHA-256 hash of
its token. Log lines in SimHub's log are prefixed `[rigPlay]` and never contain a full token.

## What crosses your network

- The plugin broadcasts a discovery beacon on the local network with the PC's name, a random host ID, the
  SimHub version and its ports.
- The control channel between tablet and PC carries pairing, status and now-playing metadata (title,
  artist, album, app). CarPlay audio is sent from the tablet to the PC.
- Protocol 1 is **not encrypted**. Anyone on the same network can read the control channel, including the
  pairing token and now-playing metadata, and can capture the audio. The plugin accepts connections only
  from private and link-local addresses. Use rigPlay on a home network you trust. The reasons and limits
  are in [docs/protocol.md §15](protocol.md#15-security).

## Diagnostic reports

You start a diagnostic export yourself (**Settings → Diagnostics → Save diagnostic report**). Reports
include app and device versions, display settings, connection transitions, Wi-Fi band, channel and state,
and decoder recovery events. The exporter filters protocol payloads, credential-bearing lines and common
identifiers. Redaction cannot recognise every vendor-specific string: review a report before posting it.
A GitHub issue is public.

## Permissions

- Nearby devices (and Location on older Android): Bluetooth and Wi-Fi Direct discovery and connection.
- Microphone: Siri and calls.
- Notifications: connection controls in the foreground notification.
- Local VPN (optional): carries the USB link to the iPhone. It is not an internet VPN.

GitHub applies its own policies when you download releases or open issues.
