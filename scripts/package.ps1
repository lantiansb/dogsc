[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.18',
    [ValidatePattern('^[A-Za-z0-9_.-]+\.exe$')]
    [string]$ApplicationExeName = 'DogSC.exe',
    [switch]$WithoutFfmpeg,
    [switch]$SkipInstaller,
    [string]$NativeArchivePath,
    [switch]$OfflineNativeMedia
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeArguments = @{}
if ($NativeArchivePath) { $nativeArguments.ArchivePath = $NativeArchivePath }
if ($OfflineNativeMedia) { $nativeArguments.Offline = $true }
& (Join-Path $PSScriptRoot 'ensure-native-media.ps1') @nativeArguments
$nativeManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/ffmpeg/manifest.json') -Raw | ConvertFrom-Json
$project = Join-Path $repoRoot 'src/FrameTrail.App/FrameTrail.App.csproj'
$licenseRoot = Join-Path $repoRoot 'licenses'
$releaseRoot = Join-Path $repoRoot 'artifacts/release'
$publishRoot = Join-Path $releaseRoot "DogSC-$Version-win-x64"
$portableRoot = Join-Path $publishRoot 'app'
$publishStaging = Join-Path $env:TEMP "FrameTrail-publish-$Version-$PID-$([guid]::NewGuid().ToString('N'))"
$licenseOutput = Join-Path $publishRoot 'licenses'
$ffmpegOutput = Join-Path $portableRoot 'tools/ffmpeg'
$zipPath = Join-Path $releaseRoot "DogSC-$Version-win-x64.zip"
$installerPath = Join-Path $releaseRoot "DogSC-Setup-$Version-win-x64.exe"
$upstreamBaseSha = 'e207d11b111c5e58152238fb64115b2009850547'
$screenRecorderLibVendor = Join-Path $repoRoot 'third_party/ScreenRecorderLib'
$screenRecorderLibVendorDll = Join-Path $screenRecorderLibVendor 'prebuilt/ScreenRecorderLib.dll'
$screenRecorderLibVendorArchive = Join-Path $screenRecorderLibVendor 'ScreenRecorderLib-v7.0.1-source.zip'
$screenRecorderLibVendorPatch = Join-Path $screenRecorderLibVendor 'first-frame-wgc.patch'
$screenRecorderLibVendorLicense = Join-Path $screenRecorderLibVendor 'LICENSE'
$screenRecorderLibDllSha256 = '3D366DD95EEDDDE20BF66B6C0CC8B10E5F9BF7C9489E8D0B5AAEE0C838118151'
$screenRecorderLibPatchSha256 = '4FB260DCADD8FDE129A06D4380AE19ECA7A3EA985FEDE4D19E430DAA7C2C7C3F'
$screenRecorderLibSourceArchiveSha256 = '31435BB4F5D159D5D943AE5E31BD4DE6A9C5E8476AFA9691E7DC8E51A5FA27E0'
$screenRecorderLibLicenseSha256 = '9AAD4239954F3F4B1BAE22011CA72E00FE5AD9218068CD44375A1A5306F7CF32'
. (Join-Path $PSScriptRoot 'source-provenance.ps1')
$null = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $upstreamBaseSha

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "应用项目尚未集成：$project"
}
if (-not (Test-Path -LiteralPath $licenseRoot -PathType Container)) { throw "许可证目录缺失：$licenseRoot" }
foreach ($requiredFile in @($screenRecorderLibVendorDll, $screenRecorderLibVendorArchive, $screenRecorderLibVendorPatch, $screenRecorderLibVendorLicense)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "ScreenRecorderLib 固定材料缺失：$requiredFile" }
}
if ((Get-FileHash -LiteralPath $screenRecorderLibVendorDll -Algorithm SHA256).Hash.ToUpperInvariant() -ne $screenRecorderLibDllSha256) { throw 'ScreenRecorderLib 预构建 DLL 哈希不匹配。' }
if ((Get-FileHash -LiteralPath $screenRecorderLibVendorArchive -Algorithm SHA256).Hash.ToUpperInvariant() -ne $screenRecorderLibSourceArchiveSha256) { throw 'ScreenRecorderLib 上游源码 ZIP 哈希不匹配。' }
if ((Get-FileHash -LiteralPath $screenRecorderLibVendorPatch -Algorithm SHA256).Hash.ToUpperInvariant() -ne $screenRecorderLibPatchSha256) { throw 'ScreenRecorderLib WGC 补丁哈希不匹配。' }
if ((Get-FileHash -LiteralPath $screenRecorderLibVendorLicense -Algorithm SHA256).Hash.ToUpperInvariant() -ne $screenRecorderLibLicenseSha256) { throw 'ScreenRecorderLib MIT 许可证哈希不匹配。' }

