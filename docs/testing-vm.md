# Testing the plugin on the Windows VM

Plugin changes are verified in a real SimHub (see [CONTRIBUTING.md](../CONTRIBUTING.md)). For that, a
Windows 10 VM with SimHub 9.12.6 runs on the development host as the `winvm` container, managed from
`/opt/winvm`. It is shared with the OpenDash project, so other sessions may be using it.

| | |
|---|---|
| Guest | Windows 10 Pro, 2 vCPU, 4 GB, 3840x2160 display, local admin user from `WIN_USERNAME` in `/opt/winvm/.env` |
| SimHub | 9.12.6, `C:\Program Files (x86)\SimHub\`, free edition, started by the scheduled task `SimHub` |
| Guest SSH | `ssh -i /opt/winvm/ssh/id_ed25519 -p 2222 <WIN_USERNAME>@127.0.0.1` (session 0, no desktop) |
| Display | QEMU VNC on `127.0.0.1:5900`, noVNC on `127.0.0.1:8006`, RDP on `127.0.0.1:3389` |
| Shared folder | host `/opt/winvm/shared` = `\\host.lan\Data` over SSH = `Z:\` on the desktop |
| Web dash server | guest 8888, published as `http://127.0.0.1:8888` |

Every port is published on the host's loopback only: the host has a public address and no firewall.
How the VM was built and how to repair it is in OpenDash's `docs/testing-vm-setup.md`
(`/root/dev/slop/OpenDash/docs/` on the host).

## There is one VM

Two sessions driving one SimHub produce confusing results rather than errors. Claim the VM before a
test that restarts SimHub or changes its state, and release it afterwards:

```bash
scripts/vm.sh who                      # "nobody", or who holds it, since when and why
scripts/vm.sh claim "rigPlay #11: pairing on the VM"
scripts/vm.sh release
```

The lock is `/opt/winvm/shared/vm.lock`. `claim` writes the same JSON as OpenDash's `scripts/vm.ts`
(`{"who","since","note"}`); `who` also reads a free-text line. A claim older than 90 minutes counts as
abandoned. You are `user@host:<checkout directory>` unless `RIGPLAY_VM_WHO` says otherwise, so two
worktrees on the same host are two different holders. `plugin` refuses to run while someone else holds
a fresh claim. Never stop or restart the VM (`vm_stop`, `vm_restart`, `docker compose`) while it is
claimed by someone else.

## The loop: `scripts/vm.sh`

Run on the host that runs the container. Bash only; the plugin build uses `~/.dotnet/dotnet`.

```bash
scripts/vm.sh status                   # container, SSH, VNC, SimHub, installed RigPlay.dll, ports, lock
scripts/vm.sh plugin --dry-run         # print every step below without doing it
scripts/vm.sh plugin                   # build, install RigPlay.dll, restart SimHub
scripts/vm.sh ps                       # SimHub process, installed DLL, last [rigPlay] log lines
scripts/vm.sh logs 200                 # tail the log SimHub is writing now
scripts/vm.sh shot build/vm.png        # screenshot through QEMU's VNC (works in any Windows state)
```

`plugin` does, in order:

1. `dotnet build plugin/RigPlay -c Release` (skip with `--no-build`).
2. Copies `plugin/RigPlay/bin/Release/net48/RigPlay.dll` to `/opt/winvm/shared/RigPlay.dll`.
3. Over guest SSH, in one PowerShell script: closes SimHub (killing it when the window cannot be
   reached from session 0, which is the usual case), copies `\\host.lan\Data\RigPlay.dll` into
   `C:\Program Files (x86)\SimHub\`, runs `Unblock-File` on it, checks its SHA-256 against the build,
   pre-activates the plugin, and starts SimHub with `Start-ScheduledTask SimHub`.

Notes on those steps:

- **Session 0.** A process started over SSH has no desktop. SimHub started that way would run without a
  window, so it is always started through the `SimHub` scheduled task, which runs in the logged-in
  session (`plugin` registers the task if it is missing).
- **The share.** `Z:\` exists only in the desktop session. Over SSH the share is `\\host.lan\Data`.
- **Zone mark.** A DLL copied from a share carries the internet-zone mark, and .NET then refuses to
  load it without saying so. Hence `Unblock-File`.
- **Killing SimHub** skips the plugin's `End()` and its final settings save, so a setting the page has
  not saved yet is lost.
- **Logs.** `logs` and `ps` read `SimHub.txt`, or the lowest-numbered `SimHub.N.txt` when it is missing,
  never the newest by modification time: the guest clock drifts, and a rotated log can look newer
  than the live one. The MCP tool `simhub_logs` sorts by time and can show an old file.

### The "new plugin" prompt and activation

The first time SimHub sees a new plugin DLL it shows a "new plugin found" prompt on the desktop and
does not load the plugin until it is accepted. Activation is stored in
`C:\Program Files (x86)\SimHub\PluginsData\PluginsActivation.json`, an array of
`{"ClassName","IsEnabled","ShowInMainMenu","ShowInMainMenuPosition"}`. rigPlay's entry has
`"ClassName": "RigPlayPlugin.RigPlay"`. `plugin` adds it, enabled, when it is missing; if the prompt
appears anyway, take a screenshot and click it away (`screenshot` and `click`).

### Settings

`C:\Program Files (x86)\SimHub\PluginsData\Common\RigPlay.RigPlaySettings.json`, written at plugin start,
when the page saves a change and at `End()`. SimHub keeps rotating copies in `PluginsData\Common\_Backups\`
(`RigPlay.RigPlaySettings_b1.json`, `_b2`, ...). To test a fresh install, stop SimHub, move both away,
and start it again.

## MCP tools

The `winvm` MCP server gives every Claude Code session on the host the same access as tools.

| Group | Tools | Notes |
|---|---|---|
| Lifecycle | `vm_status` `vm_start` `vm_stop` `vm_restart` `vm_wait_ready` `vm_logs` | `vm_stop` and `vm_restart` reboot Windows for everyone. Check the lock first. |
| Commands | `run_powershell(script, timeout_seconds)` `run_cmd(command)` | Run over SSH in **session 0**. Anything with a window started here is invisible. |
| Desktop | `run_in_desktop(command, arguments, working_dir)` | Starts a program in the logged-in desktop through a scheduled task. Returns immediately. |
| Files | `upload_file` `download_file` `read_file(remote_path, tail_lines)` `write_file` | SCP underneath. Give Windows paths with forward slashes, for example `C:/Temp/x.dll`. |
| GUI | `screenshot(max_width)` `screen_size` `click` `mouse_move` `drag` `type_text` `press_keys` | Clicks use full-resolution coordinates (`screen_size`); `screenshot` is scaled to 1024 px wide by default. |
| SimHub | `simhub_status` `simhub_start` `simhub_stop(force)` `simhub_logs(lines)` `simhub_install_plugin(local_dll_path)` | `simhub_install_plugin` copies a DLL into the SimHub folder, unblocks it and restarts SimHub; it does not pre-activate. |

`press_keys` takes vncdotool names: `enter`, `tab`, `esc`, `alt-f4`, `ctrl-alt-del`, space-separated.

## The web dash server

SimHub serves installed dashboards on guest port 8888, published on the host. From the host:
`http://127.0.0.1:8888/Dash#<Name>` (the name percent-encoded; `/dashboard/<Name>` is a 404 on SimHub
9.12.6), for example with a headless browser. This is what the tablet's
WebView loads ([ADR 0004](decisions/0004-dashboard-via-web-dash-server.md)). The guest firewall already
allows 8888 (rule "SimHub web dash server").

