#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GamePath,
    [ValidateSet(2, 3, 4)][int]$Players = 4,
    [switch]$Repair,
    [switch]$DryRun,
    [switch]$Json,
    [switch]$PrepareOnly,
    [string]$CacheRoot = (Join-Path $env:LOCALAPPDATA '4WheelLocalSplitScreenMod'),
    [string]$PayloadPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ProductId = '4wheel-local-splitscreen-mod'
$LauncherVersion = '0.3.1'
$GameExeName = '4Wheel Challenge.exe'
$GameDataName = '4Wheel Challenge_Data'
$AppId = '3504700'
$LoaderName = 'BepInEx-IL2CPP-791.zip'
$LoaderUrl = 'https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip'
$LoaderSha256 = 'BB1C510909B69522733791F3A0817A487379A52DA6468A492F1C9C19B3EC6F9A'
$PlayersSpecified = $PSBoundParameters.ContainsKey('Players')
$Mutex = $null
$MutexHeld = $false

function Full-Path([string]$Path) {
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\', '/')
}

function Is-Inside([string]$Child, [string]$Parent) {
    $candidate = Full-Path $Child
    $boundary = (Full-Path $Parent) + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CachePath([string]$Path, [string]$Root) {
    if (-not (Is-Inside $Path $Root)) { throw "目标路径越过独立副本目录：$Path" }
}

function Assert-NoReparseParents([string]$Path) {
    $current = Full-Path $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "独立副本路径不能经过符号链接或目录联接：$current"
            }
        }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Read-VdfValue([string]$Text, [string]$Key) {
    $match = [regex]::Match($Text, '(?m)"' + [regex]::Escape($Key) + '"\s+"([^"]*)"')
    if ($match.Success) { return $match.Groups[1].Value.Replace('\\', '\') }
    return $null
}

function Is-GameRoot([string]$Path) {
    if (-not $Path) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path $GameExeName) -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path 'GameAssembly.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path 'UnityPlayer.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path $GameDataName) -PathType Container)
}

function Find-GameRoot {
    if ($GamePath) {
        $explicit = Full-Path $GamePath
        if (Test-Path -LiteralPath $explicit -PathType Leaf) { $explicit = Split-Path -Parent $explicit }
        if (Is-GameRoot $explicit) { return $explicit }
        throw "指定目录中找不到完整游戏：$explicit"
    }
    # The portable folder may be on the desktop or inside the original game.
    $nearby = Full-Path $PSScriptRoot
    for ($i = 0; $i -lt 4 -and $nearby; $i++) {
        if (Is-GameRoot $nearby) { return $nearby }
        $nearby = Split-Path -Parent $nearby
    }
    $steamRoots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($registry in @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )) {
        try {
            $value = Get-ItemPropertyValue -LiteralPath $registry.Path -Name $registry.Name -ErrorAction Stop
            if ($value) { [void]$steamRoots.Add((Full-Path $value)) }
        } catch { }
    }
    if (${env:ProgramFiles(x86)}) { [void]$steamRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }
    $libraries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($steam in $steamRoots) {
        [void]$libraries.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        $text = [IO.File]::ReadAllText($vdf)
        foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"|"\d+"\s+"([A-Za-z]:[^"]+)"')) {
            $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            [void]$libraries.Add((Full-Path ($value.Replace('\\', '\'))))
        }
    }
    foreach ($library in $libraries) {
        $manifest = Join-Path $library ('steamapps\appmanifest_' + $AppId + '.acf')
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        $text = [IO.File]::ReadAllText($manifest)
        if ((Read-VdfValue $text 'appid') -ne $AppId) { continue }
        $installDir = Read-VdfValue $text 'installdir'
        if (-not $installDir) { continue }
        $common = Join-Path $library 'steamapps\common'
        $candidate = Full-Path (Join-Path $common $installDir)
        if ((Is-Inside $candidate $common) -and (Is-GameRoot $candidate)) { return $candidate }
    }
    if (-not $DryRun -and -not $PrepareOnly) {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = '请选择 Steam 原版游戏的 4Wheel Challenge.exe'
        $dialog.Filter = '4Wheel Challenge.exe|4Wheel Challenge.exe'
        $dialog.CheckFileExists = $true
        $dialog.Multiselect = $false
        try {
            if ($dialog.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) {
                $selected = Full-Path (Split-Path -Parent $dialog.FileName)
                if (Is-GameRoot $selected) { return $selected }
                throw '选中的文件旁边缺少游戏数据，请选择 Steam 的完整安装目录。'
            }
        } finally { $dialog.Dispose() }
    }
    throw '没有找到 Steam 游戏。请先安装游戏，或使用：Start-LocalCoop.cmd -GamePath "游戏安装目录"'
}

function Get-SourceStamp([string]$Source) {
    $build = ''
    $manifest = Join-Path (Split-Path -Parent (Split-Path -Parent $Source)) ('appmanifest_' + $AppId + '.acf')
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $text = [IO.File]::ReadAllText($manifest)
        if ((Read-VdfValue $text 'appid') -eq $AppId) { $build = Read-VdfValue $text 'buildid' }
    }
    $files = @($GameExeName, 'GameAssembly.dll', 'UnityPlayer.dll',
        ($GameDataName + '\il2cpp_data\Metadata\global-metadata.dat'), ($GameDataName + '\globalgamemanagers'))
    $parts = New-Object 'System.Collections.Generic.List[string]'
    $parts.Add($Source.ToLowerInvariant())
    $parts.Add([string]$build)
    foreach ($relative in $files) {
        $path = Join-Path $Source $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $file = Get-Item -LiteralPath $path
            $parts.Add($relative + '|' + $file.Length + '|' + $file.LastWriteTimeUtc.Ticks)
        }
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($parts -join "`n")))).Replace('-', '') }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Fingerprint = $digest; BuildId = $build }
}

