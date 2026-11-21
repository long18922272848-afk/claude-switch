param([switch]$SkipBuild, [string]$Version = '1.1.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?$') { throw 'Version must be a semantic release version.' }
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$dist = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
if ([IO.Path]::GetDirectoryName($dist) -ne $projectRoot) { throw 'Artifact directory must be the project dist directory.' }
if (Test-Path -LiteralPath $dist) {
    $item = Get-Item -LiteralPath $dist -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'dist must be a normal local directory, not a link.' }
} else { New-Item -ItemType Directory -Path $dist | Out-Null }

if (-not $SkipBuild) { & (Join-Path $projectRoot 'build.ps1') -Test }
$exe = Join-Path $projectRoot 'claude-switch.exe'
$files = @('claude-switch.exe', 'README.md', 'PRIVACY.md', 'LICENSE', 'docs/office-example.png')
foreach ($name in $files) {
    $path = Join-Path $projectRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release input is missing: $name" }
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Release input cannot be a link: $name" }
}
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($metadata.ProductVersion -ne $Version -or $metadata.ProductName -ne 'claude-switch') { throw 'Executable product/version does not match the requested release. Update AssemblyInfo.cs and rebuild.' }

# Verify the executable is AMD64 PE32+ before labelling it windows-x64.
$binary = [IO.File]::OpenRead($exe)
$reader = New-Object IO.BinaryReader($binary)
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Release executable is not a PE file.' }
    $binary.Position = 0x3C
    $offset = $reader.ReadInt32()
    if ($offset -lt 0 -or $offset + 26 -gt $binary.Length) { throw 'Release executable has an invalid PE header.' }
    $binary.Position = $offset
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Release executable is not Windows AMD64.' }
    $binary.Position = $offset + 24
    if ($reader.ReadUInt16() -ne 0x20B) { throw 'Release executable is not PE32+.' }
} finally { $reader.Dispose() }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archiveName = "claude-switch-v$Version-windows-x64.zip"
$archivePath = Join-Path $dist $archiveName
$temp = Join-Path $dist ('.package-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$checksumPath = Join-Path $dist 'SHA256SUMS.txt'
$checksumTemp = Join-Path $dist ('.checksums-' + [Guid]::NewGuid().ToString('N') + '.tmp')
foreach ($target in @($archivePath, $checksumPath)) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target)) -ne $dist) { throw 'Release output must remain inside dist.' }
    if (Test-Path -LiteralPath $target) {
        $existing = Get-Item -LiteralPath $target -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release output cannot overwrite a directory or link.' }
    }
}
$fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$startHere = @'
claude-switch - Windows x64 portable release

1. Extract this ZIP to a folder where you can keep the application.
2. Use Windows 10 or Windows 11, x64, with .NET Framework 4.8 or newer.
3. Install Claude Desktop and sign in with your own account.
4. Double-click claude-switch.exe. Read README.md for setup and features.

This release contains no preconfigured accounts, credentials, usage history,
conversation transcripts, or machine-specific settings. Local runtime data is
created on the computer that runs the application; see PRIVACY.md.

This is an unsigned community application, not an official Anthropic product.
Closing the panel minimizes it to the notification area. Use the tray menu to
exit it before replacing the executable with a newer release.

Source, license, and limitations are described in README.md and LICENSE.
'@

try {
    $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        # Deliberate allowlist: never archive the worktree, profiles, fixtures or logs.
        foreach ($name in $files) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTime
            $entry.ExternalAttributes = 0
            $inputFile = [IO.File]::OpenRead((Join-Path $projectRoot $name))
            $outputFile = $entry.Open()
            try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose(); $inputFile.Dispose() }
        }
        $entry = $zip.CreateEntry('START-HERE.txt', [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTime
        $entry.ExternalAttributes = 0
        $writer = New-Object IO.StreamWriter($entry.Open(), ([Text.UTF8Encoding]::new($false)))
        try { $writer.Write($startHere.Replace("`r`n", "`n")) } finally { $writer.Dispose() }
    } finally { $zip.Dispose(); $stream.Dispose() }

    # Validate the archive before replacing any previous release file.
    $verify = [IO.Compression.ZipFile]::OpenRead($temp)
    try {
        $expected = @($files) + 'START-HERE.txt'
        if ($verify.Entries.Count -ne $expected.Count) { throw 'Unexpected ZIP member count.' }
        foreach ($entry in $verify.Entries) {
            if ($entry.FullName -notin $expected -or $entry.Length -le 0) { throw 'Unexpected or empty ZIP member.' }
        }
    } finally { $verify.Dispose() }

    Move-Item -LiteralPath $temp -Destination $archivePath -Force
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumTemp, "$hash  $archiveName`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $checksumTemp -Destination $checksumPath -Force
    Write-Output "Packaged dist/$archiveName"
    Write-Output "SHA256 $hash"
} finally {
    # Only the two newly-created files in dist may be removed; never recurse.
    foreach ($candidate in @($temp, $checksumTemp)) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($candidate)) -ne $dist) { throw 'Refusing cleanup outside dist.' }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { Remove-Item -LiteralPath $candidate -Force }
    }
}
