# Gates of Hell Shader Modding Support Launcher

Language：English [中文](./READMEcn.md) 

## Catalog
- [Gates of Hell Shader Modding Support Launcher](#gates-of-hell-shader-modding-support-launcher)
  - [Catalog](#catalog)
  - [User Guide](#user-guide)
  - [Runtime Details](#runtime-details)
    - [Preprocess](#preprocess)
    - [Postprocess](#postprocess)
    - [Mod presets](#mod-presets)
    - [Notice](#notice)
  - [Development Guide](#development-guide)
  - [Credits](#credits)
  - [License](#license)
  - [Support my Work](#support-my-work)
---

## User Guide

Please check the workshop page of this launcher for user guide.  
https://steamcommunity.com/sharedfiles/filedetails/?id=3410344592

## Runtime Details

* At startup, the launcher loads settings from %LOCALAPPDATA%\GOHSMSLauncher\settings.conf。
* No matter what launch option is selected, program will follow the process below:
0. Move Environment.CurrentDirectory to game directory
1. Preprocess
2. Launch game with startup parameters
3. Hide windows to make programs run in the background
4. Wait for game program to finish
5. When game finished, run postprocess
6. Decide to exit the program or redisplay the window according to settings

* When exit, save caches

### Preprocess

* If no cached game path, run search method
* * If program is in subfolder of steamapps, search game by hardcoded path
* * Else get steam path from Registry and search game path in all game libraries
* * Move Environment.CurrentDirectory to game directory
* [File Replace Method Only] Replace Files in game root directory
* * If no modified shader.pak found,extract pak from program
* * Else using cached pak to replace original shader.pak
* Force set bump quality to parallax in player profile

### Postprocess

* [File Replace Method Only][Need enable in Settings] Restore vanilla shader file, modified file will remain as cache
* [Need enable in Settings] Clear shader cache in C:\Users\YOURUSERNAME\Documents\my games\gates of hell\shader_cache

### Mod presets

The mod manager's preset toolbar can:  
- save the current loaded list as a preset
- overwrite the selected preset with the currently loaded list
- load a saved preset
- rename preset
- delete preset 

Presets are UTF-8 TOML files in `presets` beside the launcher executable.  
Missing mods will be skipped and listed in a warning during preset loading process.  
The preset filename is its display name.  

One preset file including:  
- `version = 1` to mark preset format version 
- an ordered `[[mods]]` array

```toml
version = 1

[[mods]]
id = "mod_123456"
name = "Workshop example"

[[mods]]
id = "local_mod"
name = "Local example"
```

### Notice
* Force change bump quality is a special measure used to support my shader mods
* Move Environment.CurrentDirectory to game directory is always necessary to let steam not truncate our launch commands
* Settings are saved under LocalAppData, with compatibility for existing settings.conf beside the EXE.
* Game Launch process optional comes with the -showmodinfo parameter, which is an enhancement I found that let game shows detailed mod information
* Automatic update does not check for the availability of new version, so please delete the backup EXE file yourself after verified updated version.

## Development Guide

I'm building this program using Visual Studio 2026, with environment below:
* Windows x64 and a .NET 10 SDK with the Visual Studio C++ build tools for Native AOT
* Avalonia Desktop and Fluent Theme 12.1.3, with DataGrid 12.1.2
* Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-Aot.ps1`. Distribute the sole EXE from `bin/net10-aot-single-file/`.
* The EXE includes the .NET runtime and native UI libraries. No .NET runtime installation is required on the target PC.
* On first launch, native DLLs are verified and extracted to `%LOCALAPPDATA%\GOHSMSLauncher\Native\<archive SHA-256>` (or `%TEMP%` if necessary). Missing or changed DLLs are restored on the next launch.
* Use `--software-rendering` for software graphics. `--validate-native-bundle` verifies extraction and loading without opening the UI. `--self-test-ui` checks the six pages, two languages, and mod operations without launching the game.
* Avalonia DataGrid 12.1.2 produces two upstream Native AOT analysis summaries for reflection based features. This launcher uses fixed read-only columns with compiled bindings; the publish script rejects all other trim/AOT warnings.
* `Properties/i18n.Designer.cs` is maintained with the embedded Chinese resource lookup; keep that lookup when adding resource keys.
* From commit a120cb0 onwards, this project has been using Codex to support development.

## Credits
Special Thanks to  
* @𝙆𝙄𝙍𝙄𝙉 𝙎𝙏𝙍𝙊𝙉𝙂 Provides high-res material for launcher icon  
* Players who participated in the launcher test  

for their contribution during the development!  

## License
Licensed under the Apache License, Version 2.0.
See the [LICENSE](LICENSE) file for details.

## Support my Work
If you like my products, please give this repository a STAR, I'd appreciate it =)  
  
If possible, you can directly support my work in the following ways:

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N4N2ZJR4A)  
[![mbd.pub](./img/mbd.png)](https://mbd.pub/o/fedStudio)  