[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeCaptureProbe,
    [switch]$IncludeMediaProbe,
    [switch]$IncludeVisualProbe,
    [switch]$IncludeReleaseProbe,
    [switch]$IncludeIntegration
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$coreTests = Join-Path $repoRoot 'tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj'
$captureProbe = Join-Path $repoRoot 'tests/FrameTrail.Capture.Probe/FrameTrail.Capture.Probe.csproj'
if ($IncludeMediaProbe -or $IncludeVisualProbe) { & (Join-Path $PSScriptRoot 'ensure-libav.ps1') }

if (-not (Test-Path -LiteralPath $coreTests -PathType Leaf)) {
    throw "测试项目不存在：$coreTests"
}

Push-Location $repoRoot
try {
    & dotnet run --project $coreTests --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Core 测试程序失败，退出码 $LASTEXITCODE" }
    & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.DogScImport.Tests/FrameTrail.DogScImport.Tests.csproj') --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "DogSC 只读导入测试失败，退出码 $LASTEXITCODE" }
    if ($IncludeMediaProbe) {
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "共享GPU/媒体测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --timeline-assets
        if ($LASTEXITCODE -ne 0) { throw "时间线缩略图与波形测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --scrub-decoder (Join-Path $repoRoot ('artifacts/validation/scrub-decoder-' + [guid]::NewGuid().ToString('N')))
        if ($LASTEXITCODE -ne 0) { throw "时间线持续解码与帧缓存检查失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --native-seek-decoder (Join-Path $repoRoot ('artifacts/validation/native-seek-' + [guid]::NewGuid().ToString('N')))
        if ($LASTEXITCODE -ne 0) { throw "常驻原生解码与时间戳检查失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --native-playback (Join-Path $repoRoot ('artifacts/validation/native-playback-' + [guid]::NewGuid().ToString('N')))
        if ($LASTEXITCODE -ne 0) { throw "常驻播放的变速重排与像素一致性检查失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --frame-clock (Join-Path $repoRoot ('artifacts/validation/frame-clock-' + [guid]::NewGuid().ToString('N') + '.json'))
        if ($LASTEXITCODE -ne 0) { throw "播放帧计时与取消释放检查失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --dogsc-camera-import
        if ($LASTEXITCODE -ne 0) { throw "DogSC 摄像头素材比例与保存失败回归失败，退出码 $LASTEXITCODE" }
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.AudioOverrides.Probe/FrameTrail.AudioOverrides.Probe.csproj') --configuration $Configuration -- (Join-Path $repoRoot ('artifacts/validation/audio-overrides-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
        if ($LASTEXITCODE -ne 0) { throw "逐片段音量与辅助媒体时钟测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.AudioOverrides.Probe/FrameTrail.AudioOverrides.Probe.csproj') --configuration $Configuration -- --source-rate-probe (Join-Path $repoRoot ('artifacts/validation/source-rate-' + [guid]::NewGuid().ToString('N')))
        if ($LASTEXITCODE -ne 0) { throw "辅助音频短时/长时时钟滤镜回归失败，退出码 $LASTEXITCODE" }
    }
    if ($IncludeIntegration) {
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.Integration/FrameTrail.Integration.csproj') --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "合成媒体集成测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Integration/FrameTrail.Integration.csproj') --configuration $Configuration -- --sync
        if ($LASTEXITCODE -ne 0) { throw "1080p60 音画同步集成测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Integration/FrameTrail.Integration.csproj') --configuration $Configuration -- --range-audio-export
        if ($LASTEXITCODE -ne 0) { throw "跨剪辑范围与双音轨同步导出测试失败，退出码 $LASTEXITCODE" }
    }

    if ($IncludeVisualProbe) {
        $visualOutput = Join-Path $repoRoot ('artifacts/validation/theme-offscreen-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- $visualOutput
        if ($LASTEXITCODE -ne 0) { throw "离屏主题文字与时间线像素检查失败，退出码 $LASTEXITCODE" }
        $timelineInputOutput = Join-Path $repoRoot ('artifacts/validation/timeline-input-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --timeline-input $timelineInputOutput
        if ($LASTEXITCODE -ne 0) { throw "时间线悬停保留与点击定位检查失败，退出码 $LASTEXITCODE" }
        $previewInteractionOutput = Join-Path $repoRoot ('artifacts/validation/preview-interaction-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --preview-interaction $previewInteractionOutput
        if ($LASTEXITCODE -ne 0) { throw "真实媒体预览调度与帧提交检查失败，退出码 $LASTEXITCODE" }
        $previewSizingOutput = Join-Path $repoRoot ('artifacts/validation/preview-sizing-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --preview-sizing $previewSizingOutput
        if ($LASTEXITCODE -ne 0) { throw "预览物理尺寸与源清晰度检查失败，退出码 $LASTEXITCODE" }
        foreach ($playbackMode in @('--playback-latency', '--playback-audio-latency')) {
            $playbackOutput = Join-Path $repoRoot ('artifacts/validation/' + $playbackMode.TrimStart('-') + '-' + [guid]::NewGuid().ToString('N'))
            & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- $playbackMode $playbackOutput
            if ($LASTEXITCODE -ne 0) { throw "后台播放暂停恢复检查 $playbackMode 失败，退出码 $LASTEXITCODE" }
        }
        $microphoneRecoveryOutput = Join-Path $repoRoot ('artifacts/validation/microphone-recovery-offscreen-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --microphone-access-recovery $microphoneRecoveryOutput
        if ($LASTEXITCODE -ne 0) { throw "离屏麦克风访问恢复提示检查失败，退出码 $LASTEXITCODE" }
        $errorRecoveryOutput = Join-Path $repoRoot ('artifacts/validation/error-recovery-offscreen-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --error-recovery $errorRecoveryOutput
        if ($LASTEXITCODE -ne 0) { throw "备忘录与全局错误恢复检查失败，退出码 $LASTEXITCODE" }
        $bundledResourceOutput = Join-Path $repoRoot ('artifacts/validation/bundled-resource-locator-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --bundled-resources $bundledResourceOutput
        if ($LASTEXITCODE -ne 0) { throw "随包资源路径纯逻辑检查失败，退出码 $LASTEXITCODE" }
        $desktopInstanceOutput = Join-Path $repoRoot ('artifacts/validation/desktop-instance-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --desktop-instance $desktopInstanceOutput
        if ($LASTEXITCODE -ne 0) { throw "后台单实例通信与恢复检查失败，退出码 $LASTEXITCODE" }
        $desktopIconsOutput = Join-Path $repoRoot ('artifacts/validation/desktop-icons-policy-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --desktop-icons $desktopIconsOutput
        if ($LASTEXITCODE -ne 0) { throw "桌面图标恢复策略检查失败，退出码 $LASTEXITCODE" }
        $desktopIconsUiOutput = Join-Path $repoRoot ('artifacts/validation/desktop-icons-ui-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --desktop-icons-ui $desktopIconsUiOutput
        if ($LASTEXITCODE -ne 0) { throw "桌面图标选项的浅深主题布局检查失败，退出码 $LASTEXITCODE" }
        $desktopSurfacesOutput = Join-Path $repoRoot ('artifacts/validation/desktop-surfaces-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --desktop-surfaces $desktopSurfacesOutput
        if ($LASTEXITCODE -ne 0) { throw "独立录制浮窗布局与动态主题检查失败，退出码 $LASTEXITCODE" }
        $recordingBarOutput = Join-Path $repoRoot ('artifacts/validation/recording-bar-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --recording-hotkey $recordingBarOutput
        if ($LASTEXITCODE -ne 0) { throw "录制条布局、菜单回调与快捷键策略检查失败，退出码 $LASTEXITCODE" }
        $trayMenuOutput = Join-Path $repoRoot ('artifacts/validation/tray-menu-window-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --tray-menu-window $trayMenuOutput
        if ($LASTEXITCODE -ne 0) { throw "托盘菜单布局、命令与工作区定位检查失败，退出码 $LASTEXITCODE" }
        $brandingOutput = Join-Path $repoRoot ('artifacts/validation/dogsc-branding-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- --branding $brandingOutput
        if ($LASTEXITCODE -ne 0) { throw 'DogSC 品牌元数据、上游图标与主题检查失败。' }
        Write-Host 'WPF离屏与隐藏 HWND 检查完成，未显示桌面窗口或访问采集设备；真实交互仍须单独验收。'
    }

    if ($IncludeCaptureProbe) {
        if (-not (Test-Path -LiteralPath $captureProbe -PathType Leaf)) {
            throw "采集探针项目不存在：$captureProbe"
        }
        & dotnet build $captureProbe --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "采集探针构建失败，退出码 $LASTEXITCODE" }
        foreach ($pureProbe in @('--capture-result-notice-test', '--audio-packet-clock-test', '--audio-qpc-jitter-diagnosis-test', '--audio-packet-diagnostics-test', '--track-dispose-race-test', '--failure-lifecycle-probe', '--playback-options-test', '--playback-session-volume-test', '--microphone-clock-segment-test', '--microphone-access-error-test')) {
            & dotnet run --no-build --project $captureProbe --configuration $Configuration -p:Platform=x64 -- $pureProbe
            if ($LASTEXITCODE -ne 0) { throw "采集纯逻辑探针 $pureProbe 失败，退出码 $LASTEXITCODE" }
        }
        Write-Host '采集探针构建和纯逻辑检查通过；脚本不会启动录制、访问设备或请求权限。'
    }
    if ($IncludeReleaseProbe) {
        & (Join-Path $PSScriptRoot 'test-release-manifest.ps1')
        & (Join-Path $PSScriptRoot 'test-source-provenance.ps1')
        & (Join-Path $PSScriptRoot 'test-source-zip-policy.ps1')
        & (Join-Path $PSScriptRoot 'test-utf8-script.ps1')
        if ($LASTEXITCODE -ne 0) { throw "UTF-8 子脚本回归失败，退出码 $LASTEXITCODE" }
        Write-Host '发行清单、源码来源与源码 ZIP 结构规则检查完成；使用隔离合成数据，未运行安装器或应用。'
    }
}
finally {
    Pop-Location
}
