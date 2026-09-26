param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\net10-aot-single-file'),
    [string]$StagingDirectory = (Join-Path $PSScriptRoot 'artifacts\aot-stage')
)

$ErrorActionPreference = 'Stop'
$exeName = 'GOHShaderModdingSupportLauncher.exe'
$project = Join-Path $PSScriptRoot 'GOHShaderModdingSupportLauncher.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$staging = [IO.Path]::GetFullPath($StagingDirectory)
$logDirectory = Join-Path $PSScriptRoot 'artifacts'
$log = Join-Path $logDirectory 'aot-publish.log'
$workspacePrefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'

if ($output.TrimEnd('\') -eq $staging.TrimEnd('\')) {
    throw 'Output and staging directories must differ.'
}
if (-not $staging.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The staging directory must be inside the project workspace.'
}
if (Test-Path -LiteralPath $staging) {
    # The resolved staging path was verified above before this recursive removal.
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $logDirectory, $staging, $output -Force | Out-Null

& dotnet publish $project -c Release -r win-x64 -o $staging -p:PublishAot=true -p:SelfContained=true -p:PublishSingleFile=false -v:minimal *> $log
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $log -Tail 60 | Write-Host
    throw "Native AOT publish failed. See $log"
}
$analysisWarnings = @(Select-String -LiteralPath $log -Pattern 'warning\s+IL\d{4}|warning.*\bAOT\b')
foreach ($warning in $analysisWarnings) {
    $knownDataGridWarning = $warning.Line.Contains('Avalonia.Controls.DataGrid.dll : warning IL2104') -or
        $warning.Line.Contains('Avalonia.Controls.DataGrid.dll : warning IL3053')
    if (-not $knownDataGridWarning) {
        throw "Unhandled AOT or trimming warning: $($warning.Line). See $log"
    }
}
if ($analysisWarnings.Count -gt 2) {
    throw "Unexpected number of DataGrid analysis warnings. See $log"
}

$sourceExe = Join-Path $staging $exeName
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "AOT executable missing: $sourceExe" }
$files = @(Get-ChildItem -LiteralPath $staging -File -Recurse)
$dlls = @($files | Where-Object { $_.DirectoryName -eq $staging -and $_.Extension -ieq '.dll' } | Sort-Object Name)
if ($dlls.Count -eq 0) { throw 'No native DLLs were found in the AOT publish output.' }
$unexpected = @($files | Where-Object {
    $_.FullName -ne $sourceExe -and
    -not ($_.DirectoryName -eq $staging -and $_.Extension -ieq '.dll') -and
    -not ($_.DirectoryName -eq $staging -and $_.Extension -ieq '.pdb')
})
if ($unexpected.Count -ne 0) {
    throw "Unhandled AOT publish files: $($unexpected.FullName -join ', ')"
}

Add-Type -AssemblyName System.IO.Compression
$memory = [IO.MemoryStream]::new()
$archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
$manifestLines = [System.Collections.Generic.List[string]]::new()
$zipTimestamp = [DateTimeOffset]::Parse('1980-01-01T00:00:00Z')
try {
    foreach ($dll in $dlls) {
        $hash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestLines.Add("$hash $($dll.Name)")
        $entry = $archive.CreateEntry($dll.Name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $zipTimestamp
        $source = [IO.File]::OpenRead($dll.FullName)
        $destination = $entry.Open()
        try { $source.CopyTo($destination) }
        finally { $destination.Dispose(); $source.Dispose() }
    }
    $manifestEntry = $archive.CreateEntry('manifest.txt', [IO.Compression.CompressionLevel]::Optimal)
    $manifestEntry.LastWriteTime = $zipTimestamp
    $manifestStream = $manifestEntry.Open()
    $writer = [IO.StreamWriter]::new($manifestStream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($manifestLines -join "`n") + "`n") }
    finally { $writer.Dispose() }
}
finally { $archive.Dispose() }
$archiveBytes = $memory.ToArray()
$memory.Dispose()
$sha = [Security.Cryptography.SHA256]::Create()
try { $archiveHash = $sha.ComputeHash($archiveBytes) }
finally { $sha.Dispose() }

$destinationExe = Join-Path $output $exeName
$otherOutputFiles = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.FullName -ne $destinationExe })
if ($otherOutputFiles.Count -ne 0) {
    throw "Distribution directory contains other files: $($otherOutputFiles.FullName -join ', ')"
}
Copy-Item -LiteralPath $sourceExe -Destination $destinationExe -Force
$stream = [IO.File]::Open($destinationExe, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
$binary = [IO.BinaryWriter]::new($stream)
try {
    $binary.Write($archiveBytes)
    $binary.Write([Text.Encoding]::ASCII.GetBytes('GOHSMSN1'))
    $binary.Write([Int64]$archiveBytes.Length)
    $binary.Write($archiveHash)
}
finally { $binary.Dispose() }

foreach ($arguments in @(@('--validate-native-bundle'), @('--self-test-ui', '--software-rendering'))) {
    $check = Start-Process -FilePath $destinationExe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($check.ExitCode -ne 0) {
        throw "Published EXE failed the $($arguments -join ' ') check with exit code $($check.ExitCode)."
    }
}

Write-Host "Native AOT executable: $destinationExe"
Write-Host "Embedded native libraries: $($dlls.Count)"
Write-Host "Archive SHA-256: $([BitConverter]::ToString($archiveHash).Replace('-', '').ToLowerInvariant())"
Write-Host "Publish log: $log"
if ($analysisWarnings.Count -ne 0) {
    Write-Warning 'Avalonia DataGrid 12.1.2 emits two known package-level analysis summaries. This app uses only fixed, read-only columns with compiled bindings; the detailed warnings were reviewed.'
}
