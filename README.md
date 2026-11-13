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
    - [Mod subscriptions](#mod-subscriptions)
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

### Mod subscriptions

Open **Mod subscriptions** to browse Gates of Hell mods on ModDB without an account. The page shows the author's summary, articles, images and files, and keeps favorites separate from subscriptions. Select the files you want before subscribing; downloads are never selected automatically.

**Mod subscriptions** sits immediately after **Mod manager** in the sidebar. Choose a source at the top, enter a search and press Enter or **Refresh**; unchanged searches refresh the current page. Paging stays at the lower left and status at the lower right. **Check subscriptions** checks all sources for the selected game. Expand the GitHub configuration to edit topics or add a repository.

**Refresh** updates the current list and reading content together: the overview, current article and already opened navigation pages, or the complete file list and selected file notes. Reading positions and unchanged download choices are retained. Failed reads keep the previous content; changed or removed files lose their old download selection. Repeated refreshes share pending requests, and changing the search, source, mod or game discards obsolete responses. Directly added GitHub repositories remain open even when topics searches do not include them. Complete or cancel an installation or cleanup confirmation before refreshing.

Click the status at the lower right to expand progress, cached-content notices and failure details within the page. Content and image retries use the same refresh operation. A successful request does not dismiss another request's failure. Images load when visible, share pending transfers and reuse a bounded byte cache for up to ten minutes; expired visible images can be read again without discarding a displayed image on failure.

Details contain **Description** and **Files**. Description lists the overview and individual articles down the left, loads content as you navigate, and embeds images with inline enlargement and a return action. Files loads automatically, lists files vertically and shows only the current file's description and download tasks. Download checkboxes are independent of reading selection; switching files preserves your choices and reading positions. The selected count and download action stay below the reader. Installation checks, cleanup previews and confirmations stay inside the page. Successful content is reused during browsing; failed refreshes preserve cached content. Narrow windows switch between the list and details with **Back to list**.

Reading preserves the order of paragraphs, headings, links, lists, quotes, code, simple tables and embedded images. Independent images appear in a horizontal gallery above the body. Browse with the scrollbar or arrow buttons and choose **More images** for the next gallery page; the gallery never scrolls automatically. Embedded images keep their place in the body and are excluded from the gallery. Older cached descriptions remain readable and gain the ordered layout after a successful refresh.

Unsubscribed mods offer **Choose files to subscribe**. Subscribed mods offer **Unsubscribe**, while incomplete cleanup offers **Continue cleanup**. Unfinished operations appear in Files only when needed; review their current-game scope before confirming recovery, especially when it continues an earlier deletion. Individual failed downloads retain their own retry action.

Choose **GitHub** to search public repositories within your configured topics. The default is `gates-of-hell`; edit the comma-separated list, choose **Match any topic** or **Match all topics**, then **Save topics and search**. Keywords and sorting keep the same topic scope. An empty topics list does not search. **Add repository URL** opens an explicit public `https://github.com/owner/repository` even if it has no matching topics; repository IDs keep the same project from being added twice.

GitHub Description shows the README and its images. Files shows public release attachments with their own labels and shared version notes. Releases without attachments remain available as notes without a download checkbox. The project type is a browsing hint; a mod topic does not prove that an attachment is installable. Tools and files that need building are kept for download and manual use. Repositories without releases can offer source snapshots fixed to a commit SHA; the launcher does not run builds or generators. Complete standard mod packages use the same archive checks and explicit installation confirmation as ModDB packages.

Choose **Mow Portal** to browse its Gates of Hell category, author descriptions, version notes, images and public download links. Search filters the current category page; use **Next** to continue. Public Yandex files can be downloaded anonymously and use the same standard mod installation checks. Other hosts remain visible with original-page links and are not offered for download. Closed shares, disabled downloads and exhausted quotas keep a clear failure state without account login.

Mow Portal checks report **Page or download links changed; confirmation needed** when a page or link changes. This does not prove a new upload or a revised file. Review the author page and choose downloads yourself; acknowledging a notice does not change the installed version.

Mow Portal keeps the author introduction, gallery and general installation instructions in the overview. Files shows shared version and download-area information alongside the selected public link, without repeating the introduction as an article or as every file's description. Missing individual file descriptions are identified explicitly.

GitHub checks include all public releases, attachments added to existing releases and attachment revisions. Requests use the anonymous API quota, without a token or login. When that quota is exhausted, the page shows when to retry and keeps the previous file list and notices.

Downloaded archives can be inspected and installed into the selected game's `mods` directory. Confirm the original mod folder and that the archive is a complete package before installation. Patches, installers and unsupported layouts can be downloaded for manual installation; the launcher does not run their scripts or change game files outside `mods`.

The first successful file check establishes a baseline. Later checks report new files and changed revisions; read or ignored notices are separate from installed versions. Use **Check subscriptions** for a manual check; while the launcher runs, subscriptions are also checked periodically. A failed check keeps the previous file list and notices available.

Unsubscribing previews which managed files will be removed and which files will remain. Favorites are retained. Manual changes, files with uncertain ownership and installation steps outside `mods` need separate attention. Close the game and editor before installing, replacing or removing a mod.

ModDB requests use the Windows WinHTTP backend and the Windows user proxy settings. Normal requests do not use account credentials or cookies. If Cloudflare requires verification, requests to that source pause and the page offers **Open verification window**. Complete the website's check in the separate Microsoft Edge or Google Chrome window, without signing in, then select **Verify access and retry**. The launcher tests a real ModDB request before resuming access; opening a browser or finishing a check alone does not guarantee access has been restored.

The verification window uses a separate temporary browser profile. The launcher does not read your personal browser profile or import account cookies; only the isolated session's Cloudflare clearance and matching browser identity are used temporarily for the verified source. Use **Close verification session** to stop using this session. Built-in verification currently supports ModDB webpages; other hosts keep the original-page and explicit-retry options. If neither browser is available or verification still fails, cached content and **Open original page** remain available, and you can explicitly retry access later. Some site verification policies may still prevent access.

Other request failures show the HTTP status or connection error category. Run the EXE with `--check-moddb` to test its own connection and parsing without opening the UI or changing subscriptions or game files. The result and connection details are written to `%LOCALAPPDATA%\GOHSMSLauncher\Logs\diag.log` (or the temporary log directory if LocalAppData is unavailable); exit code 0 means the live list was read successfully. Logs exclude signed download addresses, verification cookies and credentials.

Images follow a limited number of HTTPS redirects to supported media hosts. Failed images show the HTTP, content or decoding error and can be retried; individual images are limited to 20 MiB and checked for excessive pixel dimensions before decoding. Run `--check-moddb-images --software-rendering` to check the Replays Unlocked and Hotmod galleries, download their first thumbnail and original in full, and test decoding with the EXE's own renderer. This diagnostic writes to the same log and returns 0 only when all image checks pass; it does not open a verification browser or modify game files.

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
* Use `--software-rendering` for software graphics. `--validate-native-bundle` verifies extraction and loading without opening the UI. `--self-test-ui` checks seven pages, stable navigation, two languages, and mod operations without network access or launching the game.
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