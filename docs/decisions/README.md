# Decision records

One file per decision that would otherwise have to be re-derived from the code. A record says what
was decided, what it was decided against, and what it costs; the code says what it is.

Where a record and [`docs/protocol.md`](../protocol.md) overlap, the protocol document is the
contract and the record explains why it is that way.

## Accepted

| | | |
|---|---|---|
| 0001 | [Monorepo](0001-monorepo.md) | The Android app stays at the root; the SimHub plugin lives in `plugin/`; the protocol and its fixtures are shared |
| 0002 | [The plugin builds on Linux](0002-plugin-builds-on-linux.md) | net48, code-only WPF, SimHub DLLs in `plugin/lib`, net8 tests; no Windows in the build |
| 0003 | [Plain TCP and JSON lines](0003-protocol-plain-tcp-json.md) | `TcpListener` and newline-delimited JSON, UDP beacon, no `HttpListener`, no TLS in protocol 1 |
| 0004 | [Dashboards through the web dash server](0004-dashboard-via-web-dash-server.md) | The tablet loads SimHub's own dashboard pages; the plugin only sends the URL |
| 0005 | [Audio as PCM over UDP](0005-audio-pcm-over-udp.md) | s16le PCM with a 12-byte header, jitter buffer on the PC, Opus later |
| 0006 | [Opus is optional, not the default](0006-opus-optional.md) | The plugin's setting offers Opus before PCM; Concentus decodes, Android's encoder encodes; PCM stays the baseline |

## Writing one

Copy the shape of an existing record: title, **Date**, **Status**, then Context, Decision and
Consequences. Context says what forced a choice and lists the alternatives considered; a record that
names only the option taken is a summary of the code, not a decision. Consequences include the costs
and anything still unresolved.

Numbers are assigned in order when the record is started. A decision that replaces an earlier one gets
a new record; the old one's status becomes "Superseded by NNNN" and its text stays as it was.
