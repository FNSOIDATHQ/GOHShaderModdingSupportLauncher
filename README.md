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
    - [Mod aliases](#mod-aliases)
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

### Mod aliases

Select one Mod in **Mod manager** and choose **Edit alias** to set, change or clear its alias. Both name columns show matching aliases in bold; hover to see the original name. Available Mods sort by their displayed names, while enabled Mods keep their load order.

**Settings → Mod aliases** manages all rules, including names that are not currently installed. Search by original name or alias, select a rule to edit or delete it, or choose **Add rule** to enter a new original name and alias. Matching uses the complete original name and is case sensitive. Mods with the same name share a rule across games and sources. Renamed Mods require an updated rule; aliases are never matched again as names.

Rules are saved in `%LOCALAPPDATA%\GOHSMSLauncher\mod-aliases.json`, alongside launcher settings. Removing a Mod or clearing subscription caches preserves them. Aliases change only the name display; Mod files, IDs, enabled order and preset matching keep their original values.

### Mod subscriptions

The Mod manager's **Source** column shows **ModDB**, **GitHub** or **Mow Portal** for Mods installed through subscriptions. Workshop Mods retain their Workshop source, and manually installed Mods without an installation record show Local. Attribution uses the selected game's exact installation directory and works offline; aliases do not change it. Select one linked Mod and choose **View subscription** to open its saved project and file details. **View in Mod manager** locates the corresponding installation; choose a directory when a file has multiple installations. Platform labels do not change local shader-cache operations or file cleanup permissions.

The centered **Auto-translate** toolbar translates the current overview, article or file notes using **Google** or **TranSmart (Tencent)**. It starts off, follows the UI language by default, and remembers your provider, language and **Original / Translated** choices. Existing preferences keep Google and retain the previous on/off setting. Only the current public text is sent to the selected provider; local folders, subscriptions, logs and verification data stay on your computer. Both providers work without signing in or entering a personal key. Unavailable target languages are disabled; TranSmart currently offers English, Simplified Chinese, Japanese, Korean, Russian, German, French, Spanish and Portuguese. Availability also depends on the source/target pair and the service's current engines.

Text is sent in bounded batches, and successful fragments are reused across pages and partial retries. Switching providers cancels old work and keeps their caches separate. These experimental anonymous services may be unavailable or rate limited. Brief interruptions use bounded retries; longer cooldowns or unsupported pairs keep the original text, with progress and retry in the lower-right status details. The launcher never silently sends failed text to another provider. Code, links, filenames, versions, line breaks, image positions and download choices are preserved; translations never replace author metadata or update evidence. TranSmart is a candidate for mainland China access; direct mainland connectivity has not been verified.

Open **Mod subscriptions** to browse Gates of Hell mods on ModDB without an account. The page shows the author's summary, articles, images and files, and keeps favorites separate from subscriptions. Select the files you want before subscribing; downloads are never selected automatically.

**Mod subscriptions** sits immediately after **Mod manager** in the sidebar. Choose a source at the top, enter a search and press Enter or **Refresh**; unchanged searches refresh the current page. Paging stays at the lower left and status at the lower right. **Check subscriptions** checks all sources for the selected game. Expand the GitHub configuration to edit topics or add a repository.

Use the menu button above the sidebar to switch between names and a compact icon bar; hover or focus an icon to identify its page. The compact language button opens English and Chinese choices. **Hide mod list**, below the list and to the left of the paging controls, gives the reader more room; **Show mod list** restores it from the same position. Both choices are remembered across restarts. Narrow windows temporarily use compact navigation and a list/detail view with **Back to list**; widening the window restores your preferences. Overviews, articles, file notes and enlarged images share a solid dark reading background.

**Refresh** updates the current list and reading content together: the overview, current article and already opened navigation pages, or the complete file list and selected file notes. Reading positions and unchanged download choices are retained. Failed reads keep the previous content; changed or removed files lose their old download selection. Repeated refreshes share pending requests, and changing the search, source, mod or game discards obsolete responses. Directly added GitHub repositories remain open even when topics searches do not include them. Complete or cancel an installation or cleanup confirmation before refreshing.

Click the status at the lower right to expand progress, cached-content notices and failure details within the page. Content and image retries use the same refresh operation. A successful request does not dismiss another request's failure. Images load when visible and share pending transfers. Public images without query parameters are cached across restarts, including previews you have not enlarged. Fresh images are reused for ten minutes without a network request; expired images are revalidated when supported, and a failed refresh can show the cached image with a status notice. The cache is limited to 32 MiB in memory and 256 MiB / 1,024 files on disk. Addresses with query parameters stay in memory.

**Tools → Clear Mod subscription cache** clears all sources' cached content, images, translations and reading history, then reports the bytes freed and any failures in a dialog. Returning to Mod subscriptions reloads the list and selected content at your previous browsing location. Subscriptions, favorites, read state, preferences, downloads, installed Mods, installation tasks and the independent verification session are preserved.

Details contain **Description** and **Files**. Description lists the overview and individual articles down the left, loads content as you navigate, and embeds images with inline enlargement and a return action. Files loads automatically, lists files vertically and shows only the current file's description and download tasks. Download checkboxes are independent of reading selection; switching files preserves your choices and reading positions. The selected count and download action stay below the reader. Installation checks, cleanup previews and confirmations stay inside the page. Successful content is reused during browsing; failed refreshes preserve cached content. Narrow windows switch between the list and details with **Back to list**.

The single **Open original** action follows the current overview, article, file, shared release notes or selected image and always opens your browser. Images use their public detail page when known, otherwise their original image address. Supported ModDB, GitHub and Mow Portal links open inside the launcher; **Back** beside the paging controls restores the previous reader and position. Document anchors scroll within the text. If a supported link fails, the current content stays visible and **Open original** offers the failed target. Other web links open in your browser; clicking a file link never starts a download. GitHub discovery continues to use only your configured topics.

Reading preserves the order of paragraphs, headings, links, lists, quotes, code, simple tables and embedded images. Independent images appear in a horizontal gallery above the body. Browse with the scrollbar or arrow buttons and choose **More images** for the next gallery page; the gallery never scrolls automatically. Embedded images keep their place in the body and are excluded from the gallery. Older cached descriptions remain readable and gain the ordered layout after a successful refresh.

Click a gallery image to select it, then use **Enlarge image** beside the gallery heading. Left/Right and Home/End select images; Enter or Space enlarges the selection. Images keep their proportions in a larger, compact layout. Selection and scroll positions survive pagination, refreshes, translation changes and returning from the enlarged view. Generated image names appear only when enlarged and remain available to accessibility tools. Click embedded images directly to enlarge them; author captions in the text stay in place.

In **Files**, check the files you want and choose **Subscribe and download selected files** to create a subscription. The file list starts at 420 pixels wide when space permits and can be resized between 240 and 480 pixels by dragging its divider or using the arrow keys while the divider has focus. Names use one line with full-title tooltips; dates, sizes and Mow Portal download hosts appear on separate lines when available. In a narrow reading area, **Show file list** and **Read selected file** switch between navigation and the full-width reader without clearing download choices or reading positions. Author descriptions and shared version notes remain in the reader without repeated file headings or metadata.

Subscribed mods offer **Unsubscribe**, while incomplete cleanup offers **Continue cleanup**. Independent **Subscribed** and **Favorited** badges appear in Mod rows and detail headings; both can be present together. The status details at the bottom right show the last successful check, update count, cleanup problems and download-availability diagnostics. Unfinished operations appear in **Downloads and installation** when needed; review their current-game scope before confirming recovery, especially when it continues an earlier deletion. Individual failed downloads retain their own retry action.

Choose **GitHub** to search public repositories within your configured topics. The default is `gates-of-hell`; edit the comma-separated list, choose **Match any topic** or **Match all topics**, then **Save topics and search**. Keywords and sorting keep the same topic scope. An empty topics list does not search. **Add repository URL** opens an explicit public `https://github.com/owner/repository` even if it has no matching topics; repository IDs keep the same project from being added twice.

GitHub Description shows the README and its images. Files shows public release attachments with their own labels and shared version notes. Releases without attachments remain available as notes without a download checkbox. The project type is a browsing hint; a mod topic does not prove that an attachment is installable. Tools and files that need building can also be placed in `mods`; building and extra deployment remain manual. Repositories without releases can offer source snapshots fixed to a commit SHA; the launcher does not run builds or generators. All anonymously downloadable attachments use the same placement checks and explicit installation confirmation as ModDB files.

Choose **Mow Portal** to browse its Gates of Hell category, author descriptions, version notes, images and public download links. Search filters the current category page; use **Next** to continue. Public Yandex files can be downloaded anonymously and use the same placement checks and installation confirmation. Other hosts remain visible with original-page links and are not offered for download. Closed shares, disabled downloads and exhausted quotas keep a clear failure state without account login.

Mow Portal checks report **Page or download links changed; confirmation needed** when a page or link changes. This does not prove a new upload or a revised file. Review the author page and choose downloads yourself; acknowledging a notice does not change the installed version.

Mow Portal keeps the author introduction, gallery and general installation instructions in the overview. Files shows shared version and download-area information alongside the selected public link, without repeating the introduction as an article or as every file's description. Missing individual file descriptions are identified explicitly.

GitHub checks include all public releases, attachments added to existing releases and attachment revisions. Requests use the anonymous API quota, without a token or login. When that quota is exhausted, the page shows when to retry and keeps the previous file list and notices.

**Downloads and installation** is the third tab beside **Description** and **Files**, with tasks from all sources in the selected game. Subscribing selects this tab automatically. Switching to reading preserves the current installation or cleanup review, and background completion does not switch tabs. Downloads are checked automatically and wait for **Confirm installation**. Review the content layout, destination, new installation or explicitly selected managed replacement, and file scope. The **Original author's instructions** section is collapsed by default. A single layout is preselected; multiple layouts require a choice. Existing destinations are checked before confirmation; use a separate folder or explicitly select a managed replacement when needed.

ZIP, RAR and 7z files can be unpacked into `mods`, including tools, scripts, DLLs, source snapshots, patches and packages without `mod.info`. Single files retain their response filename when available; unsupported archive formats are explicitly stored without unpacking. Corrupt archives and changed checksums fail. The launcher never runs author programs or writes outside `mods`. Completed tasks offer the installation folder and Mod manager. Installation records placement; activation remains subject to the manager's existing feature checks.

The first successful file check establishes a baseline. Later checks report new files and changed revisions; read or ignored notices are separate from installed versions. Use **Check subscriptions** for a manual check; while the launcher runs, subscriptions are also checked periodically. A failed check keeps the previous file list and notices available.

Each downloaded file's task card shows its filename and an **Unsubscribe** action beside its installation actions. This removes only that file's verified installations and download cache after review and confirmation; other files, favorites and the Mod's update subscription remain. Unsubscribing from the Mod's detail heading cleans up the entire Mod: cancel related downloads and wait for them to stop, review deletion and preservation grouped by directory, explicitly confirm, then view the result. File paths can be expanded, and a failed cleanup can be reviewed and continued. Favorites and manually added files are retained. Manual changes, files with uncertain ownership and installation steps outside `mods` need separate attention. Close the game and editor before installing, replacing or removing a mod.

ModDB requests use the Windows WinHTTP backend and the Windows user proxy settings. Normal requests do not use account credentials or cookies. If Cloudflare requires verification, requests to that source pause and the page offers **Open verification window**. Complete the website's check in the separate Microsoft Edge or Google Chrome window, without signing in, then select **Verify access and retry**. The launcher tests a real ModDB request before resuming access; opening a browser or finishing a check alone does not guarantee access has been restored.

The verification window uses a separate temporary browser profile. The launcher does not read your personal browser profile or import account cookies; only the isolated session's Cloudflare clearance and matching browser identity are used temporarily for the verified source. Use **Close verification session** to stop using this session. Built-in verification currently supports ModDB webpages; other hosts keep the original-page and explicit-retry options. If neither browser is available or verification still fails, cached content and **Open original page** remain available, and you can explicitly retry access later. Some site verification policies may still prevent access.

Other request failures show the HTTP status or connection error category. Run the EXE with `--check-moddb` to test its own connection and parsing without opening the UI or changing subscriptions or game files. The result and connection details are written to `%LOCALAPPDATA%\GOHSMSLauncher\Logs\diag.log` (or the temporary log directory if LocalAppData is unavailable); exit code 0 means the live list was read successfully. Logs exclude signed download addresses, verification cookies and credentials.

Images follow a limited number of HTTPS redirects to supported media hosts. Failed images show the HTTP, content or decoding error and can be retried; individual images are limited to 20 MiB and checked for excessive pixel dimensions before decoding. Run `--check-moddb-images --software-rendering` to check the Replays Unlocked and Hotmod galleries, download their first thumbnail and original in full, and test decoding with the EXE's own renderer. This diagnostic writes to the same log and returns 0 only when all image checks pass; it does not open a verification browser or modify game files.

### Launcher updates

The launcher checks the official GitHub **Latest** release in the background after startup. Successful checks are cached for six hours in `launcher-update.json` beside the launcher settings. **About → Check for updates** refreshes the check and also works before game/profile paths are configured. Requests are anonymous and use the Windows user proxy settings; GitHub API cooldowns are shared with Mod subscriptions. Connection, rate-limit and metadata failures preserve the previous successful snapshot.

Only a strictly newer numeric version is offered, with an optional `v` prefix and two to four numeric components (for example, `v4.00` or `v4.00.1`). Equal versions and older releases never replace the current launcher. Previews, legacy WPF attachments and unsupported version formats remain available through **Open GitHub release**. Downloading requires one attachment named exactly `GOHShaderModdingSupportLauncher.exe` with a valid GitHub `sha256` digest. Releases must contain the final Windows x64 single-file EXE produced by `Publish-Aot.ps1`, including its appended native library bundle; the script prints the complete EXE's SHA-256 after packaging.

**Download and update** shows progress and supports cancellation. The full file size, SHA-256 and x64 executable header are checked before preparing a replacement. The launcher saves its settings and closes through the normal subscription cleanup; a separate helper then verifies both files again, atomically replaces the EXE, preserves the previous version as `old.exe`, and restarts. An existing `old.exe` blocks the update until you move or remove that backup. Replacement or process-start failures trigger recovery of the previous EXE; errors are recorded beside the launcher in `.update-error.log`. If the new version starts but later has a runtime problem, use the retained `old.exe` to recover manually.

Run `--check-launcher-update` to test the EXE's own anonymous release-metadata request without downloading or applying an attachment. Exit code 0 means the metadata request and parsing succeeded; the current tag, eligibility and any incompatible release format are recorded in the diagnostic log. The Workshop updater and its UI workflow are retained in [Legacy/WorkshopUpdater](Legacy/WorkshopUpdater/README.md), excluded from the application build.

### Notice
* Force change bump quality is a special measure used to support my shader mods
* Move Environment.CurrentDirectory to game directory is always necessary to let steam not truncate our launch commands
* Settings are saved under LocalAppData, with compatibility for existing settings.conf beside the EXE.
* Game Launch process optional comes with the -showmodinfo parameter, which is an enhancement I found that let game shows detailed mod information
* Launcher updates preserve `old.exe`; remove it after confirming the updated version works, before applying another update.

Use `--check-google-translation` or `--check-transmart-translation` to test the EXE's own anonymous WinHTTP translation with public short samples, a multi-paragraph text of over 3,000 characters, English-to-Chinese and Chinese-to-English targets, protected values, line breaks, cache and cancellation. A Russian-to-Chinese sample is reported separately when the service's engine is unavailable. These checks do not open the UI or change game files. Exit code 0 means the required short/long, two-target and cache/cancellation checks passed; results and any unavailable Russian engine are written to the same diagnostic log. These commands use the Windows user proxy settings and do not establish mainland direct-access evidence. Translation preferences are stored separately in `translation-settings.json`; the bounded translation caches stay in memory and are cleared by the subscription cache cleanup tool.

Mod directory inspection runs in the background. Discovered folders appear before their content checks finish; each pending row shows only **Checking**, then updates as its result arrives. Both Mod manager tables show **Source** and a resizable **Features** column. Hover over the information symbol beside **Features** for four concise explanations with the same badge styles as the list. Confirmed ordinary content shows **Regular**; invalid or unconfirmed structures and completed checks with unresolved results show **Other**, alongside any confirmed **DLL** or **Shader** features. Aliases, source information and enabled order are preserved.

Activation requires the **Regular** badge. Attempts to activate other entries show “This mod cannot be used through activation”; if a rejected entry has a **DLL** badge, a second line says “For DLL injection mods, install manually according to the author's instructions”. Mixed **Regular + DLL** Mods can activate their ordinary content; activation does not run scripts or install DLLs. Preset loading rechecks files in the background and rejects invalid entries as a whole, preserving the current enabled list and preset file. Missing preset entries keep their separate warning. Existing invalid or missing enabled references remain visible and can be disabled or reordered, including during a scan. Game/editor launch and locked-list restoration use the same eligibility check before shader processing or enabled-list writes. Exit restoration preserves newer list changes and external options edits made during its scan; canceled scans and results for another game cannot restore an older list. Normal refresh does not change the enabled list. Disabling ordinary content does not uninstall separately installed DLLs. Subscription tasks show available content inspection results only for explicitly associated local installations in the selected game. Uninstalled or unscanned content has no inspection placeholder.

## Development Guide

I'm building this program using Visual Studio 2026, with environment below:
* Windows x64 and a .NET 10 SDK with the Visual Studio C++ build tools for Native AOT
* Avalonia Desktop and Fluent Theme 12.1.3, with DataGrid 12.1.2
* Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-Aot.ps1`. Distribute the sole EXE from `bin/net10-aot-single-file/`.
* The EXE includes the .NET runtime and native UI libraries. No .NET runtime installation is required on the target PC.
* On first launch, native DLLs are verified and extracted to `%LOCALAPPDATA%\GOHSMSLauncher\Native\<archive SHA-256>` (or `%TEMP%` if necessary). Missing or changed DLLs are restored on the next launch.
* Use `--software-rendering` for software graphics. `--validate-native-bundle` verifies extraction and loading without opening the UI. `--self-test-ui` checks seven pages, stable navigation, two languages, subscription placement and cleanup (automatic checks, legacy records, filename preservation, conflicts and task cancellation), mod operations and directory inspection (ordinary/mixed/localization/PAK content, metadata, independent feature states, IDs, cache invalidation and cancellation) without network access or launching the game.
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