function Get-GameFiles([string]$Source) {
    # Copy only the known Unity distribution layout. Root-level mods, source,
    # launchers, profiles and release folders are never part of the copy set.
    $rootFiles = @($GameExeName, 'GameAssembly.dll', 'UnityPlayer.dll', 'baselib.dll',
        'UnityCrashHandler64.exe', 'steam_api64.dll', 'steam_api.dll')
    foreach ($name in $rootFiles) {
        $path = Join-Path $Source $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "游戏文件不能是链接：$path" }
            [pscustomobject]@{ Relative = $name; Source = $item.FullName; Length = [long]$item.Length; Ticks = $item.LastWriteTimeUtc.Ticks }
        }
    }
    $excluded = @('BepInEx', 'MelonLoader', 'Mods', 'UserData', 'UserLibs', 'dotnet', 'doorstop_libs',
        'publish', 'runtime-test', 'backups', 'analysis', 'tools', 'source', 'payload', 'launcher',
        '.git', '.github', '.vs', '.vscode', 'node_modules', '4wheel-local-splitscreen-mod')
    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push((Join-Path $Source $GameDataName))
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        $directoryItem = Get-Item -LiteralPath $directory -Force
        if (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "游戏数据目录不能是链接：$directory" }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "游戏数据包含链接，已停止复制：$($item.FullName)" }
            if ($item.PSIsContainer) {
                if ($excluded -notcontains $item.Name) { $stack.Push($item.FullName) }
            } else {
                [pscustomobject]@{ Relative = $item.FullName.Substring($Source.Length + 1); Source = $item.FullName;
                    Length = [long]$item.Length; Ticks = $item.LastWriteTimeUtc.Ticks }
            }
        }
    }
}

function Read-State([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json) }
    catch { throw "独立副本记录损坏，请保留文件并查看错误：$Path" }
}

function Write-Utf8([string]$Path, [string]$Text) {
    Assert-CachePath $Path $CacheRoot
    Assert-NoReparseParents $Path
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Ensure-Directory([string]$Path) {
    Assert-CachePath $Path $CacheRoot
    Assert-NoReparseParents $Path
    [void][IO.Directory]::CreateDirectory($Path)
}

function Get-LoaderArchive([string]$Bundled, [string]$Cached) {
    Assert-NoReparseParents $Cached
    $archive = if (Test-Path -LiteralPath $Bundled -PathType Leaf) { $Bundled } else { $Cached }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Write-Host '首次准备：正在下载启动组件，请稍候…'
        Ensure-Directory (Split-Path -Parent $Cached)
        $temporary = $Cached + '.download'
        Assert-CachePath $temporary $CacheRoot
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $LoaderUrl -UseBasicParsing -OutFile $temporary
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $LoaderSha256) { throw '启动组件校验失败，已停止安装。' }
            Move-Item -LiteralPath $temporary -Destination $Cached -Force
        } finally {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
        }
        $archive = $Cached
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $LoaderSha256) {
        throw "启动组件 SHA-256 不匹配，请重新下载发布包或删除这个下载文件后重试：$archive"
    }
    return $archive
}

