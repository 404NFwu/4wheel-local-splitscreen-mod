[CmdletBinding()]
param(
    [string]$GameDirectory = (Join-Path $env:LOCALAPPDATA '4WheelLocalSplitScreenMod\Game'),
    [string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'source\WheelchairLocalCoop.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'payload'
}
if (-not (Test-Path -LiteralPath $GameDirectory -PathType Container)) {
    throw 'GameDirectory does not exist. Supply the game directory containing BepInEx.'
}
$resolvedGameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
foreach ($requiredRelativePath in @('BepInEx\core\BepInEx.Unity.IL2CPP.dll', 'BepInEx\interop\Assembly-CSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedGameDirectory $requiredRelativePath) -PathType Leaf)) {
        throw "Missing $requiredRelativePath. Install BepInEx IL2CPP and generate interop files from your own game first."
    }
}

# No game binaries are copied. References remain in the supplied game directory.
$buildArguments = @('build', $projectFile, '--configuration', 'Release', "-p:GameDirectory=$resolvedGameDirectory")
& $DotnetPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$builtPlugin = Join-Path $projectRoot 'source\bin\Release\net6.0\WheelchairLocalCoop.dll'
if (-not (Test-Path -LiteralPath $builtPlugin -PathType Leaf)) {
    throw 'Build finished without the expected plugin DLL.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resolvedOutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$destination = Join-Path $resolvedOutputDirectory 'WheelchairLocalCoop.dll'
# Deliberately copy only this authored plugin: no .deps.json, .pdb or dependencies.
Copy-Item -LiteralPath $builtPlugin -Destination $destination -Force
$digest = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Built plugin: $destination"
Write-Output "SHA256: $digest"
