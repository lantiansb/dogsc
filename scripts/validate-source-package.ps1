[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceZip,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.18',
    [ValidateRange(60, 7200)]
    [int]$TestTimeoutSeconds = 3600,
    [ValidateRange(60, 7200)]
    [int]$BuildTimeoutSeconds = 1800,
    [ValidateRange(60, 7200)]
    [int]$PackageTimeoutSeconds = 3600,
    [string]$NativeArchivePath
)

$ErrorActionPreference = 'Stop'
$null = . (Join-Path $PSScriptRoot 'source-zip-policy.ps1')
$workspaceRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceZipPath = [System.IO.Path]::GetFullPath($SourceZip)
$resultDirectory = Join-Path $workspaceRoot "artifacts/validation/package-$Version"
$resultPath = Join-Path $resultDirectory 'source-validation.json'
$upstreamCommit = 'e207d11b111c5e58152238fb64115b2009850547'
$runId = [guid]::NewGuid().ToString('N')
$tempBoundary = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
$tempRoot = Join-Path $env:TEMP "FrameTrail-source-package-validation-$runId"
$logsDirectory = Join-Path $resultDirectory "source-validation-logs-$runId"
$run = [ordered]@{
    schemaVersion = 1
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('o')
    completedUtc = $null
    status = 'running'
    sourceZip = $sourceZipPath
    sourceZipSha256 = $null
    validationTempDirectory = $tempRoot
    extractedDirectory = Join-Path $tempRoot 'extract/FrameTrail'
    workspaceRoot = $workspaceRoot
    logsDirectory = $logsDirectory
    sourceMetadata = $null
    sourceArchiveLayout = $null
    noGitMetadataVerified = $false
    screenRecorderLib = $null
    nativeMedia = $null
    steps = @()
    buildInfo = $null
    error = $null
}

