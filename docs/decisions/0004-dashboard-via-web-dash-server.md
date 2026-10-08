# ADR 0004: The tablet shows SimHub dashboards through SimHub's web dash server

**Date:** 2026-10-03
**Status:** Accepted

## Context

Next to CarPlay, the tablet shows a SimHub dashboard: the one opened by the SimHub button, and an idle
dashboard while no phone is connected. Options considered:

- **Render on the PC and stream video to the tablet.** Costs GPU and encoder time on the PC that is
  also running the sim, adds latency, and needs a video pipeline in the plugin.
- **Reimplement a dashboard renderer in the app.** SimHub dashboards are a large, evolving format; a
  second renderer would always lag behind SimHub's.
- **Use SimHub's web dash server.** SimHub already serves any installed dashboard over HTTP at
  `http://<pc>:8888/Dash#<Name>` with live data, to any browser on the LAN. This was verified
  on the VM with SimHub 9.12.6. (First written as `/dashboard/<Name>`, which SimHub 9.12.6 answers
  with 404; corrected 2026-10-03, see protocol §11.)

## Decision

The tablet loads the dashboard in a WebView from SimHub's web dash server. The plugin does not serve
dashboards itself. It lists `<SimHub>\DashTemplates\`, lets the user choose the dashboards on its
settings page, and sends ready-to-load URLs in `state.dashboardUrl` and `state.idleDashboardUrl`.

The plugin builds the URL from the local address of the tablet's TCP connection (`Socket.LocalEndPoint`)
rather than from a list of interfaces, so a PC with several network adapters gives each tablet an
address it can reach. The plugin probes the web dash server and reports `dashboardServer.reachable`,
so the tablet can tell the user to enable it instead of showing a browser error.

## Consequences

Dashboards look and behave exactly as in SimHub, and any dashboard the user installs works without
changes to rigPlay. The feature depends on the user enabling the web dash server in SimHub's settings;
the plugin detects and explains it but cannot turn it on. Rendering cost moves to the tablet's WebView,
so heavy dashboards may run slowly on low-end tablets; that is a dashboard choice the user can change.
If SimHub changes the URL scheme or port, only the plugin's URL builder changes.
