[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

function Write-Package([string]$Name, [System.Collections.IDictionary]$Files) {
    $path = Join-Path $OutputDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path. Choose a new output directory." }
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Files.GetEnumerator()) {
            $source = Join-Path $root $entry.Value
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $($entry.Value)" }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, ('4Wheel-Local-SplitScreen/' + $entry.Key), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    Write-Output $path
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256)
}

# Explicit allowlists: never recursively package the working directory or game.
$portable = [ordered]@{
    'Start-LocalCoop.cmd' = 'launcher\Start-LocalCoop.cmd'
    'Start-LocalCoop.ps1' = 'launcher\Start-LocalCoop.ps1'
    '启动本地分屏.cmd' = 'launcher\Start-LocalCoop.cmd'
    '先看这个.txt' = '先看这个.txt'
    'README.md' = 'README.md'
    'LICENSE' = 'LICENSE'
    'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
    'docs/DEVELOPMENT.md' = 'docs\DEVELOPMENT.md'
    'payload/WheelchairLocalCoop.dll' = 'payload\WheelchairLocalCoop.dll'
}
$source = [ordered]@{}
foreach ($name in @('.gitignore','README.md','LICENSE','THIRD-PARTY-NOTICES.md','RELEASE-NOTES.md','先看这个.txt','docs\DEVELOPMENT.md','source\WheelchairLocalCoop.csproj','scripts\Build-Mod.ps1','scripts\Package-Mod.ps1','launcher\Start-LocalCoop.cmd','launcher\Start-LocalCoop.ps1')) {
    $source.Add($name.Replace('\','/'), $name)
}
foreach ($name in @('Plugin','LocalPlayers','InputRouting','SharedCamera','WeaponOwnership','WeaponAiming','WeaponHud','LobbyNavigation','WheelchairMovement','TestDriver','WeaponTests','AimTests','C4Tests','MovementTests','MovementSoak')) {
    $source.Add("source/$name.cs", "source\$name.cs")
}
$hashes = @()
foreach ($result in (Write-Package '4Wheel-Local-SplitScreen-v0.3.1.zip' $portable)) {
    if ($result -is [string]) { Write-Host $result } else { $hashes += $result }
}
foreach ($result in (Write-Package '4Wheel-Local-SplitScreen-v0.3.1-source.zip' $source)) {
    if ($result -is [string]) { Write-Host $result } else { $hashes += $result }
}
$lines = foreach ($result in $hashes) { '{0}  {1}' -f $result.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($result.Path) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
