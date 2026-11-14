using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

internal static class LauncherUpdate
{
    internal sealed record Candidate(string Source, string Target, string SourceHash, string TargetHash);
    internal sealed record Prepared(Candidate Candidate, string Backup, string Staged);

    private static string Text(string key) => Properties.i18n.ResourceManager.GetString(key, Properties.i18n.Culture) ?? key;

    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    internal static void CheckTarget(string target)
    {
        if (!Path.IsPathFullyQualified(target) || !File.Exists(target) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Text("LU_UnsupportedTarget"));
        target = Path.GetFullPath(target);
        ManagedModFiles.EnsureSafe(target);
        if (Path.Exists(Path.Combine(Path.GetDirectoryName(target)!, "old.exe"))) throw new IOException(Text("Update_BackupExists"));
        // Check directory permissions while the current launcher can still display the failure.
        string probe = target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }

    internal static Prepared Prepare(Candidate candidate)
    {
        CheckTarget(candidate.Target);
        ManagedModFiles.EnsureSafe(candidate.Source);
        if (Path.GetFullPath(candidate.Source).Equals(Path.GetFullPath(candidate.Target), StringComparison.OrdinalIgnoreCase) ||
            !Hash(candidate.Target).Equals(candidate.TargetHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Text("LU_TargetChanged"));
        string staged = candidate.Target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ManagedModFiles.EnsureSpace(staged, new FileInfo(candidate.Source).Length);
            using (var source = File.Open(candidate.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(destination); destination.Flush(flushToDisk: true); }
            if (!Hash(staged).Equals(candidate.SourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Text("LU_ChecksumFailed"));
            return new(candidate, Path.Combine(Path.GetDirectoryName(candidate.Target)!, "old.exe"), staged);
        }
        catch { File.Delete(staged); throw; }
    }

    internal static void Discard(Prepared update)
    {
        if (Path.GetDirectoryName(update.Staged) != Path.GetDirectoryName(update.Candidate.Target) ||
            !update.Staged.StartsWith(update.Candidate.Target + ".update-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid staged update path.");
        File.Delete(update.Staged);
    }

    internal static ProcessStartInfo CreateHelperStartInfo(string script)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        return info;
    }

    internal static string BuildScript(Prepared update, int parentId, bool restart = true)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $target = {{Quote(update.Candidate.Target)}}
            $staged = {{Quote(update.Staged)}}
            $backup = {{Quote(update.Backup)}}
            $expectedSource = {{Quote(update.Candidate.SourceHash)}}
            $expectedTarget = {{Quote(update.Candidate.TargetHash)}}
            $replaced = $false
            $lock = $null
            function Get-UpdateHash([string]$path) {
                $algorithm = [Security.Cryptography.SHA256]::Create()
                $inputFile = [IO.File]::OpenRead($path)
                try { return [BitConverter]::ToString($algorithm.ComputeHash($inputFile)).Replace('-', '') }
                finally { $inputFile.Dispose(); $algorithm.Dispose() }
            }
            function Assert-SafePath([string]$path) {
                for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
                    if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
                        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Update path contains a link or junction.' }
                    }
                }
            }
            try {
                $parent = $null
                try { $parent = [Diagnostics.Process]::GetProcessById({{parentId}}) } catch [ArgumentException] { }
                if ($null -ne $parent) {
                    try { if (-not $parent.WaitForExit(120000)) { throw 'Launcher did not exit in time.' } }
                    finally { $parent.Dispose() }
                }
                if ([IO.Path]::GetDirectoryName($target) -ne [IO.Path]::GetDirectoryName($staged) -or
                    [IO.Path]::GetDirectoryName($target) -ne [IO.Path]::GetDirectoryName($backup)) { throw 'Invalid update directory.' }
                Assert-SafePath $target
                Assert-SafePath $staged
                Assert-SafePath $backup
                Assert-SafePath ($target + '.update.lock')
                $lock = [IO.File]::Open($target + '.update.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                for ($attempt = 0; $attempt -lt 30; $attempt++) {
                    if ([IO.File]::Exists($backup) -or [IO.Directory]::Exists($backup)) { throw {{Quote(Text("Update_BackupExists"))}} }
                    if ((Get-UpdateHash $target) -ne $expectedTarget) { throw {{Quote(Text("LU_TargetChanged"))}} }
                    if ((Get-UpdateHash $staged) -ne $expectedSource) { throw {{Quote(Text("LU_ChecksumFailed"))}} }
                    try {
                        [IO.File]::Replace($staged, $target, $backup, $false)
                        $replaced = $true
                        break
                    } catch {
                        if ($attempt -eq 29) { throw }
                        [Threading.Thread]::Sleep(1000)
                    }
                }
                {{(restart ? """
                $startInfo = [Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $target
                $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
                $startInfo.UseShellExecute = $false
                $restarted = [Diagnostics.Process]::Start($startInfo)
                if ($null -eq $restarted) { throw 'Unable to start updated launcher.' }
                $restarted.Dispose()
                """ : "# Restart disabled for regression tests.")}}
            } catch {
                $failure = $_.Exception.Message
                if ($replaced) {
                    try {
                        if ((Get-UpdateHash $target) -ne $expectedSource) { throw 'The updated target changed before rollback.' }
                        if ([IO.File]::Exists($staged) -or [IO.Directory]::Exists($staged)) { throw 'The staged rollback path is occupied.' }
                        [IO.File]::Replace($backup, $target, $staged, $false)
                        $failure += "`n" + {{Quote(Text("LU_Restored"))}}
                    } catch {
                        $failure += "`n" + {{Quote(Text("LU_RestoreFailed"))}} + "`n$backup`n" + $_.Exception.Message
                    }
                }
                $log = $target + '.update-error.log'
                try { [IO.File]::WriteAllText($log, $failure) } catch { }
                {{(restart ? "$shell = [Activator]::CreateInstance([type]::GetTypeFromProgID('WScript.Shell')); $null = $shell.Popup(" + Quote(Text("LU_Failed")) + " + \"`n$failure`n$log\", 0, " + Quote(Text("Update_Title")) + ", 16)" : "[Console]::Error.WriteLine($failure)")}}
                exit 1
            } finally {
                if ($null -ne $lock) { $lock.Dispose() }
                try { [IO.File]::Delete($staged) } catch { }
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
