# ADR 0003: The control channel is plain TCP with newline-delimited JSON

**Date:** 2026-10-03
**Status:** Accepted

## Context

The tablet and the plugin exchange a handful of small messages: pairing, heartbeats, settings
(`state`), the tablet's status, media commands and, later, telemetry at up to 10 Hz. The plugin runs
inside SimHub on Windows, without administrator rights, and must reach the tablet across a home LAN.

Options considered:

- **HTTP or WebSocket with `HttpListener`.** Listening on anything other than localhost needs a URL
  ACL (`netsh http add urlacl`), which needs administrator rights once per port. Users would hit
  "access denied" on first start.
- **SimHub's own web server (port 8888).** It serves dashboards, but there is no supported way for a
  plugin to add endpoints to it.
- **gRPC or another RPC framework.** Brings code generation and runtime dependencies on two platforms
  (net48 and Android) for a small message set, and is hard to inspect by hand.
- **MQTT or another broker.** Needs a broker process on the PC.
- **A WebSocket library on a raw socket.** Removes the URL ACL problem but adds a dependency and a
  handshake for no benefit over a plain stream.

## Decision

The plugin listens with `TcpListener` on a fixed, configurable port and the tablet connects. Each
message is one JSON object on one line. Both sides send a heartbeat every second; five seconds without
a line means the link is lost. The tablet finds the PC through a UDP broadcast beacon. Messages carry
an integer protocol version negotiated in the first exchange. The full contract is
[`docs/protocol.md`](../protocol.md).

Protocol 1 has no TLS. The traffic stays on the LAN, the plugin refuses non-private source addresses,
and pairing uses a PIN shown on the PC and a token bound to the tablet and the host.

## Consequences

`TcpListener` needs no administrator rights; Windows Firewall asks once. The protocol can be driven by
hand (`nc <pc> 23711` and a typed `hello`), which makes debugging on the VM easy. Each side owns its own
framing, heartbeat, reconnect logic and JSON mapping, with no generated code; the fixture tests are
what keeps the two in agreement.

Anyone on the LAN can read the traffic, including the pairing token. That is accepted for a sim rig
on a home network and recorded in the spec's security section. Adding TLS later (self-signed
certificate pinned at pairing) does not change the message set but needs a new protocol version.