$null = New-Item -ItemType Directory -Path $releaseRoot -Force
$releaseBoundary = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
if ((Get-Item -LiteralPath $releaseRoot -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Refusing to build inside a release directory reparse point: $releaseRoot" }
function Remove-GeneratedReleaseOutput([string]$Path) {
    $absolutePath = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolutePath.StartsWith($releaseBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除发布目录以外的路径：$absolutePath"
    }
    if (Test-Path -LiteralPath $absolutePath) {
        $item = Get-Item -LiteralPath $absolutePath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝删除链接或重解析点：$absolutePath"
        }
        Remove-Item -LiteralPath $absolutePath -Recurse -Force
    }
}
Remove-GeneratedReleaseOutput $publishRoot
Remove-GeneratedReleaseOutput $zipPath
Remove-GeneratedReleaseOutput $installerPath
$null = New-Item -ItemType Directory -Path $portableRoot -Force
if ($publishStaging.Contains("'")) { throw "系统临时目录含有撇号，无法规避 .NET SDK 发布路径限制：$publishStaging" }

try {
    $null = New-Item -ItemType Directory -Path $publishStaging -Force
    Push-Location $repoRoot
    try {
        & dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:Platform=x64 -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false `
            -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
            --output $publishStaging
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
    Copy-Item -Path (Join-Path $publishStaging '*') -Destination $portableRoot -Recurse -Force
}
finally {
    if (Test-Path -LiteralPath $publishStaging -PathType Container) {
        $stagingAbsolute = [System.IO.Path]::GetFullPath($publishStaging)
        $tempAbsolute = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        if (-not $stagingAbsolute.StartsWith($tempAbsolute, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理系统临时目录之外的发布暂存：$stagingAbsolute"
        }
        if ((Get-ChildItem -LiteralPath $stagingAbsolute -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "拒绝清理含有重解析点的发布暂存：$stagingAbsolute" }
        Remove-Item -LiteralPath $stagingAbsolute -Recurse -Force
    }
}

$publishedScreenRecorderLib = Join-Path $portableRoot 'ScreenRecorderLib.dll'
if (-not (Test-Path -LiteralPath $publishedScreenRecorderLib -PathType Leaf)) {
    throw '自包含发布目录缺少 ScreenRecorderLib.dll。'
}
$publishedScreenRecorderLibSha256 = (Get-FileHash -LiteralPath $publishedScreenRecorderLib -Algorithm SHA256).Hash.ToUpperInvariant()
if ($publishedScreenRecorderLibSha256 -ne $screenRecorderLibDllSha256) {
    throw "发布目录 ScreenRecorderLib.dll 哈希不匹配。期望 $screenRecorderLibDllSha256，实际 $publishedScreenRecorderLibSha256。"
}

$applicationExe = Join-Path $portableRoot $ApplicationExeName
if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf)) {
    throw "发布目录中未找到应用主程序 $ApplicationExeName。检查 App 项目的 AssemblyName 后再运行 package.ps1。"
}
Copy-Item -LiteralPath $licenseRoot -Destination $licenseOutput -Recurse -Force
$fetchArguments = @{ PackageDirectory=$publishRoot; Offline=[bool]$OfflineNativeMedia }
if ($NativeArchivePath) { $fetchArguments.ArchivePath = $NativeArchivePath }
& (Join-Path $PSScriptRoot 'fetch-libav-corresponding-source.ps1') @fetchArguments
foreach ($name in @('README.md', 'LICENSE')) {
    $sourceFile = Join-Path $repoRoot $name
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "发行材料缺失：$sourceFile" }
    Copy-Item -LiteralPath $sourceFile -Destination $publishRoot -Force
}
$docsSource = Join-Path $repoRoot 'docs'
if (-not (Test-Path -LiteralPath $docsSource -PathType Container)) { throw "文档目录缺失：$docsSource" }
Copy-Item -LiteralPath $docsSource -Destination $publishRoot -Recurse -Force

# Ship only the public native-media description and pinned manifest. The other
# third_party/ffmpeg files may contain local research or build inputs.
$publishedFfmpegInfo = Join-Path $publishRoot 'third_party/ffmpeg'
$null = New-Item -ItemType Directory -Path $publishedFfmpegInfo -Force
foreach ($name in @('README.md','manifest.json')) {
    $sourcePath = Join-Path $repoRoot "third_party/ffmpeg/$name"
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "FFmpeg public release information is missing: $sourcePath" }
    Copy-Item -LiteralPath $sourcePath -Destination $publishedFfmpegInfo -Force
}

$screenRecorderLibLicenseOutput = Join-Path $licenseOutput 'ScreenRecorderLib-source'
$null = New-Item -ItemType Directory -Path $screenRecorderLibLicenseOutput -Force
foreach ($sourceName in @('ScreenRecorderLib-v7.0.1-source.zip', 'first-frame-wgc.patch', 'README.md', 'provenance.json', 'LICENSE')) {
    $sourcePath = Join-Path $screenRecorderLibVendor $sourceName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "ScreenRecorderLib 对应材料缺失：$sourcePath" }
    Copy-Item -LiteralPath $sourcePath -Destination $screenRecorderLibLicenseOutput -Force
}

$sampleStaging = Join-Path $env:TEMP ("FrameTrail-sample-$Version-$PID-" + [guid]::NewGuid().ToString('N'))
$sampleNames = @('FrameTrail-Demo.frametrail', 'FrameTrail-Demo.mp4', 'synthetic-screen.mp4', 'synthetic-camera.mp4', 'independent-tone.wav', 'demo-badge.png', 'preview.png', 'README.md')
try {
    if (Test-Path -LiteralPath $sampleStaging) { throw "样例暂存目录已存在：$sampleStaging" }
    $previousFfmpegDirectory = $env:FRAMETRAIL_FFMPEG_DIR
    try {
        $env:FRAMETRAIL_FFMPEG_DIR = Join-Path $repoRoot 'artifacts/toolchains/dogsc-native-8.1.2/cli'
        & (Join-Path $PSScriptRoot 'create-sample.ps1') -OutputDirectory $sampleStaging
        if ($LASTEXITCODE -ne 0) { throw '原创样例生成失败。' }
    } finally { $env:FRAMETRAIL_FFMPEG_DIR = $previousFfmpegDirectory }
    $sampleDestination = Join-Path $publishRoot 'samples/FrameTrail-Demo'
    $null = New-Item -ItemType Directory -Path $sampleDestination -Force
    foreach ($name in $sampleNames) {
        $sampleFile = Join-Path $sampleStaging $name
        if (-not (Test-Path -LiteralPath $sampleFile -PathType Leaf)) { throw "生成的演示缺少文件：$name" }
        Copy-Item -LiteralPath $sampleFile -Destination $sampleDestination
    }
}
finally {
    $sampleAbsolute = [IO.Path]::GetFullPath($sampleStaging)
    $tempBoundary = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (-not $sampleAbsolute.StartsWith($tempBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw "拒绝清理TEMP之外的样例暂存：$sampleAbsolute" }
    if (Test-Path -LiteralPath $sampleAbsolute) {
        if (((Get-Item -LiteralPath $sampleAbsolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '拒绝清理样例重解析点。' }
        Remove-Item -LiteralPath $sampleAbsolute -Recurse -Force
    }
}

$sourceProvenance = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $upstreamBaseSha
$gitHead = $sourceProvenance.Head
$sourceKind = $sourceProvenance.Kind
$workingTreeState = $sourceProvenance.WorktreeState

$nativeCache = Join-Path $repoRoot 'artifacts/toolchains/dogsc-native-8.1.2'
$nativeArchiveSource = if ($NativeArchivePath) { [IO.Path]::GetFullPath($NativeArchivePath) } else { Join-Path $nativeCache ([string]$nativeManifest.archive.file) }
$nativeArchiveDestination = Join-Path $releaseRoot ([string]$nativeManifest.archive.file)
$nativeArchiveHash = ([string]$nativeManifest.archive.sha256).ToLowerInvariant()
if ((Get-FileHash -LiteralPath $nativeArchiveSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nativeArchiveHash) { throw 'Native media archive no longer matches its manifest.' }
if (Test-Path -LiteralPath $nativeArchiveDestination) {
    if ((Get-FileHash -LiteralPath $nativeArchiveDestination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nativeArchiveHash) { throw "Existing native archive asset differs; refusing to overwrite: $nativeArchiveDestination" }
} else { Copy-Item -LiteralPath $nativeArchiveSource -Destination $nativeArchiveDestination }
$sourceArchiveName = [string]$nativeManifest.sourceArchive.file
$sourceArchiveHash = ([string]$nativeManifest.sourceArchive.sha256).ToLowerInvariant()
$sourceArchiveMatches = @($nativeManifest.sourceFiles | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ceq $sourceArchiveName -and ([string]$_.sha256).ToLowerInvariant() -eq $sourceArchiveHash })
if ($sourceArchiveMatches.Count -ne 1) { throw 'Native manifest sourceFiles must contain the separate source archive with its declared hash.' }
$sourceArchiveSource = Join-Path $nativeCache ([string]$sourceArchiveMatches[0].path)
$sourceArchiveDestination = Join-Path $releaseRoot $sourceArchiveName
if ((Get-FileHash -LiteralPath $sourceArchiveSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceArchiveHash) { throw 'Corresponding source archive no longer matches its manifest.' }
if (Test-Path -LiteralPath $sourceArchiveDestination) {
    if ((Get-FileHash -LiteralPath $sourceArchiveDestination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceArchiveHash) { throw "Existing corresponding source archive differs; refusing to overwrite: $sourceArchiveDestination" }
} else { Copy-Item -LiteralPath $sourceArchiveSource -Destination $sourceArchiveDestination }
if (-not $WithoutFfmpeg) {
    $null = New-Item -ItemType Directory -Path $ffmpegOutput -Force
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        $source = Join-Path $nativeCache "cli/$name"
        $expected = @($nativeManifest.runtimeFiles | Where-Object { ([string]$_.path).Replace('\','/') -ceq "cli/$name" })
        if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$expected[0].sha256).ToLowerInvariant()) { throw "Native CLI file failed manifest hash verification: $name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $ffmpegOutput $name) -Force
    }
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        $target = Join-Path $ffmpegOutput $name
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string](@($nativeManifest.runtimeFiles | Where-Object { ([string]$_.path).Replace('\','/') -ceq "cli/$name" })[0].sha256)).ToLowerInvariant()) { throw "Packaged CLI file failed manifest hash verification: $name" }
    }
}
else { Remove-GeneratedReleaseOutput $ffmpegOutput }

$vcRuntimeNotice = @'
DogSC 录制功能通过 ScreenRecorderLib 依赖 Microsoft Visual C++ 2015-2022 Redistributable (x64)。
本安装程序不会安装或更新系统运行库。如果录制无法启动，请从微软官方页面手动安装 x64 版本：
https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
'@
$vcRuntimeNotice | Set-Content -LiteralPath (Join-Path $publishRoot 'VC-RUNTIME-REQUIREMENT.txt') -Encoding utf8

$buildInfo = @"
产品：DogSC Windows 社区移植
上游：https://github.com/laogou717/dogsc
版本：$Version
目标：win-x64 自包含便携发布目录
配置：$Configuration
打包用途：Windows 社区发行候选
源码类型：$sourceKind
包含 FFmpeg：$(-not $WithoutFfmpeg)
FrameTrail 源码提交：$gitHead
DogSC 上游审计基线：$upstreamBaseSha
打包时工作区：$workingTreeState
ScreenRecorderLib：7.0.1，本地 WGC 首帧补丁构建
ScreenRecorderLib DLL SHA-256：$publishedScreenRecorderLibSha256
ScreenRecorderLib 上游源码 ZIP SHA-256：$screenRecorderLibSourceArchiveSha256
ScreenRecorderLib 补丁 SHA-256：$screenRecorderLibPatchSha256
FFmpeg：$($nativeManifest.version)
FFmpeg 原生归档 SHA-256：$($nativeManifest.archive.sha256)
FFmpeg 对应源码归档：$($nativeManifest.sourceArchive.file)
FFmpeg 对应源码归档 SHA-256：$($nativeManifest.sourceArchive.sha256)
"@
$buildInfo | Set-Content -LiteralPath (Join-Path $publishRoot 'BUILD-INFO.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $compiler = & (Join-Path $PSScriptRoot 'ensure-inno-setup.ps1')
    if ($LASTEXITCODE -ne 0 -or -not $compiler) { throw '无法取得 Inno Setup 编译器。' }
    $iss = Join-Path $repoRoot 'installer/FrameTrail.iss'
    if (-not (Test-Path -LiteralPath $iss -PathType Leaf)) { throw "安装器脚本不存在：$iss" }
    & $compiler $iss "/DAppVersion=$Version" "/DAppExeName=$ApplicationExeName" "/DReleaseDir=$publishRoot" `
        "/O$releaseRoot" "/FDogSC-Setup-$Version-win-x64"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw 'Inno Setup 安装包编译失败。'
    }
}

Write-Host "便携目录：$publishRoot"
Write-Host "ZIP：$zipPath"
if (Test-Path -LiteralPath $installerPath) { Write-Host "安装包：$installerPath" }
