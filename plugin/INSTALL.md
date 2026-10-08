# Install the rigPlay SimHub plugin

The rigPlay plugin runs inside SimHub on the PC. It lets rigPlay tablets find and pair with the PC, tells
them which SimHub dashboard to show, and plays their CarPlay audio on the PC.

You need Windows with **SimHub 9.12.6 or later**. The release zip `rigPlay-plugin.zip` holds `RigPlay.dll`,
`Concentus.dll` (the Opus audio decoder, used only when you turn Opus on) and this file. SimHub already
ships everything else the plugin needs.

## Install

1. **Close SimHub**, including its tray icon (right-click the icon → Exit).
2. **Copy `RigPlay.dll` and `Concentus.dll`** into the folder that holds `SimHubWPF.exe`. It is usually
   `C:\Program Files (x86)\SimHub\`. Windows asks for administrator rights to write there. Without
   `Concentus.dll` the plugin still works; only the **Opus compression** option is unavailable.
3. **Unblock the files.** Windows marks files from the internet as blocked, and SimHub then does not load
   them and shows no error. Open PowerShell and run:

   ```powershell
   Unblock-File "C:\Program Files (x86)\SimHub\RigPlay.dll", "C:\Program Files (x86)\SimHub\Concentus.dll"
   ```

   You can also right-click each file → **Properties** → tick **Unblock** → **OK**.
4. **Start SimHub.** It shows a "new plugin found" prompt for rigPlay. Accept it and enable the plugin.
5. **rigPlay** now appears in SimHub's left menu. Click it to open the rigPlay page.

You do not need to change Windows Firewall settings. SimHub's installer already allows SimHub through
Windows Firewall, and the plugin runs inside SimHub, so Windows does not ask. If a third-party firewall or
security suite asks about SimHub, allow it on **private** networks only.

## Turn on SimHub's web dash server

The tablet shows SimHub dashboards through SimHub's own web dash server.

1. In SimHub, open **Settings → Web dash server**.
2. Turn it on and keep the port at **8888**.

On the rigPlay page, **Dashboards → Web dash server** turns green ("Reachable on port 8888") within about
10 seconds. If you use a different port in SimHub and rigPlay does not pick it up, type it into **Web dash
server port** on the rigPlay page.

## The rigPlay page

Click **rigPlay** in SimHub's left menu. The page has these sections:

- **Status**: whether the tablet server listens (TCP port 23711) and discovery runs (UDP 23710), the PC
  name tablets see, and the connected tablets.
- **Pairing**: PIN requests from tablets and the list of paired tablets.
- **Dashboards**: the dashboard tablets show, and whether SimHub's web dash server answers.
- **Audio**: the output device, volume, mute, the audio port (UDP 23712), the **Opus compression** switch
  (off by default; turn it on for a tablet on weak or shared Wi-Fi) and live reception figures.

## Pair a tablet

1. Put the tablet on the same home network as the PC and open rigPlay on it.
2. On the tablet, pick this PC from the list (it is listed under the name shown in **Status → PC name on
   tablets**). If it is not listed, enter the PC's IP address and port 23711 by hand.
3. The rigPlay page shows "Tablet *name* wants to connect" with a 6-digit **PIN**. Type that PIN on the
   tablet. It is valid for 120 seconds and allows 3 attempts. **Deny** refuses the tablet.

The tablet then appears under **Paired tablets** and reconnects by itself whenever both are running.
**Forget** next to a tablet removes it; it has to pair again with a new PIN.

## Choose the dashboard

Under **Dashboards**:

- **Dashboard shown by the SimHub button**: the dashboard the tablet opens from its SimHub button or from
  the SimHub icon in CarPlay.
- **While no iPhone is connected**: optionally, a dashboard the tablet shows while no phone is connected.
  Leave it on "(The tablet's home screen)" to show rigPlay's home screen instead.

The list holds the dashboards installed in SimHub. **Refresh list** picks up dashboards installed while
SimHub runs. Changes reach connected tablets at once.

## Choose the audio output

Under **Audio**, pick the **Output device** that plays the tablet's CarPlay audio (music, Siri, calls).
"Windows default" follows the default device in Windows. Set the volume and mute here too; they apply at
once. Siri and calls lower the music while they play.

The **Datagrams** line counts what arrives. Audio is accepted only from paired tablets; anything else is
counted as rejected.

## Troubleshooting

### rigPlay is not in the left menu

- The DLL is still blocked. Close SimHub, run `Unblock-File` on it (see Install, step 3) and start SimHub.
- The plugin was not accepted or was disabled. Open **Add/remove features** (bottom of SimHub's left
  menu), enable rigPlay and tick it to show in the left menu.
  SimHub keeps this choice in `C:\Program Files (x86)\SimHub\PluginsData\PluginsActivation.json`; with
  SimHub closed you can delete rigPlay's entry (`"ClassName": "RigPlayPlugin.RigPlay"`) so that SimHub
  asks again on the next start.
- The DLL is in the wrong folder. It must sit next to `SimHubWPF.exe`, not in a subfolder.
- SimHub's log (`C:\Program Files (x86)\SimHub\Logs\SimHub.txt`) has a line starting with `[rigPlay]`
  for every step of the plugin's start; an error there names the problem.

### Two PCs with SimHub on the same network

Each PC running the plugin shows up on the tablet. Give them distinct names in **Status → PC name on
tablets** so you pick the right one. A tablet pairs with one PC; it reconnects to that PC only.

### A port is already in use

**Status → Tablet server** or **Audio → Last error** then says the port could not be bound, and the
SimHub log says the same. rigPlay uses:

| Port | Use | Where to change it |
|---|---|---|
| UDP 23710 | Discovery broadcasts | Fixed. If another program holds it, tablets do not find the PC: enter its address by hand. |
| TCP 23711 | Tablet connections | **Status → Control port (TCP)**. Enter the new port on the tablet when connecting by hand. |
| UDP 23712 | Audio from the tablets | **Audio → Audio port (UDP)**. Tablets learn the new port by themselves. |

Pick a port between 1024 and 49151 that no other program uses.

### No sound on the PC

- Check **Audio → Output device**. "Saved device, not connected" means the chosen device is unplugged and
  Windows' default plays instead. **Refresh** reloads the device list.
- Check **Mute** and **Volume** on the rigPlay page, and SimHub's own volume in the Windows volume mixer.
- **Audio → Music and media** shows the stream while audio arrives. "stopped" with no datagrams means the
  tablet plays locally: check **Settings → Audio output** on the tablet, and that the tablet is paired.
- **Datagrams** shows rejected packets when audio comes from an address that is not a paired tablet.

### The tablet cannot show the dashboard

- The web dash server is off: turn it on (see above). The tablet then says "SimHub's web dash server is
  off" instead of the dashboard.
- **Dashboards → Web dash server** is green but the tablet still cannot load the page: the tablet loads
  the dashboard from the address it connected to. If the PC has several network adapters (VPN, virtual
  machines), connect the tablet to the PC's address on the home network, by hand if needed.
- No dashboard is selected: choose one under **Dashboards**.
