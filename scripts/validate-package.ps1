[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.18', [switch]$SkipInstallation, [switch]$InteractiveDesktop, [switch]$ContentOnly, [ValidatePattern('^[0-9a-fA-F]{32}$')][string]$RestorePreferencesSnapshot)

$ErrorActionPreference = 'Stop'
if (-not $ContentOnly -and -not $InteractiveDesktop -and -not $RestorePreferencesSnapshot) {
    throw '包运行验收会打开窗口、播放声音并操作真实桌面。请安排主机交互时段后显式传入 -InteractiveDesktop；本次未启动应用或安装程序。'
}
if ($RestorePreferencesSnapshot -and $ContentOnly) { throw '偏好恢复入口不能与 -ContentOnly 同时使用。' }
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo "artifacts/release/DogSC-$Version-win-x64"
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$null = New-Item -ItemType Directory -Path $validation -Force
$preferenceSnapshotId = [guid]::NewGuid().ToString('N')
$preferenceSnapshotPath = Join-Path $validation "user-preferences-$preferenceSnapshotId.json"
$preferenceSnapshotCreated = $false
$preferenceRestoreFailed = $false
$portableArchiveBaselineVerified = $false
$report = [ordered]@{ Version=$Version; Scope=$(if ($ContentOnly) { 'content-only' } elseif ($SkipInstallation) { 'portable-only' } else { 'interactive-package' }); StartedUtc=[DateTime]::UtcNow.ToString('O'); Passed=$false; Checks=@(); Limitations=@('本机 Windows 11 x64；不代替没有运行库的干净虚拟机。') }
if ($ContentOnly) { $report.Limitations += '仅后台检查包内容与哈希，未执行播放、录制或安装卸载，不替代交互运行验收。' }
else { $report.Limitations += '应用运行验证隔离 PATH 与 DOTNET_ROOT，使用当前真实桌面。' }
function Get-PreferenceTargets([string]$LocalDataRoot) {
    $appData = Join-Path $LocalDataRoot 'FrameTrail'
    return @(
        [ordered]@{ Name='settings.json'; Path=(Join-Path $appData 'settings.json') },
        [ordered]@{ Name='recent-projects.json'; Path=(Join-Path $appData 'recent-projects.json') },
        [ordered]@{ Name='memo.txt'; Path=(Join-Path $appData 'memo.txt') },
        [ordered]@{ Name='memo.txt.settings.json'; Path=(Join-Path $appData 'memo.txt.settings.json') }
    )
}
function Write-JsonCreateNew([string]$Path, $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 8))
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Write-ValidationReport([string]$Path, $Value, [bool]$PreferenceRestoreFailed) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
    if ($PreferenceRestoreFailed) { throw '用户偏好恢复失败；验证报告已写入，退出码必须为非零。' }
}
function Get-PreferenceRecoveryCommand([string]$SnapshotId) {
    $executable = Join-Path $PSHOME 'pwsh.exe'
    if (-not [IO.File]::Exists($executable)) { $executable = 'pwsh' }
    return "& `"$executable`" -NoProfile -File `"$PSCommandPath`" -Version $Version -RestorePreferencesSnapshot $SnapshotId"
}
function New-PreferenceSnapshot([string]$Path, [string]$SnapshotId, [string]$LocalDataRoot) {
    $localData = $LocalDataRoot
    $files = foreach ($target in Get-PreferenceTargets $localData) {
        $present = [IO.File]::Exists($target.Path)
        [ordered]@{ Name=$target.Name; Exists=$present; ContentBase64=$(if ($present) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($target.Path)) } else { $null }) }
    }
    Write-JsonCreateNew $Path ([ordered]@{ Version=1; SnapshotId=$SnapshotId; CreatedUtc=[DateTime]::UtcNow.ToString('O'); Files=@($files) })
}
function Restore-PreferenceSnapshot([string]$Path, [string]$ExpectedId, [string]$LocalDataRoot) {
    if (-not [IO.File]::Exists($Path)) { throw "偏好恢复快照不存在：$Path" }
    $snapshot = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($snapshot.Version -ne 1 -or $snapshot.SnapshotId -ne $ExpectedId -or @($snapshot.Files).Count -ne 4) { throw '偏好恢复快照格式、编号或文件数量无效。' }
    $localData = $LocalDataRoot
    $targets = @(Get-PreferenceTargets $localData)
    $allowedNames = @($targets | ForEach-Object Name)
    $seen = @{}
    $validated = [Collections.Generic.List[object]]::new()
    foreach ($entry in $snapshot.Files) {
        if ($entry.Name -notin $allowedNames -or $seen.ContainsKey([string]$entry.Name)) { throw "快照包含不允许或重复的目标：$($entry.Name)" }
        if ($entry.Exists -isnot [bool]) { throw "快照文件存在状态不是布尔值：$($entry.Name)" }
        $seen[[string]$entry.Name] = $true
        $target = $targets | Where-Object Name -eq $entry.Name | Select-Object -First 1
        if ($null -eq $target) { throw "快照目标未映射到固定偏好文件：$($entry.Name)" }
        $bytes = $null
        if ($entry.Exists) {
            if ($entry.ContentBase64 -isnot [string]) { throw "快照文件缺少原始字节：$($entry.Name)" }
            try { $bytes = [Convert]::FromBase64String($entry.ContentBase64) } catch { throw "快照文件 Base64 无效：$($entry.Name)" }
        } elseif ($null -ne $entry.ContentBase64) {
            throw "原先不存在的文件不应包含内容：$($entry.Name)"
        }
        $validated.Add([pscustomobject]@{ Name=[string]$entry.Name; Path=[string]$target.Path; Exists=[bool]$entry.Exists; Bytes=$bytes })
    }
    if ($seen.Count -ne 4) { throw '偏好恢复快照未覆盖全部固定目标。' }
    # No user preference is changed until the complete manifest and every byte payload have passed validation.
    foreach ($entry in $validated) {
        if ($entry.Exists) {
            $bytes = [byte[]]$entry.Bytes
            $parent = Split-Path -Parent $entry.Path
            $null = New-Item -ItemType Directory -Path $parent -Force
            $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($entry.Path) + '.restore-' + [guid]::NewGuid().ToString('N'))
            try {
                [IO.File]::WriteAllBytes($temporary, $bytes)
                if ([IO.File]::Exists($entry.Path)) { [IO.File]::Move($temporary, $entry.Path, $true) } else { [IO.File]::Move($temporary, $entry.Path) }
            } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
            $actual = [IO.File]::ReadAllBytes($entry.Path)
            if ($actual.Length -ne $bytes.Length -or [BitConverter]::ToString($actual) -cne [BitConverter]::ToString($bytes)) { throw "恢复后的字节校验失败：$($entry.Name)" }
        } else {
            if ([IO.File]::Exists($entry.Path)) { [IO.File]::Delete($entry.Path) }
            if ([IO.File]::Exists($entry.Path)) { throw "未能恢复文件原先不存在的状态：$($entry.Name)" }
        }
    }
    return $true
}
if ($RestorePreferencesSnapshot) {
    $restoreId = $RestorePreferencesSnapshot.ToLowerInvariant()
    $restorePath = Join-Path $validation "user-preferences-$restoreId.json"
    $restoreReportPath = Join-Path $validation "user-preferences-$restoreId-restore-result.json"
    try {
        $null = Restore-PreferenceSnapshot $restorePath $restoreId ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
        $marker = Join-Path $validation "user-preferences-$restoreId.restored"
        if (-not [IO.File]::Exists($marker)) { Write-JsonCreateNew $marker ([ordered]@{ SnapshotId=$restoreId; RestoredUtc=[DateTime]::UtcNow.ToString('O') }) }
        [ordered]@{ SnapshotId=$restoreId; Passed=$true; Snapshot=$restorePath; RestoredUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $restoreReportPath -Encoding utf8
        return
    } catch {
        [ordered]@{ SnapshotId=$restoreId; Passed=$false; Snapshot=$restorePath; RecoveryCommand=(Get-PreferenceRecoveryCommand $restoreId); Error=$_.Exception.ToString() } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $restoreReportPath -Encoding utf8
        throw
    }
}
function Run-OwnedProcess([string]$Exe, [string[]]$Arguments, [bool]$Isolate = $false, [int]$TimeoutSeconds = 180) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $Exe
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($Isolate) {
        $start.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
        $start.Environment['DOTNET_ROOT'] = Join-Path $validation 'absent-dotnet'
        $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.Environment.Remove('FRAMETRAIL_FFMPEG_DIR') | Out-Null
        $start.Environment.Remove('FRAMETRAIL_FFPROBE') | Out-Null
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "验证进程超时，已停止本次启动的进程树：$Exe"
        }
        if ($process.ExitCode -ne 0) { throw "验证进程失败 ($($process.ExitCode))：$Exe" }
    } finally { $process.Dispose() }
}
function Check-Report([string]$Path, [string]$Id, [string]$Name) {
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $data.Passed) { throw "$Name 未通过：$($data.Error)" }
    $report.Checks += [ordered]@{ Id=$Id; Name=$Name; Passed=$true; Report=$Path }
}
function Assert-PortableArchive([string]$ArchivePath, [string]$DirectoryPath) {
    # Inspect the archive itself so a stale ZIP cannot inherit a newer directory's result.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $directoryRoot = [IO.Path]::GetFullPath($DirectoryPath).TrimEnd('\') + '\'
    $expected = @{}
    foreach ($file in Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File) {
        $relative = $file.FullName.Substring($directoryRoot.Length).Replace('\', '/')
        $expected.Add($relative, $file)
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $seen = @{}
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = $entry.FullName.Replace('\', '/')
            if ($seen.ContainsKey($name) -or -not $expected.ContainsKey($name)) { throw "ZIP包含重复或非预期文件：$name" }
            $seen.Add($name, $true)
            $file = $expected[$name]
            if ($entry.Length -ne $file.Length) { throw "ZIP文件长度与发布目录不一致：$name" }
            $stream = $entry.Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [Convert]::ToHexString($algorithm.ComputeHash($stream)) }
            finally { $algorithm.Dispose(); $stream.Dispose() }
            if ($entryHash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "ZIP文件内容与发布目录不一致：$name" }
        }
        if ($seen.Count -ne $expected.Count) { throw 'ZIP缺少发布目录中的文件。' }
        return $seen.Count
    } finally { $archive.Dispose() }
}
function Copy-SampleProjectForPlayback([string]$ProjectPath, [string]$DestinationDirectory) {
    $sourceDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $ProjectPath))
    $destination = [IO.Path]::GetFullPath($DestinationDirectory)
    if (-not [IO.File]::Exists($ProjectPath)) { throw "播放样例工程不存在：$ProjectPath" }
    if ([IO.Directory]::Exists($destination) -or [IO.File]::Exists($destination)) { throw "播放样例副本目录已存在：$destination" }
    $sourceItems = @(Get-ChildItem -LiteralPath $sourceDirectory -Force -Recurse)
    if (@($sourceItems | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) {
        throw "播放样例源目录包含重解析点：$sourceDirectory"
    }
    $null = New-Item -ItemType Directory -Path $destination
    foreach ($item in $sourceItems) {
        $relative = $item.FullName.Substring($sourceDirectory.TrimEnd('\').Length).TrimStart('\')
        $target = Join-Path $destination $relative
        if ($item.PSIsContainer) {
            $null = New-Item -ItemType Directory -Path $target -Force
        } else {
            $parent = Split-Path -Parent $target
            $null = New-Item -ItemType Directory -Path $parent -Force
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
    $copiedItems = @(Get-ChildItem -LiteralPath $destination -Force -Recurse -File)
    $sourceFiles = @($sourceItems | Where-Object { -not $_.PSIsContainer })
    if ($copiedItems.Count -ne $sourceFiles.Count) { throw "播放样例副本文件数量不一致：$destination" }
    foreach ($sourceFile in $sourceFiles) {
        $relative = $sourceFile.FullName.Substring($sourceDirectory.TrimEnd('\').Length).TrimStart('\')
        $copyPath = Join-Path $destination $relative
        if (-not [IO.File]::Exists($copyPath) -or
            (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash) {
            throw "播放样例副本校验失败：$relative"
        }
    }
    return (Join-Path $destination ([IO.Path]::GetFileName($ProjectPath)))
}
function Record-PortableArchiveUnchanged([string]$ArchivePath, [string]$DirectoryPath) {
    try {
        $fileCount = Assert-PortableArchive $ArchivePath $DirectoryPath
        $report.Checks += [ordered]@{ Id='portable-archive-unchanged'; Name='整轮包验收结束时发布目录每个文件仍与原始 ZIP 逐字节一致'; Passed=$true; FileCount=$fileCount }
        return $true
    } catch {
        $report.Checks += [ordered]@{ Id='portable-archive-unchanged'; Name='整轮包验收结束时发布目录每个文件仍与原始 ZIP 逐字节一致'; Passed=$false; Error=$_.Exception.ToString() }
        return $false
    }
}
try {
    if (-not $ContentOnly) {
        $unrestored = @()
        foreach ($candidate in Get-ChildItem -LiteralPath $validation -Filter 'user-preferences-*.json' -File) {
            if ($candidate.Name -match '^user-preferences-([0-9a-f]{32})\.json$') {
                $candidateId = $Matches[1]
                if (-not (Test-Path -LiteralPath (Join-Path $validation "user-preferences-$candidateId.restored"))) { $unrestored += $candidate }
            }
        }
        if ($unrestored.Count -gt 0) { throw "发现尚未恢复的用户偏好快照，先使用 -RestorePreferencesSnapshot <UUID> 恢复并校验：$($unrestored[0].FullName)" }
        New-PreferenceSnapshot $preferenceSnapshotPath $preferenceSnapshotId ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
        $preferenceSnapshotCreated = $true
        $report.PreferenceSnapshotId = $preferenceSnapshotId
        $report.PreferenceSnapshot = $preferenceSnapshotPath
        $report.PreferenceRecoveryCommand = Get-PreferenceRecoveryCommand $preferenceSnapshotId
        $report.Checks += [ordered]@{ Id='preferences-snapshot'; Name='交互验收前保存四个固定用户偏好文件的原始字节与存在状态'; Passed=$true; SnapshotId=$preferenceSnapshotId }
    }
    $exe = Join-Path $release 'app/DogSC.exe'
    $sample = Join-Path $release 'samples/FrameTrail-Demo/FrameTrail-Demo.frametrail'
    foreach ($path in @($exe, (Join-Path $release 'app/DogSC.dll'), (Join-Path $release 'app/DogSC.deps.json'), (Join-Path $release 'app/DogSC.runtimeconfig.json'), $sample, (Join-Path $release 'app/coreclr.dll'), (Join-Path $release 'app/hostfxr.dll'), (Join-Path $release 'app/hostpolicy.dll'), (Join-Path $release 'app/tools/ffmpeg/ffmpeg.exe'), (Join-Path $release 'app/tools/ffmpeg/ffprobe.exe'), (Join-Path $release 'README.md'), (Join-Path $release 'docs/assets/DogSC.png'), (Join-Path $release 'LICENSE'), (Join-Path $release 'licenses/DogSC-MIT.txt'), (Join-Path $release 'licenses/DogSCWindows-MIT.txt'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "发行包缺少必要文件：$path" }
    }
    if (-not (Get-Content -LiteralPath (Join-Path $release 'README.md') -Raw).Contains('docs/assets/DogSC.png')) {
        throw '便携 README 未引用随包复制的 DogSC 品牌图标。'
    }
    $appInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($appInfo.ProductName -ne 'DogSC' -or $appInfo.FileVersion -notmatch "^$([regex]::Escape($Version))(\.0)?$" -or
        $appInfo.LegalCopyright -notlike '*神烦老狗*') { throw '主程序品牌、版本或原作者署名不匹配。' }
    $logoHash = (Get-FileHash -LiteralPath (Join-Path $release 'docs/assets/DogSC.png') -Algorithm SHA256).Hash
    if ($logoHash -ne '1F1A16C58905C0901D408F3D3DF39CCCA5070C34C820A8BD6B0255230E554AF8') { throw '随包 Logo 与固定 DogSC 原图不一致。' }
    $bundledLicense = [IO.File]::ReadAllText((Join-Path $release 'licenses/DogSC-MIT.txt')).Replace("`r`n", "`n").Trim()
    $upstreamLicense = [IO.File]::ReadAllText((Join-Path $repo 'upstream/dogsc/LICENSE')).Replace("`r`n", "`n").Trim()
    if ($bundledLicense -cne $upstreamLicense) { throw '随包 MIT 声明与原仓库文本不一致。' }
    $report.Checks += [ordered]@{ Id='dogsc-branding'; Name='DogSC 产品名、版本、原作者版权、原版 Logo 哈希与上游 MIT 声明一致'; Passed=$true }
    $report.PortableArchiveFilesVerified = Assert-PortableArchive (Join-Path $repo "artifacts/release/DogSC-$Version-win-x64.zip") $release
    $nativeManifest = Get-Content -LiteralPath (Join-Path $repo 'third_party/ffmpeg/manifest.json') -Raw | ConvertFrom-Json
    if ($nativeManifest.schemaVersion -ne 1 -or $nativeManifest.version -ne '8.1.2' -or [string]$nativeManifest.archive.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [string]$nativeManifest.sourceArchive.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'FFmpeg native manifest is incomplete or unsupported.' }
    $nativeAssetPath = Join-Path $repo "artifacts/release/$($nativeManifest.archive.file)"
    $sourceAssetPath = Join-Path $repo "artifacts/release/$($nativeManifest.sourceArchive.file)"
    foreach ($asset in @(@{Path=$nativeAssetPath; Hash=([string]$nativeManifest.archive.sha256).ToLowerInvariant()},@{Path=$sourceAssetPath; Hash=([string]$nativeManifest.sourceArchive.sha256).ToLowerInvariant()})) {
        if (-not (Test-Path -LiteralPath $asset.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Hash) { throw "FFmpeg distribution asset is missing or mismatched: $($asset.Path)" }
    }
    $nativeRuntimeRoot = Join-Path $release 'app/tools/libav'
    $nativeCliRoot = Join-Path $release 'app/tools/ffmpeg'
    foreach ($entry in $nativeManifest.runtimeFiles) {
        $relative = ([string]$entry.path).Replace('\','/')
        $target = if ($relative.StartsWith('cli/',[StringComparison]::Ordinal)) { Join-Path $nativeCliRoot ([IO.Path]::GetFileName($relative)) } else { Join-Path $nativeRuntimeRoot ([IO.Path]::GetFileName($relative)) }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Packaged native runtime hash mismatch: $relative" }
    }
    foreach ($entry in $nativeManifest.supportFiles) {
        $relative = ([string]$entry.path).Replace('\','/')
        $target = Join-Path $nativeRuntimeRoot ([IO.Path]::GetFileName($relative))
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Packaged native support file hash mismatch: $relative" }
    }
    $sourceMaterialsRoot = Join-Path $release 'licenses/libav-corresponding-source'
    foreach ($entry in $nativeManifest.sourceFiles) {
        $relative = ([string]$entry.path).Replace('\','/') -replace '^source-materials/',''
        $target = Join-Path $sourceMaterialsRoot $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Packaged native corresponding source hash mismatch: $relative" }
    }
    $expectedSourceNames = @($nativeManifest.sourceFiles | ForEach-Object { ([string]$_.path).Replace('\','/') -replace '^source-materials/','' } | Sort-Object)
    $actualSourceNames = @(Get-ChildItem -LiteralPath $sourceMaterialsRoot -Recurse -File | ForEach-Object { $_.FullName.Substring(([IO.Path]::GetFullPath($sourceMaterialsRoot).TrimEnd('\') + '\').Length).Replace('\','/') } | Sort-Object)
    if (@(Compare-Object $expectedSourceNames $actualSourceNames).Count -ne 0) { throw 'Packaged native corresponding source tree contains unmanifested or missing files.' }
    $cliNames = @('ffmpeg.exe','ffprobe.exe')
    $actualCliNames = @(Get-ChildItem -LiteralPath $nativeCliRoot -File -Filter '*.exe' | Select-Object -ExpandProperty Name | Sort-Object)
    if (@(Compare-Object $cliNames $actualCliNames).Count -ne 0) { throw 'Packaged FFmpeg CLI contains extra or missing executables.' }
    $expectedLibavNames = @('avcodec-62.dll','avformat-62.dll','avutil-60.dll','swscale-9.dll','swresample-6.dll')
    $actualLibavNames = @(Get-ChildItem -LiteralPath $nativeRuntimeRoot -File -Filter '*.dll' | Select-Object -ExpandProperty Name | Sort-Object)
    if (@(Compare-Object $expectedLibavNames $actualLibavNames).Count -ne 0) { throw 'Packaged native decoder contains extra or missing DLLs.' }
    $report.Checks += [ordered]@{ Id='native-media-distribution'; Name='原生解码 DLL、静态 CLI、许可证与完整对应源码均匹配固定 manifest'; Passed=$true; ArchiveSha256=$nativeManifest.archive.sha256; SourceArchiveSha256=$nativeManifest.sourceArchive.sha256 }
    $portableArchiveBaselineVerified = $true
    $expectedSamples = @('FrameTrail-Demo.frametrail', 'FrameTrail-Demo.mp4', 'synthetic-screen.mp4', 'synthetic-camera.mp4', 'independent-tone.wav', 'demo-badge.png', 'preview.png', 'README.md') | Sort-Object
    $sampleDirectory = Split-Path -Parent $sample
    $actualSamples = @(Get-ChildItem -LiteralPath $sampleDirectory -Force)
    if (@($actualSamples | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0 -or
        @(Compare-Object $expectedSamples @($actualSamples.Name | Sort-Object)).Count -gt 0) { throw '演示工程包含非预期文件、目录、链接或缺少固定生成物。' }
    $report.SampleFilesVerified = $expectedSamples
    $report.Checks += [ordered]@{ Id='portable-archive-content'; Name='主程序、自包含运行库、原创样例与许可证齐备，ZIP逐文件内容一致'; Passed=$true }
    if (-not $ContentOnly) {
    $playback = Join-Path $validation 'portable-playback.json'
    $portableSample = Copy-SampleProjectForPlayback $sample (Join-Path $validation "portable-playback-sample-$preferenceSnapshotId")
    Run-OwnedProcess $exe @('--open', $portableSample, '--playback-smoke', $playback) $true
    Check-Report $playback 'portable-playback' '隔离开发环境后的便携版有声播放与暂停恢复'
    $workflow = Join-Path $validation 'portable-workflow'
    Run-OwnedProcess $exe @('--workflow-smoke', $workflow, '--with-pause', '--with-input-stimulus', '--record-seconds', '6') $true
    Check-Report (Join-Path $workflow 'workflow-result.json') 'portable-workflow' '便携版真实录制、暂停、编辑、重开及导出'
    if (-not $SkipInstallation) {
        $registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9EB9A39E-9EF9-4E70-9D2B-E081ECBC1A68}_is1'
        if (Test-Path -LiteralPath $registry) { throw '存在已安装的 FrameTrail；为保留用户安装，本次跳过自动覆盖。' }
        $install = Join-Path $validation "installed-$preferenceSnapshotId"
        if (Test-Path -LiteralPath $install) { throw "安装验证目录已存在，请使用新的版本或手动检查：$install" }
        $installer = Join-Path $repo "artifacts/release/DogSC-Setup-$Version-win-x64.exe"
        Run-OwnedProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$install", "/LOG=$(Join-Path $validation "install-$preferenceSnapshotId.log")") $false 300
        $registration = Get-ItemProperty -LiteralPath $registry
        $registeredPath = [IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\')
        if ($registeredPath -ne [IO.Path]::GetFullPath($install).TrimEnd('\')) { throw "安装注册路径不匹配：$registeredPath" }
        if (-not (Test-Path -LiteralPath (Join-Path $install 'docs/assets/DogSC.png') -PathType Leaf)) { throw '安装版缺少 README 引用的 DogSC 图标。' }
        $installedExe = Join-Path $install 'DogSC.exe'
        $installedSample = Join-Path $install 'samples/FrameTrail-Demo/FrameTrail-Demo.frametrail'
        $installedPlaybackSample = Copy-SampleProjectForPlayback $installedSample (Join-Path $validation "installed-playback-sample-$preferenceSnapshotId")
        $installedReport = Join-Path $validation 'installed-playback.json'
        Run-OwnedProcess $installedExe @('--open', $installedPlaybackSample, '--playback-smoke', $installedReport) $true
        Check-Report $installedReport 'installed-playback' 'per-user 安装版样例打开及有声播放'
        $uninstaller = Join-Path $install 'unins000.exe'
        Run-OwnedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $validation "uninstall-$preferenceSnapshotId.log")") $false 300
        if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $installedExe)) { throw '卸载后仍残留应用主程序或安装注册项。' }
        $report.Checks += [ordered]@{ Id='installed-uninstall'; Name='卸载清除本次安装程序与 HKCU 注册项'; Passed=$true }
    }
    }
    $report.Artifacts = @(foreach ($artifactName in @("DogSC-$Version-win-x64.zip", "DogSC-Setup-$Version-win-x64.exe",[string]$nativeManifest.archive.file,[string]$nativeManifest.sourceArchive.file)) {
        $artifact = Get-Item -LiteralPath (Join-Path $repo "artifacts/release/$artifactName")
        [ordered]@{ Name=$artifact.Name; Bytes=$artifact.Length; SHA256=(Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash }
    })
    $report.BuildInfo = Get-Content -LiteralPath (Join-Path $release 'BUILD-INFO.txt') -Raw
    $report.Passed = $true
} catch { $report.Error = $_.Exception.ToString(); throw }
finally {
    if ($preferenceSnapshotCreated) {
        try {
            $null = Restore-PreferenceSnapshot $preferenceSnapshotPath $preferenceSnapshotId ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
            $marker = Join-Path $validation "user-preferences-$preferenceSnapshotId.restored"
            if (-not [IO.File]::Exists($marker)) { Write-JsonCreateNew $marker ([ordered]@{ SnapshotId=$preferenceSnapshotId; RestoredUtc=[DateTime]::UtcNow.ToString('O') }) }
            $report.Checks += [ordered]@{ Id='preferences-restored'; Name='交互验收后恢复并逐字节核对四个固定用户偏好文件'; Passed=$true; SnapshotId=$preferenceSnapshotId }
        } catch {
            $report.Passed = $false
            $preferenceRestoreFailed = $true
            $report.PreferenceRestoreError = $_.Exception.ToString()
            $report.PreferenceRecoveryCommand = Get-PreferenceRecoveryCommand $preferenceSnapshotId
            $report.Checks += [ordered]@{ Id='preferences-restored'; Name='交互验收后恢复并逐字节核对四个固定用户偏好文件'; Passed=$false; SnapshotId=$preferenceSnapshotId; SnapshotRetained=$true }
        }
    }
    $portableArchiveUnchanged = $true
    if ($portableArchiveBaselineVerified) {
        $portableArchiveUnchanged = Record-PortableArchiveUnchanged (Join-Path $repo "artifacts/release/DogSC-$Version-win-x64.zip") $release
        if (-not $portableArchiveUnchanged) { $report.Passed = $false }
    }
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $resultName = if ($ContentOnly) { 'content-result.json' } else { 'result.json' }
    Write-ValidationReport (Join-Path $validation $resultName) $report $preferenceRestoreFailed
    if (-not $portableArchiveUnchanged) { throw '整轮包验收后发布目录与原始 ZIP 不一致；验证报告已记录失败。' }
}