function Expand-Loader([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            if (-not $relative) { continue }
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw '启动组件包含无效路径。' }
            $target = Full-Path (Join-Path $Destination $relative)
            Assert-CachePath $target $Destination
            Assert-NoReparseParents $target
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) {
                Ensure-Directory $target
            } else {
                Ensure-Directory (Split-Path -Parent $target)
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $zip.Dispose() }
}

try {
    $CacheRoot = Full-Path $CacheRoot
    $SourceRoot = Find-GameRoot
    $OwnedGame = Join-Path $CacheRoot 'Game'
    if ($CacheRoot -eq $SourceRoot -or (Is-Inside $CacheRoot $SourceRoot) -or (Is-Inside $SourceRoot $CacheRoot)) {
        throw '独立副本目录不能与原版游戏目录重叠。'
    }
    Assert-NoReparseParents $CacheRoot
    if (-not $PayloadPath) {
        $PayloadPath = Join-Path $PSScriptRoot 'payload'
        if (-not (Test-Path -LiteralPath $PayloadPath -PathType Container)) { $PayloadPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'payload' }
    }
    $PayloadPath = Full-Path $PayloadPath
    $PluginSource = Join-Path $PayloadPath 'WheelchairLocalCoop.dll'
    if (-not (Test-Path -LiteralPath $PluginSource -PathType Leaf)) { throw '发布包不完整：找不到 payload\WheelchairLocalCoop.dll。请先完整解压。' }
    $PluginHash = (Get-FileHash -LiteralPath $PluginSource -Algorithm SHA256).Hash
    $MarkerPath = Join-Path $CacheRoot 'launcher-owned.json'
    $StatePath = Join-Path $CacheRoot 'install-state.json'
    $Marker = Read-State $MarkerPath
    if ($Marker -and $Marker.product -ne $ProductId) { throw '此缓存目录不属于本启动器，已停止。' }
    if (-not $Marker -and (Test-Path -LiteralPath $CacheRoot) -and @(Get-ChildItem -LiteralPath $CacheRoot -Force).Count -gt 0) {
        throw "独立副本目录已有不明文件，启动器不会覆盖：$CacheRoot"
    }
    $State = Read-State $StatePath
    if ($State -and ($State.product -ne $ProductId -or $State.schema -ne 1)) { throw '不支持当前独立副本记录格式。' }
    $Stamp = Get-SourceStamp $SourceRoot
    $NeedGame = $Repair -or -not $State -or $State.sourceFingerprint -ne $Stamp.Fingerprint -or -not (Is-GameRoot $OwnedGame)
    $NeedLoader = $Repair -or -not $State -or $State.loaderSha256 -ne $LoaderSha256 -or
        -not (Test-Path -LiteralPath (Join-Path $OwnedGame 'winhttp.dll') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $OwnedGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') -PathType Leaf)
    $PluginTarget = Join-Path $OwnedGame 'BepInEx\plugins\WheelchairLocalCoop.dll'
    $NeedPlugin = $Repair -or -not $State -or $State.pluginSha256 -ne $PluginHash -or -not (Test-Path -LiteralPath $PluginTarget -PathType Leaf)
    $Files = @()
    [long]$CopyBytes = 0
    if ($NeedGame) {
        $Files = @(Get-GameFiles $SourceRoot)
        $CopyBytes = [long](($Files | Measure-Object -Property Length -Sum).Sum)
    }
    $BundledLoader = Join-Path $PayloadPath $LoaderName
    $CachedLoader = Join-Path $CacheRoot ('Downloads\' + $LoaderName)
    [long]$LoaderBytes = if ($NeedLoader) { 256MB } else { 0 }
    [long]$RequiredBytes = if ($NeedGame -or $NeedLoader) { $CopyBytes + $LoaderBytes + 1GB } else { 16MB }
    $Volume = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($CacheRoot))
    [long]$AvailableBytes = $Volume.AvailableFreeSpace
    $Plan = [ordered]@{
        product = $ProductId; version = $LauncherVersion; source = $SourceRoot; destination = $OwnedGame
        steamBuildId = $Stamp.BuildId; copyGame = [bool]$NeedGame; installLoader = [bool]$NeedLoader
        installPlugin = [bool]$NeedPlugin; gameFiles = $Files.Count; gameBytes = $CopyBytes
        requiredFreeBytes = $RequiredBytes; availableFreeBytes = $AvailableBytes
        downloadNeeded = [bool]($NeedLoader -and -not (Test-Path -LiteralPath $BundledLoader -PathType Leaf) -and -not (Test-Path -LiteralPath $CachedLoader -PathType Leaf))
        loaderUrl = $LoaderUrl; loaderSha256 = $LoaderSha256; players = $Players
        arguments = @('-force-d3d11', '-localcoop-profile'); dryRun = [bool]$DryRun; prepareOnly = [bool]$PrepareOnly
    }
    if ($DryRun) {
        if ($Json) { $Plan | ConvertTo-Json -Depth 5 }
        else {
            Write-Host '检查完成：没有复制、下载或启动游戏。'
            Write-Host ("原版：{0}`n独立副本：{1}`n需要约 {2:N2} GB 可用空间。" -f $SourceRoot, $OwnedGame, ($RequiredBytes / 1GB))
        }
        exit 0
    }
    if ($AvailableBytes -lt $RequiredBytes) { throw ("空间不足：需要约 {0:N2} GB，当前可用 {1:N2} GB。" -f ($RequiredBytes / 1GB), ($AvailableBytes / 1GB)) }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $Mutex = New-Object Threading.Mutex($false, ('Local\4WheelLocalSplitScreenMod-' + $sid))
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw '另一个启动器正在准备游戏，请稍候。' }
    $OwnedExe = Join-Path $OwnedGame $GameExeName
    foreach ($process in Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExeName)) -ErrorAction SilentlyContinue) {
        if (-not $PrepareOnly) { throw '游戏已经运行。请先关闭轮椅模拟器，再双击启动器；不会替你结束游戏。' }
        try { $runningPath = $process.Path } catch { $runningPath = $null }
        if ($runningPath -and (Full-Path $runningPath) -eq (Full-Path $OwnedExe)) {
            throw '独立副本正在运行，请先关闭它再准备或修复文件。'
        }
    }
    [void][IO.Directory]::CreateDirectory($CacheRoot)
    if (-not $Marker) { Write-Utf8 $MarkerPath ('{"product":"' + $ProductId + '","schema":1}') }
    Ensure-Directory $OwnedGame
    if ($NeedGame) {
        Write-Host '首次准备或游戏已更新：正在复制游戏，请稍候…'
        foreach ($file in $Files) {
            $target = Full-Path (Join-Path $OwnedGame $file.Relative)
            Assert-CachePath $target $OwnedGame
            Ensure-Directory (Split-Path -Parent $target)
            Assert-NoReparseParents $target
            $copy = $true
            if (-not $Repair -and (Test-Path -LiteralPath $target -PathType Leaf)) {
                $existing = Get-Item -LiteralPath $target
                $copy = $existing.Length -ne $file.Length -or $existing.LastWriteTimeUtc.Ticks -ne $file.Ticks
            }
            if ($copy) { Copy-Item -LiteralPath $file.Source -Destination $target -Force }
        }
        # No recursive purge. Only obsolete files recorded as game files by a
        # previous install may be removed; profiles/configs are never on this list.
        if ($State -and $State.gameFiles) {
            $current = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($file in $Files) { [void]$current.Add($file.Relative) }
            foreach ($old in $State.gameFiles) {
                if ($current.Contains([string]$old)) { continue }
                if ([IO.Path]::IsPathRooted([string]$old) -or ([string]$old).Contains(':')) { throw '副本记录包含无效路径，已停止。' }
                if ([string]$old -match '(^|[\\/])\.\.?([\\/]|$)') { throw '副本记录包含路径穿越，已停止。' }
                $wasGameFile = ([string]$old).StartsWith(($GameDataName + '\'), [StringComparison]::OrdinalIgnoreCase) -or
                    @($GameExeName, 'GameAssembly.dll', 'UnityPlayer.dll', 'baselib.dll', 'UnityCrashHandler64.exe', 'steam_api64.dll', 'steam_api.dll') -contains [string]$old
                if (-not $wasGameFile) { throw '副本记录列出了非游戏文件，已停止清理。' }
                $obsolete = Full-Path (Join-Path $OwnedGame ([string]$old))
                Assert-CachePath $obsolete $OwnedGame
                Assert-NoReparseParents $obsolete
                if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
            }
        }
    }
    if ($NeedLoader) {
        $archive = Get-LoaderArchive $BundledLoader $CachedLoader
        Expand-Loader $archive $OwnedGame
    }
    Ensure-Directory (Split-Path -Parent $PluginTarget)
    Assert-NoReparseParents $PluginTarget
    if ($NeedPlugin) { Copy-Item -LiteralPath $PluginSource -Destination $PluginTarget -Force }
    Ensure-Directory (Join-Path $OwnedGame 'BepInEx\config')
    Ensure-Directory (Join-Path $OwnedGame 'BepInEx\local-profile')
    $PluginConfig = Join-Path $OwnedGame 'BepInEx\config\local.wheelchair.couchcoop.cfg'
    if (-not (Test-Path -LiteralPath $PluginConfig -PathType Leaf)) {
        Write-Utf8 $PluginConfig ("[LocalCoop]`r`nEnabled = true`r`nPlayers = $Players`r`nShowHud = true`r`n")
    } elseif ($PlayersSpecified) {
        $config = [IO.File]::ReadAllText($PluginConfig)
        if ($config -match '(?m)^Players\s*=') { $config = [regex]::Replace($config, '(?m)^Players\s*=.*$', ('Players = ' + $Players)) }
        else { $config += "`r`nPlayers = $Players`r`n" }
        Write-Utf8 $PluginConfig $config
    }
    $BepConfig = Join-Path $OwnedGame 'BepInEx\config\BepInEx.cfg'
    if (-not (Test-Path -LiteralPath $BepConfig -PathType Leaf)) {
        Write-Utf8 $BepConfig ("[IL2CPP]`r`nUpdateInteropAssemblies = true`r`n`r`n[Logging.Console]`r`nEnabled = false`r`n")
    }
    Write-Utf8 (Join-Path $OwnedGame 'steam_appid.txt') ($AppId + "`r`n")
    $savedFiles = if ($NeedGame) { @($Files | ForEach-Object { $_.Relative }) } else { @($State.gameFiles) }
    $newState = [ordered]@{ product = $ProductId; schema = 1; version = $LauncherVersion
        sourceFingerprint = $Stamp.Fingerprint; steamBuildId = $Stamp.BuildId; loaderSha256 = $LoaderSha256
        pluginSha256 = $PluginHash; gameFiles = $savedFiles; updatedUtc = [DateTime]::UtcNow.ToString('o') }
    Write-Utf8 ($StatePath + '.tmp') ($newState | ConvertTo-Json -Depth 5)
    Move-Item -LiteralPath ($StatePath + '.tmp') -Destination $StatePath -Force
    if ($PrepareOnly) { Write-Host '准备完成，未启动游戏。'; exit 0 }
    if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) { throw '请先打开 Steam 并登录，然后再次双击启动器。' }
    Write-Host '正在启动本地分屏。首次启动可能需要几分钟，请不要重复点击。'
    Start-Process -FilePath $OwnedExe -WorkingDirectory $OwnedGame -ArgumentList @('-force-d3d11', '-localcoop-profile') -WindowStyle Normal | Out-Null
    exit 0
} catch {
    [Console]::Error.WriteLine('启动失败：' + $_.Exception.Message)
    exit 1
} finally {
    if ($MutexHeld -and $Mutex) { $Mutex.ReleaseMutex() }
    if ($Mutex) { $Mutex.Dispose() }
}
