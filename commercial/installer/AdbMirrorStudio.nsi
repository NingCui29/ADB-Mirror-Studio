Unicode True
RequestExecutionLevel user
SetCompressor /SOLID lzma
CRCCheck on

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !error "APP_VERSION must come from Directory.Build.props via build-installer.ps1."
!endif
!ifndef APP_FILE_VERSION
  !error "APP_FILE_VERSION must come from Directory.Build.props via build-installer.ps1."
!endif
!ifndef UNINSTALL_INCLUDE
  !error "UNINSTALL_INCLUDE must point to the generated payload removal manifest."
!endif
!include "${UNINSTALL_INCLUDE}"
!ifndef SOURCE_DIR
  !error "SOURCE_DIR must point to the unpacked self-contained release directory."
!endif
!ifndef LICENSE_FILE
  !error "LICENSE_FILE must point to the installer license text."
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "..\artifacts\installer"
!endif
!ifndef ESTIMATED_SIZE_KB
  !define ESTIMATED_SIZE_KB 0
!endif

!define APP_NAME "ADB Mirror Studio"
!define APP_EXE "AdbMirrorStudio.App.exe"
!define APP_REGISTRY_KEY "Software\ADB Mirror Studio"
!define UNINSTALL_REGISTRY_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\{8AD894DA-F472-495E-9266-D074F1EA586E}_ADBMirrorStudio"
!define REPOSITORY_URL "https://github.com/NingCui29/ADB-Mirror-Studio"

Name "${APP_NAME} V${APP_VERSION}"
Caption "${APP_NAME} V${APP_VERSION} 安装程序"
OutFile "${OUTPUT_DIR}\ADB-Mirror-Studio-Setup-V${APP_VERSION}-win-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\ADB Mirror Studio"
InstallDirRegKey HKCU "${APP_REGISTRY_KEY}" "InstallDir"
Icon "..\src\AdbMirrorStudio.App\Assets\AppIcon.ico"
UninstallIcon "..\src\AdbMirrorStudio.App\Assets\AppIcon.ico"
BrandingText "ADB Mirror Studio · 全部功能免费"
ShowInstDetails show
ShowUninstDetails show
AutoCloseWindow false
ManifestDPIAware true
VIProductVersion "${APP_FILE_VERSION}"
VIFileVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "V${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "ADB Mirror Studio"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} installer"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (C) 2026 ADB Mirror Studio contributors"
VIAddVersionKey /LANG=1033 "OriginalFilename" "ADB-Mirror-Studio-Setup-V${APP_VERSION}-win-x64.exe"

!ifdef SIGN_COMMAND
  !finalize '${SIGN_COMMAND}'
  !uninstfinalize '${SIGN_COMMAND}'
!endif

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\AdbMirrorStudio.App\Assets\AppIcon.ico"
!define MUI_UNICON "..\src\AdbMirrorStudio.App\Assets\AppIcon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ADB Mirror Studio"
!define MUI_FINISHPAGE_LINK "访问 GitHub 项目主页"
!define MUI_FINISHPAGE_LINK_LOCATION "${REPOSITORY_URL}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString MainSectionName ${LANG_SIMPCHINESE} "ADB Mirror Studio（必需）"
LangString MainSectionName ${LANG_ENGLISH} "ADB Mirror Studio (required)"
LangString DesktopSectionName ${LANG_SIMPCHINESE} "桌面快捷方式"
LangString DesktopSectionName ${LANG_ENGLISH} "Desktop shortcut"
LangString MainSectionDescription ${LANG_SIMPCHINESE} "安装程序本体、自包含 .NET/Windows App SDK、ADB、scrcpy 和许可文档。"
LangString MainSectionDescription ${LANG_ENGLISH} "Installs the application, self-contained runtimes, ADB, scrcpy, and license documents."
LangString DesktopSectionDescription ${LANG_SIMPCHINESE} "在当前用户桌面创建快捷方式。"
LangString DesktopSectionDescription ${LANG_ENGLISH} "Creates a shortcut on the current user's desktop."
LangString UnsupportedArchitecture ${LANG_SIMPCHINESE} "ADB Mirror Studio 仅支持 Windows x64。"
LangString UnsupportedArchitecture ${LANG_ENGLISH} "ADB Mirror Studio requires 64-bit Windows."
LangString CloseApplicationPrompt ${LANG_SIMPCHINESE} "安装目录中的 ADB Mirror Studio 正在运行或文件被占用。请先停止录屏并正常退出软件，然后重试。"
LangString CloseApplicationPrompt ${LANG_ENGLISH} "ADB Mirror Studio in the installation directory is running or its files are in use. Stop recording and exit the application, then retry."
LangString RemoveUserDataPrompt ${LANG_SIMPCHINESE} "是否同时删除本机设置和崩溃日志？$\r$\n$LOCALAPPDATA\AdbMirrorStudio$\r$\n$\r$\n选择“否”可在以后重新安装时保留设置。"
LangString RemoveUserDataPrompt ${LANG_ENGLISH} "Also delete local settings and crash logs?$\r$\n$LOCALAPPDATA\AdbMirrorStudio$\r$\n$\r$\nChoose No to preserve settings for a future installation."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(UnsupportedArchitecture)"
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
FunctionEnd

