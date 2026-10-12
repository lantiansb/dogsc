#ifndef AppVersion
  #define AppVersion "0.2.18"
#endif
#ifndef AppExeName
  #define AppExeName "DogSC.exe"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\artifacts\release\DogSC-0.2.18-win-x64"
#endif

[Setup]
AppId={{9EB9A39E-9EF9-4E70-9D2B-E081ECBC1A68}
AppName=DogSC
AppVersion={#AppVersion}
AppPublisher=lantiansb · DogSC Windows 社区移植
AppPublisherURL=https://github.com/lantiansb/dogsc
AppSupportURL=https://github.com/lantiansb/dogsc/issues
AppUpdatesURL=https://github.com/lantiansb/dogsc/releases
SetupIconFile=..\src\FrameTrail.App\Assets\DogSC.ico
DefaultDirName={localappdata}\Programs\DogSC
UsePreviousAppDir=yes
DefaultGroupName=DogSC
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
UninstallDisplayName=DogSC
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\artifacts\release
OutputBaseFilename=DogSC-Setup-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile={#ReleaseDir}\VC-RUNTIME-REQUIREMENT.txt
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[InstallDelete]
; Keep AppId stable so an existing FrameTrail install upgrades in place. Remove only its obsolete launcher files.
Type: files; Name: "{app}\FrameTrail.exe"
Type: files; Name: "{app}\FrameTrail.dll"
Type: files; Name: "{app}\FrameTrail.deps.json"
Type: files; Name: "{app}\FrameTrail.runtimeconfig.json"
Type: files; Name: "{app}\FrameTrail.pdb"
Type: files; Name: "{autoprograms}\FrameTrail.lnk"
Type: files; Name: "{autodesktop}\FrameTrail.lnk"

[Files]
Source: "{#ReleaseDir}\BUILD-INFO.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\VC-RUNTIME-REQUIREMENT.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ReleaseDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ReleaseDir}\third_party\ffmpeg\README.md"; DestDir: "{app}\third_party\ffmpeg"; Flags: ignoreversion
Source: "{#ReleaseDir}\third_party\ffmpeg\manifest.json"; DestDir: "{app}\third_party\ffmpeg"; Flags: ignoreversion
Source: "{#ReleaseDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ReleaseDir}\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DogSC"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\DogSC"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 DogSC"; Flags: postinstall nowait skipifsilent

[Code]
function HasVCRuntimeX64: Boolean;
var
  Installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
    'Installed', Installed) and (Installed = 1);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasVCRuntimeX64 then
    Result := MsgBox(
      '录制功能依赖 Microsoft Visual C++ 2015–2022 Redistributable (x64)。' + #13#10 + #13#10 +
      '本安装程序不会安装或更新系统运行库。请从微软官方页面手动安装：' + #13#10 +
      'https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist' + #13#10 + #13#10 +
      '仍要继续安装 DogSC 吗？',
      mbConfirmation, MB_YESNO) = IDYES;
end;