## Exposing the rigPlay ports

The protocol ports ([protocol.md](protocol.md) section 2) are not published by default. To reach the
plugin from the host, add these lines under `ports:` in `/opt/winvm/compose.yml`:

```yaml
      - "127.0.0.1:23711:23711/tcp"   # rigPlay control channel (plugin listens)
      - "127.0.0.1:23712:23712/udp"   # rigPlay audio, tablet -> plugin
      - "127.0.0.1:23710:23710/udp"   # rigPlay discovery port; does not carry the beacon, see below
```

For the Android emulator, which runs in the `android` container on the default Docker bridge and cannot
reach the host's loopback, also publish on the bridge gateway:

```yaml
      - "172.17.0.1:23711:23711/tcp"
      - "172.17.0.1:23712:23712/udp"
      - "172.17.0.1:8888:8888"        # web dash server, for the dashboard WebView
```

Nothing else is needed. dockur forwards every published port into the guest except its own (VNC,
noVNC, SMB), UDP included, and the guest firewall allows all inbound traffic to `SimHubWPF.exe` (rule
"SimHub Setup rule"), so the plugin's listeners need no rule of their own.

**Applying it restarts the VM.** `cd /opt/winvm && docker compose up -d` recreates the container, which
reboots Windows (the disk is kept). Claim the VM, make sure no one else holds it, apply, wait for
`vm_wait_ready`, check with `scripts/vm.sh status` (the `host ports:` line lists them), release.

**Discovery does not cross the container boundary.** The guest sits behind NAT on `172.30.0.0/24` inside
the container. Its beacon goes to that subnet's broadcast address and never reaches the host's network,
and port publishing only forwards traffic coming in. An emulator or tablet testing against the VM
therefore connects by manual address:

| Client | Manual address |
|---|---|
| Tool on the host | `127.0.0.1:23711` |
| Android emulator in the `android` container | `172.17.0.1:23711` (with the bridge lines above; not yet tried) |
| Laptop or tablet elsewhere | an SSH tunnel to the host (`ssh -L 23711:127.0.0.1:23711 ...`), TCP only |

Audio then goes to the address of the control connection (`state.audio.port`, protocol section 6.6),
which works the same way. One thing does not: the plugin builds `state.dashboardUrl` from the local
address of the TCP connection (protocol section 11), and behind the NAT that is the guest's
`172.30.0.2`, which no client outside the container can reach. Test dashboards through the VM
by opening `http://<address you connected to>:8888/Dash#<Name>` directly, or on the rig.

## Telemetry

The VM has no sim. For tests that need live telemetry, OpenDash's iRacing shared-memory emulator makes
SimHub's iRacing reader see a running session: `/root/dev/slop/OpenDash/tools/irsdk-emulator` (its
README has build steps and scenarios). Build it, copy `IrsdkEmulator.exe` and its `scenarios/` folder to
the guest, and start it with `run_in_desktop`. Started over SSH it runs in session 0, where SimHub
cannot see its shared memory, and the dashboards keep showing defaults with nothing in any log.

## Gotchas

- **Slow guest.** Two cores: give `run_powershell` generous timeouts and allow 20 to 40 s after a
  SimHub start before judging the plugin page.
- **Modal dialogs block.** Plugin activation, update notices or crash dialogs stop SimHub until
  clicked. If something looks stuck, take a screenshot first.
- **Guest clock** is not UTC and drifts; do not compare guest and host timestamps.
- **Pinned SimHub.** 9.12.6, the version `plugin/lib` is built against. Do not let it update.