!macro EnsureApplicationClosed Prefix
${Prefix}WaitStart:
  StrCpy $1 30
${Prefix}Retry:
  IfFileExists "$INSTDIR\${APP_EXE}" 0 ${Prefix}Ready
  ; OPEN_EXISTING with write access detects a running image without modifying the file.
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\${APP_EXE}", i 0x40000000, i 0, p 0, i 3, i 0, p 0) p.r0'
  StrCmp $0 -1 ${Prefix}Busy
  System::Call 'kernel32::CloseHandle(p r0)'
  Goto ${Prefix}Ready
${Prefix}Busy:
  ; In-app updates launch the installer before asynchronous application shutdown completes.
  IntCmp $1 0 ${Prefix}Prompt
  Sleep 1000
  IntOp $1 $1 - 1
  Goto ${Prefix}Retry
${Prefix}Prompt:
  IfSilent ${Prefix}Abort
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(CloseApplicationPrompt)" IDRETRY ${Prefix}WaitStart
${Prefix}Abort:
  SetErrorLevel 2
  Abort
${Prefix}Ready:
!macroend

Section "$(MainSectionName)" MainSection
  SectionIn RO
  !insertmacro EnsureApplicationClosed Install

  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\ADB Mirror Studio"
  CreateShortcut "$SMPROGRAMS\ADB Mirror Studio\ADB Mirror Studio.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\Assets\AppIcon.ico" 0 SW_SHOWNORMAL "" "Android 镜像与设备管理工作台"
  CreateShortcut "$SMPROGRAMS\ADB Mirror Studio\卸载 ADB Mirror Studio.lnk" "$INSTDIR\Uninstall.exe"

  IfFileExists "$QUICKLAUNCH\User Pinned\TaskBar\ADB Mirror Studio.lnk" 0 +2
    CreateShortcut "$QUICKLAUNCH\User Pinned\TaskBar\ADB Mirror Studio.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\Assets\AppIcon.ico" 0 SW_SHOWNORMAL "" "Android 镜像与设备管理工作台"

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  nsExec::Exec '"$SYSDIR\ie4uinit.exe" -show'
  Pop $0

  WriteRegStr HKCU "${APP_REGISTRY_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "DisplayName" "${APP_NAME} V${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "Publisher" "ADB Mirror Studio"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "URLInfoAbout" "${REPOSITORY_URL}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "URLUpdateInfo" "${REPOSITORY_URL}/releases"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "HelpLink" "${REPOSITORY_URL}/issues"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" "EstimatedSize" ${ESTIMATED_SIZE_KB}
SectionEnd

Section /o "$(DesktopSectionName)" DesktopSection
  CreateShortcut "$DESKTOP\ADB Mirror Studio.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\Assets\AppIcon.ico" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${MainSection} "$(MainSectionDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${DesktopSection} "$(DesktopSectionDescription)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Uninstall"
  !insertmacro EnsureApplicationClosed Uninstall

  Delete "$DESKTOP\ADB Mirror Studio.lnk"
  Delete "$QUICKLAUNCH\User Pinned\TaskBar\ADB Mirror Studio.lnk"
  Delete "$SMPROGRAMS\ADB Mirror Studio\ADB Mirror Studio.lnk"
  Delete "$SMPROGRAMS\ADB Mirror Studio\卸载 ADB Mirror Studio.lnk"
  RMDir "$SMPROGRAMS\ADB Mirror Studio"
  DeleteRegKey HKCU "${UNINSTALL_REGISTRY_KEY}"
  DeleteRegKey HKCU "${APP_REGISTRY_KEY}"

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  nsExec::Exec '"$SYSDIR\ie4uinit.exe" -show'
  Pop $0

  !insertmacro RemoveInstalledPayload

  IfSilent PreserveUserData
  MessageBox MB_YESNO|MB_ICONQUESTION "$(RemoveUserDataPrompt)" IDNO PreserveUserData
  RMDir /r "$LOCALAPPDATA\AdbMirrorStudio"
PreserveUserData:
SectionEnd