function Assert-TempPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($tempBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a temporary path outside TEMP: $fullPath"
    }
    $workspaceBoundary = $workspaceRoot.TrimEnd('\') + '\'
    if ($fullPath.StartsWith($workspaceBoundary, [System.StringComparison]::OrdinalIgnoreCase) -or
        $workspaceRoot.StartsWith($fullPath.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Temporary validation directory must be outside the workspace and its Git ancestor: $fullPath"
    }
}

function Invoke-LoggedProcess(
    [string]$Name,
    [string]$FileName,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [int]$TimeoutSeconds
) {
    $stdoutPath = Join-Path $logsDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $logsDirectory "$Name.stderr.log"
    $record = [ordered]@{
        name = $Name
        executable = $FileName
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        timeoutSeconds = $TimeoutSeconds
        startedUtc = [DateTime]::UtcNow.ToString('o')
        completedUtc = $null
        exitCode = $null
        timedOut = $false
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
        error = $null
    }
    $process = $null
    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $FileName
        $startInfo.WorkingDirectory = $WorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $startInfo.Environment['FRAMETRAIL_FFMPEG_DIR'] = Join-Path $repoRoot 'artifacts/toolchains/dogsc-native-8.1.2/cli'
        foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add($argument) }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Failed to start process: $FileName" }

        # Read both redirected streams immediately so neither child pipe can block the process.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $record.timedOut = $true
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText($stdoutPath, $stdout, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($stderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
        if (-not $record.timedOut) { $record.exitCode = $process.ExitCode }
    }
    catch {
        $record.error = $_.Exception.Message
        [System.IO.File]::WriteAllText($stderrPath, $record.error, [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        if ($process) { $process.Dispose() }
        $record.completedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $run.steps += [pscustomobject]$record
    return [pscustomobject]$record
}

function Get-NormalizedSourceSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    $text = $text.Replace("`r`n", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($bytes)) }
    finally { $sha.Dispose() }
}

function Assert-FileHash([string]$Path, [string]$Expected, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $Expected.ToUpperInvariant()) { throw "$Label SHA-256 mismatch. Expected $Expected, actual $actual." }
}

function Assert-NoReparsePathWithin([string]$Path, [string]$Boundary) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $boundaryFull = [System.IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    $boundaryPrefix = $boundaryFull + '\'
    if (-not $fullPath.StartsWith($boundaryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.Equals($boundaryFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected boundary: $fullPath"
    }

    $cursor = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a reparse point in validation path: $cursor"
            }
        }
        if ($cursor.Equals($boundaryFull, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent.Equals($cursor, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Could not walk validation path to its boundary: $fullPath"
        }
        $cursor = $parent
    }
}

try {
    $null = New-Item -ItemType Directory -Path $resultDirectory -Force
    $null = New-Item -ItemType Directory -Path $logsDirectory -Force
    Assert-TempPath $tempRoot
    if (-not (Test-Path -LiteralPath $sourceZipPath -PathType Leaf)) { throw "Source ZIP does not exist: $sourceZipPath" }
    $run.sourceZipSha256 = (Get-FileHash -LiteralPath $sourceZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $tempRoot) { throw "UUID temporary directory already exists: $tempRoot" }
    $null = New-Item -ItemType Directory -Path $tempRoot
    $archiveRoot = Join-Path $tempRoot 'extract'
    $null = New-Item -ItemType Directory -Path $archiveRoot

    $run.sourceArchiveLayout = Assert-FrameTrailSourceZipLayout -SourceZipPath $sourceZipPath -ExtractionRoot $archiveRoot
    Expand-Archive -LiteralPath $sourceZipPath -DestinationPath $archiveRoot

    $gitMetadata = @(Get-ChildItem -LiteralPath $archiveRoot -Force -Recurse -ErrorAction Stop |
        Where-Object { $_.Name -eq '.git' })
    if ($gitMetadata.Count -gt 0) { throw "Source archive contains Git metadata: $($gitMetadata[0].FullName)" }
    $run.noGitMetadataVerified = $true

    $repoRoot = Join-Path $archiveRoot 'FrameTrail'
    $metadataPath = Join-Path $repoRoot 'SOURCE-METADATA.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw "Missing SOURCE-METADATA.json: $metadataPath" }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.formatVersion -ne 1 -or
        [string]$metadata.frameTrailCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$metadata.upstreamDogScCommit -ne $upstreamCommit -or
        [string]$metadata.worktreeStateAtArchive -ne 'clean') {
        throw 'SOURCE-METADATA.json has an unsupported format, commit, upstream base, or non-clean archive state.'
    }
    $run.sourceMetadata = $metadata

    $screenRecorderLibRoot = Join-Path $repoRoot 'third_party/ScreenRecorderLib'
    $screenRecorderLibProvenancePath = Join-Path $screenRecorderLibRoot 'provenance.json'
    if (-not (Test-Path -LiteralPath $screenRecorderLibProvenancePath -PathType Leaf)) {
        throw 'Missing third_party/ScreenRecorderLib/provenance.json.'
    }
    $screenRecorderLibProvenance = Get-Content -LiteralPath $screenRecorderLibProvenancePath -Raw | ConvertFrom-Json
    if ($screenRecorderLibProvenance.schemaVersion -ne 1 -or
        $screenRecorderLibProvenance.version -ne '7.0.1' -or
        $screenRecorderLibProvenance.source.commit -ne '646383234187efd3854e6b1069612b80c3ab9981' -or
        $screenRecorderLibProvenance.patch.sourceSha256AfterNormalizedLfUtf8 -ne '643F7AEE810E5C57467DF3620808F72576EBDB05DB466D0F77F9218E3669D61D' -or
        $screenRecorderLibProvenance.prebuilt.sha256 -ne '3D366DD95EEDDDE20BF66B6C0CC8B10E5F9BF7C9489E8D0B5AAEE0C838118151') {
        throw 'ScreenRecorderLib provenance does not match the audited version, source, patch, and tested binary.'
    }
    $screenRecorderLibArchive = Join-Path $screenRecorderLibRoot ([string]$screenRecorderLibProvenance.source.archive)
    $screenRecorderLibPatch = Join-Path $screenRecorderLibRoot ([string]$screenRecorderLibProvenance.patch.file)
    $screenRecorderLibLicense = Join-Path $screenRecorderLibRoot ([string]$screenRecorderLibProvenance.source.licenseFile)
    $screenRecorderLibDll = Join-Path $screenRecorderLibRoot ([string]$screenRecorderLibProvenance.prebuilt.file)
    Assert-FileHash $screenRecorderLibArchive ([string]$screenRecorderLibProvenance.source.archiveSha256) 'ScreenRecorderLib source archive'
    Assert-FileHash $screenRecorderLibPatch ([string]$screenRecorderLibProvenance.patch.sha256) 'ScreenRecorderLib patch'
    Assert-FileHash $screenRecorderLibLicense ([string]$screenRecorderLibProvenance.source.licenseSha256) 'ScreenRecorderLib upstream license'
    Assert-FileHash $screenRecorderLibDll ([string]$screenRecorderLibProvenance.prebuilt.sha256) 'ScreenRecorderLib tested DLL'

    $captureProject = Join-Path $repoRoot 'src/FrameTrail.Capture/FrameTrail.Capture.csproj'
    $captureProjectText = Get-Content -LiteralPath $captureProject -Raw
    if ($captureProjectText -notmatch 'ScreenRecorderLibVendorPath' -or
        $captureProjectText -notmatch 'VerifyScreenRecorderLibVendorBinary' -or
        $captureProjectText -match '<PackageReference\s+Include="ScreenRecorderLib"') {
        throw 'Capture project is not configured to build against the pinned local ScreenRecorderLib binary.'
    }
    $lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests') -Recurse -File -Filter 'packages.lock.json')
    foreach ($lockFile in $lockFiles) {
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            if ($framework.Value.PSObject.Properties.Name -contains 'ScreenRecorderLib') {
                throw "NuGet lock still lists ScreenRecorderLib as a package: $($lockFile.FullName) [$($framework.Name)]"
            }
        }
    }

    $screenRecorderLibPatchApplyVerified = $false
    $screenRecorderLibSourceCheck = Join-Path $tempRoot 'screenrecorderlib-source-check'
    New-Item -ItemType Directory -Path $screenRecorderLibSourceCheck | Out-Null
    Expand-Archive -LiteralPath $screenRecorderLibArchive -DestinationPath $screenRecorderLibSourceCheck
    $screenRecorderLibExtractedRoot = Join-Path $screenRecorderLibSourceCheck 'ScreenRecorderLib-7.0.1'
    $screenRecorderLibSourceFile = Join-Path $screenRecorderLibExtractedRoot ([string]$screenRecorderLibProvenance.patch.filePath)
    Assert-FileHash $screenRecorderLibSourceFile ([string]$screenRecorderLibProvenance.patch.sourceSha256Before) 'ScreenRecorderLib upstream WGC source'
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitCommand) { throw 'Git is required to verify that the ScreenRecorderLib source patch applies.' }
    & $gitCommand.Source -C $screenRecorderLibExtractedRoot apply --check $screenRecorderLibPatch
    if ($LASTEXITCODE -ne 0) { throw 'ScreenRecorderLib patch does not apply cleanly to the pinned source archive.' }
    & $gitCommand.Source -C $screenRecorderLibExtractedRoot apply $screenRecorderLibPatch
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply the ScreenRecorderLib patch to the pinned source archive.' }
    $appliedSourceHash = Get-NormalizedSourceSha256 $screenRecorderLibSourceFile
    if ($appliedSourceHash -ne [string]$screenRecorderLibProvenance.patch.sourceSha256AfterNormalizedLfUtf8) {
        throw "ScreenRecorderLib patched source SHA-256 mismatch: $appliedSourceHash"
    }
    $screenRecorderLibPatchApplyVerified = $true
    $run.screenRecorderLib = [ordered]@{
        version = [string]$screenRecorderLibProvenance.version
        sourceCommit = [string]$screenRecorderLibProvenance.source.commit
        sourceArchiveSha256 = (Get-FileHash -LiteralPath $screenRecorderLibArchive -Algorithm SHA256).Hash.ToUpperInvariant()
        patchSha256 = (Get-FileHash -LiteralPath $screenRecorderLibPatch -Algorithm SHA256).Hash.ToUpperInvariant()
        testedDllSha256 = (Get-FileHash -LiteralPath $screenRecorderLibDll -Algorithm SHA256).Hash.ToUpperInvariant()
        patchAppliesToPinnedSource = $screenRecorderLibPatchApplyVerified
        captureProjectUsesPinnedLocalBinary = $true
        publishedDllPath = $null
        publishedDllSha256 = $null
        publishedDllMatches = $false
        publishedMaterialsVerified = $false
    }

    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { throw "PowerShell executable not found: $pwshPath" }
    $utf8Bootstrap = Join-Path $PSScriptRoot 'invoke-utf8-script.ps1'
    $nativeManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/ffmpeg/manifest.json') -Raw | ConvertFrom-Json
    if ([string]$nativeManifest.archive.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Extracted source has no finalized native archive SHA-256.' }
    if (-not $NativeArchivePath) { $NativeArchivePath = Join-Path $workspaceRoot "artifacts/toolchains/dogsc-native-8.1.2/$($nativeManifest.archive.file)" }
    $nativeParameters = @{}
    $nativeArguments = @('-NoProfile', '-NonInteractive', '-File', $utf8Bootstrap, '-ScriptPath', (Join-Path $repoRoot 'scripts/ensure-native-media.ps1'))
    if (Test-Path -LiteralPath $NativeArchivePath -PathType Leaf) {
        Assert-FileHash $NativeArchivePath ([string]$nativeManifest.archive.sha256) 'Manifest-pinned native media archive'
        $nativeParameters.ArchivePath = $NativeArchivePath
    }
    if ($nativeParameters.Count -gt 0) { $nativeArguments += @('-ParametersJson', ($nativeParameters | ConvertTo-Json -Compress)) }
    $nativePrepareResult = Invoke-LoggedProcess -Name 'native-media-prepare' -FileName $pwshPath `
        -Arguments $nativeArguments `
        -WorkingDirectory $repoRoot -TimeoutSeconds $BuildTimeoutSeconds
    $testScript = Join-Path $repoRoot 'scripts/test.ps1'
    $testParameters = @{ Configuration='Release'; IncludeCaptureProbe=$true; IncludeVisualProbe=$true; IncludeReleaseProbe=$true } | ConvertTo-Json -Compress
    $testResult = Invoke-LoggedProcess -Name 'tests' -FileName $pwshPath `
        -Arguments @('-NoProfile', '-NonInteractive', '-File', $utf8Bootstrap, '-ScriptPath', $testScript, '-ParametersJson', $testParameters) `
        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds

    $appProject = Join-Path $repoRoot 'src/FrameTrail.App/FrameTrail.App.csproj'
    $buildResult = Invoke-LoggedProcess -Name 'app-build' -FileName 'dotnet' `
        -Arguments @('build', $appProject, '--configuration', 'Release') `
        -WorkingDirectory $repoRoot -TimeoutSeconds $BuildTimeoutSeconds

    $packageScript = Join-Path $repoRoot 'scripts/package.ps1'
    $nativeSeekResult = Invoke-LoggedProcess -Name 'native-seek' -FileName 'dotnet' `
        -Arguments @('run', '--project', (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj'), '--configuration', 'Release', '--', '--native-seek-decoder', (Join-Path $repoRoot 'artifacts/validation/native-seek')) `
        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds
    $nativePlaybackResult = Invoke-LoggedProcess -Name 'native-playback' -FileName 'dotnet' `
        -Arguments @('run', '--no-build', '--project', (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj'), '--configuration', 'Release', '--', '--native-playback', (Join-Path $repoRoot 'artifacts/validation/native-playback')) `
        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds
    $packageParametersObject = @{ Configuration='Release'; Version=$Version; WithoutFfmpeg=$true; SkipInstaller=$true }
    $innerNativeArchive = Join-Path $repoRoot "artifacts/toolchains/dogsc-native-8.1.2/$($nativeManifest.archive.file)"
    if (Test-Path -LiteralPath $innerNativeArchive -PathType Leaf) { $packageParametersObject.NativeArchivePath=$innerNativeArchive; $packageParametersObject.OfflineNativeMedia=$true }
    $packageParameters = $packageParametersObject | ConvertTo-Json -Compress
    $packageResult = Invoke-LoggedProcess -Name 'package-without-ffmpeg' -FileName $pwshPath `
        -Arguments @('-NoProfile', '-NonInteractive', '-File', $utf8Bootstrap, '-ScriptPath', $packageScript, '-ParametersJson', $packageParameters) `
        -WorkingDirectory $repoRoot -TimeoutSeconds $PackageTimeoutSeconds

    $nativePackageRoot = Join-Path $repoRoot "artifacts/release/DogSC-$Version-win-x64"
    $nativeArchiveAsset = Join-Path $repoRoot "artifacts/release/$($nativeManifest.archive.file)"
    $sourceArchiveAsset = Join-Path $repoRoot "artifacts/release/$($nativeManifest.sourceArchive.file)"
    $nativeMediaChecks = [ordered]@{
        version = [string]$nativeManifest.version
        archiveSha256 = ([string]$nativeManifest.archive.sha256).ToLowerInvariant()
        sourceArchiveSha256 = ([string]$nativeManifest.sourceArchive.sha256).ToLowerInvariant()
        nativeArchiveAssetMatches = (Test-Path -LiteralPath $nativeArchiveAsset -PathType Leaf) -and (Get-FileHash -LiteralPath $nativeArchiveAsset -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash.ToLowerInvariant() -eq ([string]$nativeManifest.archive.sha256).ToLowerInvariant()
        sourceArchiveAssetMatches = (Test-Path -LiteralPath $sourceArchiveAsset -PathType Leaf) -and (Get-FileHash -LiteralPath $sourceArchiveAsset -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash.ToLowerInvariant() -eq ([string]$nativeManifest.sourceArchive.sha256).ToLowerInvariant()
        packageRuntimeFilesMatch = $true
        packageSupportFilesMatch = $true
        packageSourceFilesMatch = $true
    }
    foreach ($entry in $nativeManifest.runtimeFiles | Where-Object { ([string]$_.path).Replace('\','/').StartsWith('libav/',[StringComparison]::Ordinal) }) {
        $target = Join-Path $nativePackageRoot "app/tools/libav/$([IO.Path]::GetFileName([string]$entry.path))"
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { $nativeMediaChecks.packageRuntimeFilesMatch=$false }
    }
    foreach ($entry in $nativeManifest.supportFiles) {
        $target = Join-Path $nativePackageRoot "app/tools/libav/$([IO.Path]::GetFileName([string]$entry.path))"
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { $nativeMediaChecks.packageSupportFilesMatch=$false }
    }
    $nativeSourceRoot = Join-Path $nativePackageRoot 'licenses/libav-corresponding-source'
    foreach ($entry in $nativeManifest.sourceFiles) {
        $relative = ([string]$entry.path).Replace('\','/') -replace '^source-materials/',''
        $target = Join-Path $nativeSourceRoot $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { $nativeMediaChecks.packageSourceFilesMatch=$false }
    }
    $nativeMediaChecks.passed = @($nativeMediaChecks.GetEnumerator() | Where-Object { $_.Key -match 'Matches$' -and -not $_.Value }).Count -eq 0
    $nativeMediaChecks.passed = $nativeMediaChecks.passed -and $nativeMediaChecks.packageRuntimeFilesMatch -and $nativeMediaChecks.packageSupportFilesMatch -and $nativeMediaChecks.packageSourceFilesMatch
    $run.nativeMedia = $nativeMediaChecks

    $publishedScreenRecorderLib = Join-Path $repoRoot "artifacts/release/DogSC-$Version-win-x64/app/ScreenRecorderLib.dll"
    if (Test-Path -LiteralPath $publishedScreenRecorderLib -PathType Leaf) {
        $publishedScreenRecorderLibHash = (Get-FileHash -LiteralPath $publishedScreenRecorderLib -Algorithm SHA256).Hash.ToUpperInvariant()
        $run.screenRecorderLib.publishedDllPath = $publishedScreenRecorderLib
        $run.screenRecorderLib.publishedDllSha256 = $publishedScreenRecorderLibHash
        $run.screenRecorderLib.publishedDllMatches = $publishedScreenRecorderLibHash -eq $run.screenRecorderLib.testedDllSha256
        $publishedMaterials = Join-Path $repoRoot "artifacts/release/DogSC-$Version-win-x64/licenses/ScreenRecorderLib-source"
        $publishedNames = @('ScreenRecorderLib-v7.0.1-source.zip', 'first-frame-wgc.patch', 'README.md', 'provenance.json', 'LICENSE')
        $publishedMaterialsPassed = $true
        foreach ($name in $publishedNames) {
            $candidate = Join-Path $publishedMaterials $name
            if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) { $publishedMaterialsPassed = $false; break }
            $original = Join-Path $screenRecorderLibRoot $name
            if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) {
                $publishedMaterialsPassed = $false
                break
            }
        }
        $run.screenRecorderLib.publishedMaterialsVerified = $publishedMaterialsPassed
    }

    $buildInfoPath = Join-Path $repoRoot "artifacts/release/DogSC-$Version-win-x64/BUILD-INFO.txt"
    if (Test-Path -LiteralPath $buildInfoPath -PathType Leaf) {
        $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw
        $sourceKindMatch = [regex]::Match($buildInfo, '(?m)^源码类型：(.+)\r?$')
        $commitMatch = [regex]::Match($buildInfo, '(?m)^FrameTrail 源码提交：([0-9a-f]{40})\r?$')
        $upstreamMatch = [regex]::Match($buildInfo, '(?m)^DogSC 上游审计基线：([0-9a-f]{40})\r?$')
        $run.buildInfo = [ordered]@{
            path = $buildInfoPath
            sourceKind = if ($sourceKindMatch.Success) { $sourceKindMatch.Groups[1].Value.Trim() } else { $null }
            frameTrailCommit = if ($commitMatch.Success) { $commitMatch.Groups[1].Value } else { $null }
            upstreamDogScCommit = if ($upstreamMatch.Success) { $upstreamMatch.Groups[1].Value } else { $null }
            sourceKindCorrect = $sourceKindMatch.Success -and $sourceKindMatch.Groups[1].Value.Trim() -eq 'source archive'
            frameTrailCommitCorrect = $commitMatch.Success -and $commitMatch.Groups[1].Value -eq [string]$metadata.frameTrailCommit
            upstreamCommitCorrect = $upstreamMatch.Success -and $upstreamMatch.Groups[1].Value -eq $upstreamCommit
        }
    }
    else {
        $run.buildInfo = [ordered]@{ path = $buildInfoPath; error = 'BUILD-INFO.txt was not produced.' }
    }

    $stepResults = @($nativePrepareResult, $testResult, $buildResult, $nativeSeekResult, $nativePlaybackResult, $packageResult)
    $stepsPassed = $stepResults.Count -eq 6 -and @($stepResults | Where-Object { $_.timedOut -or $_.exitCode -ne 0 -or $_.error }).Count -eq 0
    $buildInfoPassed = $run.buildInfo.sourceKindCorrect -and $run.buildInfo.frameTrailCommitCorrect -and $run.buildInfo.upstreamCommitCorrect
    $screenRecorderLibPassed = $screenRecorderLibPatchApplyVerified -and $run.screenRecorderLib.publishedDllMatches -and $run.screenRecorderLib.publishedMaterialsVerified
    $run.screenRecorderLib.passed = $screenRecorderLibPassed
    $run.status = if ($stepsPassed -and $buildInfoPassed -and $screenRecorderLibPassed -and $nativeMediaChecks.passed) { 'passed' } else { 'failed' }
}
catch {
    $run.status = 'failed'
    $run.error = $_.Exception.Message
}
finally {
    $run.completedUtc = [DateTime]::UtcNow.ToString('o')
    $json = $run | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($resultPath, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $logsDirectory 'result.json'), $json, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Source package validation: $($run.status)"
Write-Host "Result: $resultPath"
Write-Host "Extracted source retained at: $tempRoot"
Write-Host "Logs retained at: $logsDirectory"
if ($run.status -ne 'passed') { exit 1 }
exit 0
