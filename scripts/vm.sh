#!/usr/bin/env bash
# vm.sh: the plugin development loop against the Windows test VM (docs/testing-vm.md).
#
# The VM is a dockur/windows container on this host, managed from /opt/winvm. Two ways in, both on
# the host's loopback: SSH into Windows on 127.0.0.1:2222 (key /opt/winvm/ssh/id_ed25519, user from
# WIN_USERNAME in /opt/winvm/.env) and QEMU's own VNC on 127.0.0.1:5900. The host folder
# /opt/winvm/shared is \\host.lan\Data from an SSH session and Z:\ on the desktop.
#
# Two traps this script exists to avoid. A command sent over SSH runs in session 0, which has no
# desktop, so SimHub is started through its scheduled task, which runs in the logged-in session.
# And SimHub only loads plugins at startup, so installing a DLL means stopping and starting SimHub.
#
# This script never starts, stops or restarts the VM itself; use the winvm MCP tools for that.
set -euo pipefail

WINVM_DIR=${RIGPLAY_WINVM_DIR:-/opt/winvm}
SSH_KEY=$WINVM_DIR/ssh/id_ed25519
GUEST_SSH_PORT=2222
VNC_PORT=5900
SHARE_DIR=$WINVM_DIR/shared
SHARE_UNC='\\host.lan\Data'
SIMHUB_DIR='C:\Program Files (x86)\SimHub'
LOCK_FILE=$SHARE_DIR/vm.lock
LOCK_STALE_MINUTES=90
PLUGIN_CLASS='RigPlayPlugin.RigPlay'
# Default protocol ports (docs/protocol.md section 2).
CONTROL_PORT=23711
DISCOVERY_PORT=23710
AUDIO_PORT=23712

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DOTNET_DIR=${DOTNET_ROOT:-$HOME/.dotnet}
PLUGIN_PROJECT=$REPO_ROOT/plugin/RigPlay
PLUGIN_DLL=$PLUGIN_PROJECT/bin/Release/net48/RigPlay.dll

SSH_OPTS=(-i "$SSH_KEY" -p "$GUEST_SSH_PORT"
  -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR
  -o BatchMode=yes -o ConnectTimeout=10)

DRY_RUN=0

usage() {
  cat <<EOF
Usage: scripts/vm.sh <command> [args]

  status                     container, guest SSH, VNC, SimHub, RigPlay.dll, ports, lock
  plugin [--dry-run] [--no-build]
                             build plugin/RigPlay (Release), copy RigPlay.dll into SimHub,
                             unblock it and restart SimHub. --dry-run prints every step instead.
  logs [n]                   last n lines (default 80) of the log SimHub is writing now
  ps [n]                     SimHub process, installed RigPlay.dll and the last n (default 40)
                             log lines that mention rigPlay
  shot <file.png> [width]    screenshot of the VM display through QEMU's VNC (default width 1600,
                             0 for full resolution)
  claim "<why>"              take the VM ($LOCK_FILE); refused if someone else holds it
  release                    give it back
  who                        who holds the VM; a claim older than $LOCK_STALE_MINUTES minutes counts as nobody

Environment: RIGPLAY_VM_WHO names you in the lock (default user@host:<checkout directory>).
EOF
}

die() {
  printf 'vm.sh: %s\n' "$*" >&2
  exit 1
}

need_host() {
  [[ -d $WINVM_DIR ]] || die "$WINVM_DIR not found: run this on the host that runs the winvm container"
}

need_ssh_key() {
  [[ -r $SSH_KEY ]] || die "cannot read $SSH_KEY (the guest SSH key; root only)"
}

win_user() {
  local user
  user=$(grep -m1 '^WIN_USERNAME=' "$WINVM_DIR/.env" 2>/dev/null | cut -d= -f2-) || true
  [[ -n $user ]] || die "WIN_USERNAME not found in $WINVM_DIR/.env"
  printf '%s' "$user"
}

