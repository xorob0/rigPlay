# SimHub reference assemblies

The plugin is compiled against these DLLs. They are copied unchanged from a **SimHub 9.12.6** install
(`C:\Program Files (x86)\SimHub\` on the Windows test VM); the SHA-256 of each file matches that install.

| File | Version | Used for |
|---|---|---|
| `SimHub.Plugins.dll` | 1.0.0.0 | `IPlugin`, `IWPFSettingsV2`, settings persistence, SimHub's WPF controls (`SHSection`, `SHButtonPrimary`, `SHToggleButton`) |
| `SimHub.Logging.dll` | 1.0.0.0 | `SimHub.Logging.Current`, SimHub's log |
| `GameReaderCommon.dll` | 1.0.0.0 | game data types referenced by `SimHub.Plugins` |
| `InputManagerCS.dll` | 1.0.0.1 | input types referenced by `SimHub.Plugins` |
| `log4net.dll` | 2.0.15.0 | the `ILog` behind `SimHub.Logging.Current` |
| `MahApps.Metro.dll` | 1.5.0.23 | the WPF toolkit SimHub's controls derive from |
| `Newtonsoft.Json.dll` | 13.0.4 | the serialiser SimHub saves plugin settings with |
| `NAudio.dll` | 2.2.1.0 | audio playback for the tablet's CarPlay audio (#24) |
| `NAudio.Core.dll` | 2.2.1.0 | NAudio's core types (wave formats, buffers) |
| `NAudio.Wasapi.dll` | 2.2.1.0 | WASAPI output and device enumeration |
| `NAudio.WinMM.dll` | 2.2.1.0 | `WaveOutEvent`, the WinMM fallback output |

Every reference is `Private=false` in `RigPlay.csproj`: SimHub already loads these assemblies into its own
process, so `RigPlay.dll` is built against them but never ships them. The one exception is not in this folder:
`Concentus.dll` (the Opus decoder, a NuGet package) ships next to `RigPlay.dll` because SimHub does not have it;
its own dependencies `System.Memory` and `System.Numerics.Vectors` are SimHub's copies (SimHub 9.12.6 ships
them with binding redirects), which is why the csproj pins those packages to SimHub's versions and copies none. Bundling a second copy could load a
different version next to SimHub's and break type identity (a `Newtonsoft.Json` object from one copy is not
the same type as from the other). Installing the plugin is therefore copying `RigPlay.dll` alone.

They are committed so that `dotnet build` works on any machine, Linux included, without a SimHub install.
When SimHub is upgraded on the test VM, copy the new files from its install folder and update the versions here.
