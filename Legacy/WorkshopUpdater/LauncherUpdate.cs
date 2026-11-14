using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GOHShaderModdingSupportLauncher;

internal static class LauncherUpdate
{
    internal sealed record Candidate(string Source, string Target, string SourceHash, string TargetHash);
    internal sealed record Prepared(Candidate Candidate, string Backup);

    private static string Text(string key) => Properties.i18n.ResourceManager.GetString(key, Properties.i18n.Culture) ?? key;

    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    internal static Candidate? Find(string? workshopContent, string target)
    {
        // A framework-dependent launch via `dotnet app.dll` must never replace the shared host.
        if (Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(workshopContent) || !Path.IsPathFullyQualified(workshopContent)) return null;
        var content = new DirectoryInfo(workshopContent);
        if (!content.Exists || content.Name != "400750" ||
            !string.Equals(content.Parent?.Name, "content", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(content.Parent?.Parent?.Name, "workshop", StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Path.Combine(content.FullName, "3410344592");
        if (!Directory.Exists(folder)) return null;
        var source = Path.Combine(folder, "GOHSMSLauncher.exe");
        if (!File.Exists(source))
        {
            // Never select unrelated executables or arbitrarily choose between multiple launchers.
            var matches = Directory.EnumerateFiles(folder, "GOHSMSLauncher.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(2).ToArray();
            if (matches.Length != 1) return null;
            source = matches[0];
        }
        target = Path.GetFullPath(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return null;
        var sourceHash = Hash(source);
        var targetHash = Hash(target);
        if (sourceHash == targetHash) return null;
        return new Candidate(source, target, sourceHash, targetHash);
    }

    internal static Prepared Prepare(Candidate candidate)
    {
        var backup = Path.Combine(Path.GetDirectoryName(candidate.Target)!, "old.exe");
        if (Path.Exists(backup)) throw new IOException(Text("Update_BackupExists"));
        return new Prepared(candidate, backup);
    }

    internal static ProcessStartInfo CreateHelperStartInfo(string script)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        return info;
    }

    internal static string BuildScript(Prepared update, int parentId, bool restart = true)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $target = {{Quote(update.Candidate.Target)}}
            $source = {{Quote(update.Candidate.Source)}}
            $backup = {{Quote(update.Backup)}}
            $renamed = $false
            try {
                $parent = Get-Process -Id {{parentId}} -ErrorAction SilentlyContinue
                if ($null -ne $parent -and -not $parent.WaitForExit(120000)) { throw 'Launcher did not exit in time.' }
                for ($attempt = 0; $attempt -lt 30; $attempt++) {
                    if (Test-Path -LiteralPath $backup) { throw {{Quote(Text("Update_BackupExists"))}} }
                    try {
                        [IO.File]::Move($target, $backup)
                        $renamed = $true
                        break
                    } catch {
                        if ($attempt -eq 29) { throw }
                        Start-Sleep -Milliseconds 1000
                    }
                }
                [IO.File]::Copy($source, $target, $false)
                {{(restart ? "Start-Process -FilePath ('\"' + $target + '\"') -WorkingDirectory ([IO.Path]::GetDirectoryName($target))" : "# Restart disabled for regression tests.")}}
            } catch {
                $failure = $_.Exception.Message
                if ($renamed) {
                    $failure += "`n" + {{Quote(Text("Update_UseBackup"))}} + "`n$backup"
                }
                $log = $target + '.update-error.log'
                try { [IO.File]::WriteAllText($log, $failure) } catch { }
                {{(restart ? "$shell = New-Object -ComObject WScript.Shell; $null = $shell.Popup(" + Quote(Text("Update_Failed")) + " + \"`n$failure`n$log\", 0, " + Quote(Text("Update_Title")) + ", 16)" : "[Console]::Error.WriteLine($failure)")}}
                exit 1
            }
            exit 0
            """;
    }

    internal static void Start(Prepared update)
    {
        using var helper = Process.Start(CreateHelperStartInfo(BuildScript(update, Environment.ProcessId)))
            ?? throw new IOException("Unable to start the update helper.");
    }
}
