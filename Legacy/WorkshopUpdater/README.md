# Archived Workshop updater

This directory preserves the Workshop-based launcher updater from commit `55fa675`.
`LauncherUpdate.cs` is an unchanged copy of the former implementation, including
Workshop item discovery, hash comparison, the PowerShell helper and its original
manual recovery behavior. `MainWindow.WorkshopUpdate.cs` preserves its UI workflow
and path-based check state. The original `Update_*` localization resources remain
in the application resource files.

These C# files are explicitly excluded from the application build. They are a
reference for maintenance and restoration, and are not a fallback update source.
The active updater uses the official GitHub Release instead.

The former UI checked on startup and after selecting a game directory, and exposed
the same check through About. Discovery read
`workshop/content/400750/3410344592/GOHSMSLauncher.exe`, including a unique nested
match. Restoring this route requires deliberately reconnecting those entry points
and reviewing the archived helper's failure behavior.
