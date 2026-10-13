# 录制时隐藏桌面图标

0.2.18 新增默认关闭的录制选项，入口位于“保存与录制配置”和设置的“录制”区域。选项保存到用户设置，显示器与区域录制共用，窗口录制沿用当前桌面状态。

开始采集前隐藏图标，暂停时继续隐藏；结束、丢弃、启动失败或应用退出后恢复录制前的状态。原本隐藏的桌面保持隐藏。此功能改变本机 Explorer 桌面的显示，不移动文件或重新排列图标。

## 实现与恢复

通过 Explorer 的 `IShellWindows` → `IShellBrowser` → `IFolderView2` 获取桌面，仅改变 `FWF_NOICONS` 位。接口依据微软的 [桌面视图访问示例](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933)、[FolderFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-folderflags) 和 [SetCurrentFolderFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-setcurrentfolderflags)。

独立隐藏进程在 STA 线程调用 Explorer，避免阻塞编辑器；主程序关闭管道或意外退出后，辅助进程恢复图标。隐藏前先写入本用户、本 Windows 会话的恢复记录，并用会话互斥锁防止两段录制互相覆盖。若整个进程树被终止，再次启动 DogSC 时读取记录恢复；录制配置也提供“恢复桌面图标”入口。记录位于 `%LOCALAPPDATA%\FrameTrail\desktop-icons\session-<id>.json`。

## 本机验证（2026-10-13）

- 恢复策略 11 项：默认关闭、原先隐藏、正常恢复、失败重试、损坏记录、Explorer 替换后的策略等。
- Windows 实测 8 项：真实桌面状态、实录暂停继续、正常停止、主进程意外退出、整个测试进程树终止后的记录恢复。生成 1280×720 H.264 文件并完整解码通过。
- 完整应用流程：录制、暂停、结束、编辑、重开、导出通过，结束时核对桌面恢复原状态。测试前后的用户设置与最近项目记录逐字节一致。
- 浅深主题布局与开关初始位置 8 项通过；Core 与导入回归通过。

Explorer 重启路径已做策略测试，未重启用户的 Explorer；跨 Windows 登录会话、多显示器、第三方桌面外壳尚未实测。截图、录屏和用户设置快照只保存在本地 `artifacts/validation/desktop-icons-*`，不随源码或发行包提交。

可复现入口：`scripts/test.ps1 -IncludeVisualProbe` 包含策略与离屏检查；VisualProbe 的 `--desktop-icons-native <output> <DogSC.exe>` 会短暂改变真实桌面状态并实录，`DogSC.exe --workflow-smoke <output> --with-hidden-desktop-icons --with-pause --record-seconds 4` 验证完整流程。真实桌面测试前须保留用户偏好快照，完成后核对恢复。