# Prints a host command (dry run) or runs it.
run() {
  if (( DRY_RUN )); then
    printf '+'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

# True when a TCP port on the host's loopback answers with the given banner. A bare connect is not
# enough: docker-proxy accepts on every published port even when nothing listens behind it.
banner_is() {
  local port=$1 want=$2 got
  got=$(timeout 5 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port && head -c ${#want} <&3" 2>/dev/null) || true
  [[ $got == "$want" ]]
}

# PowerShell over SSH reports errors (and progress) as CLIXML on stderr; keep only the error text.
clean_clixml() {
  local text
  text=$(cat)
  if [[ $text != '#< CLIXML'* ]]; then
    [[ -n $text ]] && printf '%s\n' "$text"
    return 0
  fi
  { grep -o '<S S="Error">[^<]*</S>' <<<"$text" || true; } |
    sed -e 's/<S S="Error">//' -e 's/<\/S>//' | tr -d '\n' |
    sed -e 's/_x000D__x000A_/\n/g' -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' \
        -e "s/&apos;/'/g" -e 's/&amp;/\&/g'
  printf '\n'
}

# Runs a PowerShell script in the guest over SSH, in session 0. The script is sent base64 encoded
# (UTF-16LE, as -EncodedCommand wants) so that no quoting has to survive bash, ssh and cmd.exe.
# Keep scripts ASCII and free of here-strings. Usage: guest_ps <timeout-seconds> <script>
guest_ps() {
  local seconds=$1 script
  script="\$ProgressPreference = 'SilentlyContinue'"$'\n'"$2"
  if (( DRY_RUN )); then
    printf '+ ssh %s %s@127.0.0.1 powershell.exe -EncodedCommand <base64 of:>\n' "${SSH_OPTS[*]}" '<WIN_USERNAME>'
    printf '%s\n' "$script" | sed 's/^/    /'
    return 0
  fi
  need_ssh_key
  local encoded command user errfile rc=0
  # Indentation and comment lines are dropped on the way: UTF-16 plus base64 nearly triples the size.
  encoded=$(sed -e 's/^[[:space:]]*//' -e '/^#/d' <<<"$script" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)
  command="powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $encoded"
  # The guest's sshd hands the command to cmd.exe, which stops at 8191 characters.
  (( ${#command} < 8000 )) || die "internal: PowerShell script too long for cmd.exe (${#command} characters)"
  user=$(win_user)
  errfile=$(mktemp)
  timeout "$seconds" ssh "${SSH_OPTS[@]}" "$user@127.0.0.1" "$command" 2>"$errfile" | tr -d '\r' || rc=$?
  clean_clixml <"$errfile" >&2
  rm -f "$errfile"
  (( rc != 124 )) || printf 'vm.sh: guest command timed out after %ss\n' "$seconds" >&2
  return "$rc"
}

# PowerShell that sets $log to the log SimHub is writing now. Not the newest by modification time:
# the guest clock drifts, so a rotation can carry a later timestamp than the live file. SimHub.txt
# is the current log and SimHub.N.txt are rotations, N growing with age.
PS_CURRENT_LOG="
\$dir = Join-Path '$SIMHUB_DIR' 'Logs'
\$log = Get-Item (Join-Path \$dir 'SimHub.txt') -ErrorAction SilentlyContinue
if (-not \$log) {
  \$log = Get-ChildItem \$dir -Filter 'SimHub.*.txt' -ErrorAction SilentlyContinue |
    Sort-Object { [int](\$_.Name -replace '^SimHub\.(\d+)\.txt\$', '\$1') } | Select-Object -First 1
}"

# PowerShell that prints SimHub's process, the installed DLL and the ports SimHub listens on.
PS_SUMMARY="
\$p = Get-Process SimHubWPF -ErrorAction SilentlyContinue | Select-Object -First 1
if (\$p) { \"simhub:     running, pid \$(\$p.Id), session \$(\$p.SessionId), started \$(\$p.StartTime) (guest clock)\" }
else { 'simhub:     not running' }
\$d = Get-Item (Join-Path '$SIMHUB_DIR' 'RigPlay.dll') -ErrorAction SilentlyContinue
if (\$d) { \"RigPlay.dll: \$(\$d.Length) bytes, sha256 \$((Get-FileHash \$d.FullName -Algorithm SHA256).Hash.ToLower().Substring(0,12)), written \$(\$d.LastWriteTime) (guest clock)\" }
else { 'RigPlay.dll: not installed' }
if (\$p) {
  \$tcp = (Get-NetTCPConnection -State Listen -OwningProcess \$p.Id -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique | Sort-Object) -join ' '
  \$udp = (Get-NetUDPEndpoint -OwningProcess \$p.Id -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique | Sort-Object) -join ' '
  \"listening:  tcp \$tcp | udp \$udp (rigPlay defaults: tcp $CONTROL_PORT, udp $AUDIO_PORT)\"
}"

# ------------------------------------------------------------------------------------------- lock

# Prints the lock as three lines (who, since, note); prints nothing when there is no lock file.
# Reads the JSON that OpenDash's scripts/vm.ts writes ({"who","since","note"}) and also a free-text
# line; for free text the since is the last ISO-8601 timestamp in it, else the file's mtime.
read_lock() {
  [[ -s $LOCK_FILE ]] || return 0
  local body who since note
  body=$(tr -d '\r' <"$LOCK_FILE")
  if [[ $body == '{'* ]]; then
    who=$(sed -nE 's/.*"who" *: *"(([^"\\]|\\.)*)".*/\1/p' <<<"$body" | head -1)
    since=$(sed -nE 's/.*"since" *: *"([^"]*)".*/\1/p' <<<"$body" | head -1)
    note=$(sed -nE 's/.*"note" *: *"(([^"\\]|\\.)*)".*/\1/p' <<<"$body" | head -1 | sed -e 's/\\"/"/g' -e 's/\\\\/\\/g')
  else
    who=$(head -1 <<<"$body")
    since=$(grep -oE '[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z?' <<<"$body" | tail -1) || true
    note=''
  fi
  [[ -n $since ]] || since=$(date -u -r "$LOCK_FILE" +%Y-%m-%dT%H:%M:%SZ)
  printf '%s\n%s\n%s\n' "${who:-unknown}" "$since" "$note"
}

# Age of an ISO timestamp in whole minutes, or a large number when it cannot be parsed.
age_minutes() {
  local then
  then=$(date -u -d "$1" +%s 2>/dev/null) || { echo 999999; return; }
  echo $(( ($(date -u +%s) - then) / 60 ))
}

whoami_lock() {
  printf '%s' "${RIGPLAY_VM_WHO:-${USER:-$(id -un)}@$(hostname -s):$(basename "$REPO_ROOT")}"
}

json_escape() {
  local s=${1//\\/\\\\}
  s=${s//\"/\\\"}
  s=${s//$'\n'/ }
  printf '%s' "${s//$'\t'/ }"
}

# Sets LOCK_WHO / LOCK_SINCE / LOCK_NOTE / LOCK_AGE and returns 0 when a fresh claim exists.
fresh_lock() {
  local lines
  mapfile -t lines < <(read_lock)
  (( ${#lines[@]} >= 2 )) || return 1
  LOCK_WHO=${lines[0]} LOCK_SINCE=${lines[1]} LOCK_NOTE=${lines[2]:-}
  LOCK_AGE=$(age_minutes "$LOCK_SINCE")
  (( LOCK_AGE <= LOCK_STALE_MINUTES ))
}

describe_lock() {
  printf '%s since %s' "$LOCK_WHO" "$LOCK_SINCE"
  [[ -n $LOCK_NOTE ]] && printf ' (%s)' "$LOCK_NOTE"
  printf ', %s min ago' "$LOCK_AGE"
}

cmd_who() {
  need_host
  if fresh_lock; then
    describe_lock
    printf '\n'
  elif [[ -s $LOCK_FILE ]]; then
    printf 'nobody (stale claim, older than %s min: ' "$LOCK_STALE_MINUTES"
    describe_lock
    printf ')\n'
  else
    echo nobody
  fi
}

cmd_claim() {
  need_host
  local note=${1:-} me
  [[ -n $note ]] || die 'claim needs a reason: scripts/vm.sh claim "what you are testing"'
  me=$(whoami_lock)
  if fresh_lock && [[ $LOCK_WHO != "$me" ]]; then
    die "the VM is claimed by $(describe_lock)"
  fi
  local tmp
  tmp=$(mktemp "$SHARE_DIR/.vm.lock.XXXXXX")
  printf '{"who":"%s","since":"%s","note":"%s"}\n' \
    "$(json_escape "$me")" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(json_escape "$note")" >"$tmp"
  chmod 666 "$tmp"
  mv -f "$tmp" "$LOCK_FILE"
  echo "claimed by $me"
}

cmd_release() {
  need_host
  local me
  me=$(whoami_lock)
  if fresh_lock && [[ $LOCK_WHO != "$me" ]]; then
    die "the VM is claimed by $(describe_lock), not by $me"
  fi
  rm -f "$LOCK_FILE"
  echo released
}

# ------------------------------------------------------------------------------------- commands

cmd_status() {
  need_host
  local state
  state=$(docker inspect -f '{{.State.Status}} since {{.State.StartedAt}}' winvm 2>/dev/null) || state='no container named winvm'
  echo "container:  $state"
  local ssh_up=0
  if banner_is "$GUEST_SSH_PORT" 'SSH-'; then ssh_up=1; echo 'guest ssh:  up'; else echo 'guest ssh:  down'; fi
  if banner_is "$VNC_PORT" 'RFB '; then echo 'vnc:        up'; else echo 'vnc:        down'; fi
  if (( ssh_up )); then
    guest_ps 60 "$PS_SUMMARY" || echo 'guest:      SSH answered but the PowerShell query failed'
  fi
  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8888/ 2>/dev/null) || true
  echo "web dash:   http://127.0.0.1:8888/ -> HTTP ${code:-000}"
  local published
  published=$(docker port winvm 2>/dev/null | grep -E "^($CONTROL_PORT|$DISCOVERY_PORT|$AUDIO_PORT)/" | tr '\n' ' ') || true
  echo "host ports: ${published:-rigPlay ports not published (see docs/testing-vm.md, Exposing the rigPlay ports)}"
  printf 'lock:       '
  cmd_who
}

cmd_logs() {
  need_host
  local lines=${1:-80}
  [[ $lines =~ ^[0-9]+$ && $lines -gt 0 ]] || die "logs: not a line count: $lines"
  guest_ps 120 "$PS_CURRENT_LOG
if (-not \$log) { 'no SimHub log yet'; exit 0 }
\"== \$(\$log.FullName) ==\"
Get-Content -LiteralPath \$log.FullName -Tail $lines"
}

cmd_ps() {
  need_host
  local lines=${1:-40}
  [[ $lines =~ ^[0-9]+$ && $lines -gt 0 ]] || die "ps: not a line count: $lines"
  guest_ps 120 "$PS_SUMMARY
$PS_CURRENT_LOG
if (-not \$log) { 'no SimHub log yet'; exit 0 }
\"== rigPlay lines in \$(\$log.FullName) ==\"
Select-String -LiteralPath \$log.FullName -Pattern 'rigplay' -SimpleMatch | Select-Object -Last $lines | ForEach-Object { \$_.Line }"
}

cmd_shot() {
  need_host
  local out=${1:-} width=${2:-1600}
  [[ -n $out ]] || die 'shot needs an output file: scripts/vm.sh shot vm.png'
  [[ $width =~ ^[0-9]+$ ]] || die "shot: not a width: $width"
  local python=$WINVM_DIR/mcp/.venv/bin/python
  [[ -x $python ]] || die "$python not found (the winvm MCP venv provides vncdotool and Pillow)"
  mkdir -p "$(dirname "$out")"
  VM_SHOT_OUT=$out VM_SHOT_WIDTH=$width VM_SHOT_PORT=$VNC_PORT "$python" - <<'PY'
import logging, os, tempfile
from vncdotool import api
from PIL import Image
logging.getLogger('vncdotool').setLevel(logging.ERROR)
out, width = os.environ['VM_SHOT_OUT'], int(os.environ['VM_SHOT_WIDTH'])
client = api.connect('127.0.0.1::' + os.environ['VM_SHOT_PORT'], password=None, timeout=15)
tmp = tempfile.NamedTemporaryFile(suffix='.png', delete=False).name
try:
    client.captureScreen(tmp)
finally:
    client.disconnect()
im = Image.open(tmp); im.load(); os.unlink(tmp)
full = im.size
if width and im.width > width:
    im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
im.convert('RGB').save(out, format='PNG', optimize=True)
print(f'{out} ({im.width}x{im.height}, display {full[0]}x{full[1]})')
PY
}

cmd_plugin() {
  local build=1 arg
  for arg in "$@"; do
    case $arg in
      --dry-run) DRY_RUN=1 ;;
      --no-build) build=0 ;;
      *) die "plugin: unknown option $arg" ;;
    esac
  done
  need_host

  # One VM, one SimHub: do not restart it under somebody else's test.
  local me
  me=$(whoami_lock)
  if fresh_lock && [[ $LOCK_WHO != "$me" ]]; then
    if (( DRY_RUN )); then
      echo "# warning: the VM is claimed by $(describe_lock); a real run would stop here"
    else
      die "the VM is claimed by $(describe_lock); wait for it or ask them"
    fi
  elif ! fresh_lock; then
    echo "# note: nobody has claimed the VM; consider: scripts/vm.sh claim \"<why>\""
  fi

  if (( build )); then
    [[ -x $DOTNET_DIR/dotnet ]] || die "no dotnet at $DOTNET_DIR/dotnet (set DOTNET_ROOT)"
    run env DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
      "$DOTNET_DIR/dotnet" build "$PLUGIN_PROJECT" -c Release -nologo -v quiet
  fi
  local sha='<sha256 of RigPlay.dll>'
  if [[ -f $PLUGIN_DLL ]] && (( ! DRY_RUN || ! build )); then
    sha=$(sha256sum "$PLUGIN_DLL" | cut -d' ' -f1)
    echo "# plugin: $PLUGIN_DLL ($(stat -c %s "$PLUGIN_DLL") bytes, sha256 ${sha:0:12})"
  elif (( ! DRY_RUN )); then
    die "no plugin at $PLUGIN_DLL (build it, or drop --no-build)"
  fi
  # The guest checks that what it installed is what was built (skipped in a dry run without a build).
  local verify=''
  [[ $sha == '<'* ]] || verify="if (\$hash -ne '$sha') { throw \"copied file differs from the build: \$hash\" }"
  run cp "$PLUGIN_DLL" "$SHARE_DIR/RigPlay.dll"
  run chmod 664 "$SHARE_DIR/RigPlay.dll"

  guest_ps 240 "\$ErrorActionPreference = 'Stop'
\$sh = '$SIMHUB_DIR'
\$src = '$SHARE_UNC\RigPlay.dll'
\$dest = Join-Path \$sh 'RigPlay.dll'
if (-not (Test-Path -LiteralPath \$src)) { throw \"not on the share: \$src\" }
# SimHub loads plugins only at startup. Close the window first so SimHub saves its settings; from
# session 0 the window is usually not reachable, and then the process is killed.
\$p = Get-Process SimHubWPF -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not \$p) { 'SimHub was not running' }
elseif (\$p.CloseMainWindow() -and \$p.WaitForExit(20000)) { 'SimHub closed' }
else { Get-Process SimHubWPF -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; 'SimHub killed' }
# The DLL can stay locked for a moment after the process is gone.
for (\$i = 1; \$i -le 10; \$i++) {
  try { Copy-Item -LiteralPath \$src -Destination \$dest -Force; break }
  catch { if (\$i -eq 10) { throw }; Start-Sleep 1 }
}
# A file that came over a network share carries the internet zone mark; .NET then refuses it silently.
Unblock-File -LiteralPath \$dest
\$hash = (Get-FileHash -LiteralPath \$dest -Algorithm SHA256).Hash.ToLower()
$verify
\"installed \$dest, sha256 \$(\$hash.Substring(0,12))\"
# Pre-activate, so the 'new plugin found' prompt does not wait on the desktop.
\$act = Join-Path \$sh 'PluginsData\PluginsActivation.json'
if (Test-Path -LiteralPath \$act) {
  \$a = Get-Content -LiteralPath \$act -Raw | ConvertFrom-Json
  if (-not (\$a | Where-Object { \$_.ClassName -eq '$PLUGIN_CLASS' })) {
    \$a = @(\$a) + [pscustomobject]@{ ClassName = '$PLUGIN_CLASS'; IsEnabled = \$true; ShowInMainMenu = \$true; ShowInMainMenuPosition = 0 }
    ConvertTo-Json -InputObject \$a -Depth 6 | Set-Content -LiteralPath \$act -Encoding UTF8
    'pre-activated $PLUGIN_CLASS'
  }
}
# Start SimHub in the logged-in desktop through a scheduled task; started from here it would land
# in session 0, without a window.
if (-not (Get-ScheduledTask -TaskName 'SimHub' -ErrorAction SilentlyContinue)) {
  \$action = New-ScheduledTaskAction -Execute (Join-Path \$sh 'SimHubWPF.exe') -WorkingDirectory \$sh
  \$principal = New-ScheduledTaskPrincipal -UserId \$env:USERNAME -LogonType Interactive -RunLevel Highest
  Register-ScheduledTask -TaskName 'SimHub' -Action \$action -Principal \$principal -Force | Out-Null
}
Start-ScheduledTask -TaskName 'SimHub'
\$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt \$deadline) {
  \$p = Get-Process SimHubWPF -ErrorAction SilentlyContinue | Select-Object -First 1
  if (\$p) { \"SimHub started (pid \$(\$p.Id), session \$(\$p.SessionId)); check: scripts/vm.sh ps\"; exit 0 }
  Start-Sleep 1
}
throw 'SimHub did not start within 60 s; take a screenshot (scripts/vm.sh shot vm.png)'"
}

main() {
  local command=${1:-}
  [[ $# -gt 0 ]] && shift
  case $command in
    status) cmd_status "$@" ;;
    plugin) cmd_plugin "$@" ;;
    logs) cmd_logs "$@" ;;
    ps) cmd_ps "$@" ;;
    shot) cmd_shot "$@" ;;
    claim) cmd_claim "$*" ;;
    release) cmd_release ;;
    who) cmd_who ;;
    ''|-h|--help|help) usage ;;
    *) usage >&2; die "unknown command: $command" ;;
  esac
}

main "$@"
