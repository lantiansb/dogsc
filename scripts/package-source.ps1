[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.18',
    [string]$UpstreamBaseSha = 'e207d11b111c5e58152238fb64115b2009850547'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$upstreamRoot = Join-Path $repoRoot 'upstream/dogsc'
$releaseRoot = Join-Path $repoRoot 'artifacts/release'
$output = Join-Path $releaseRoot "DogSC-$Version-source.zip"
. (Join-Path $PSScriptRoot 'source-provenance.ps1')
$provenance = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $UpstreamBaseSha -RequireGitCheckout
$head = $provenance.Head
$worktreeState = $provenance.WorktreeState
if ($worktreeState -ne 'clean') { throw '源码包只归档已提交代码；请先提交当前变更，避免遗漏未提交实现。' }

$null = New-Item -ItemType Directory -Path $releaseRoot -Force
$tempRoot = Join-Path $env:TEMP "FrameTrail-source-$PID-$([guid]::NewGuid().ToString('N'))"
if ($tempRoot.Contains("'")) { throw "系统临时目录含有撇号：$tempRoot" }
$stage = Join-Path $tempRoot 'contents'
$ownArchive = Join-Path $tempRoot 'FrameTrail-HEAD.zip'
$upstreamArchive = Join-Path $tempRoot 'DogSC-upstream.zip'
try {
    $null = New-Item -ItemType Directory -Path $stage -Force
    & git -C $repoRoot archive --format=zip --prefix='FrameTrail/' HEAD "-o$ownArchive"
    if ($LASTEXITCODE -ne 0) { throw 'git archive HEAD 失败。' }
    & git -C $upstreamRoot archive --format=zip --prefix='FrameTrail/upstream/dogsc/' $UpstreamBaseSha "-o$upstreamArchive"
    if ($LASTEXITCODE -ne 0) { throw '上游 git archive 固定基线失败。' }
    Expand-Archive -LiteralPath $ownArchive -DestinationPath $stage -Force
    Expand-Archive -LiteralPath $upstreamArchive -DestinationPath $stage -Force
    $metadata = [ordered]@{
        formatVersion = 1
        frameTrailCommit = $head
        upstreamDogScCommit = $UpstreamBaseSha
        worktreeStateAtArchive = $worktreeState
        archiveContents = 'git archive HEAD 与 upstream/dogsc 固定基线；不包含未提交修改或构建/验证产物。'
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'FrameTrail/SOURCE-METADATA.json') -Encoding utf8
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $output, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "源码包：$output"
    Write-Host "FrameTrail HEAD：$head"
    Write-Host "DogSC 上游基线：$UpstreamBaseSha"
    Write-Host "打包时工作区：$worktreeState"
}
finally {
    $tempAbsolute = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $tempAbsolutePath = [System.IO.Path]::GetFullPath($tempRoot)
    if (-not $tempAbsolutePath.StartsWith($tempAbsolute, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理系统临时目录之外的源码暂存：$tempAbsolutePath"
    }
    if (Test-Path -LiteralPath $tempAbsolutePath) { Remove-Item -LiteralPath $tempAbsolutePath -Recurse -Force }
}
