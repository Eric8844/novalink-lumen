Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APP_NAME "NovaLink Lumen"
!define APP_EXE "NovaLink.Lumen.exe"
!define APP_VERSION "1.4.0"
!define APP_PUBLISHER "NovaLink"
!define APP_ID "NovaLink.Lumen"
!define LEGACY_APP_ID "NovaLink.Lumen.WinUI.Preview"
!define ELECTRON_APP_ID "c3290a45-29b6-5b93-8486-84c45adebe9f"
!define SOURCE "..\dist\release"
!define OUT "..\dist\NovaLinkLumen-Setup.exe"

Name "${APP_NAME}"
OutFile "${OUT}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\NovaLink Lumen"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallDir"
SetCompressor /SOLID lzma
BrandingText "NovaLink Lumen"

VIProductVersion "1.4.0.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "NovaLink Lumen 安装程序"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "NovaLink"

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\NovaLink.Lumen\Assets\AppIcon.ico"
!define MUI_UNICON "..\src\NovaLink.Lumen\Assets\AppIcon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 NovaLink Lumen"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "NovaLink Lumen" SEC_MAIN
  ; 旧的 WinUI 预览版：只移除旧程序文件，账户与实例数据由新版本在首次启动时迁移。
  ReadRegStr $0 HKCU "Software\${LEGACY_APP_ID}" "InstallDir"
  StrCmp $0 "" legacy_done
    nsExec::Exec 'taskkill /IM NovaLink.Lumen.exe /F'
    Pop $1
    Delete "$SMPROGRAMS\NovaLink Lumen WinUI Preview.lnk"
    Delete "$DESKTOP\NovaLink Lumen WinUI Preview.lnk"
    RMDir /r "$0"
    DeleteRegKey HKCU "Software\${LEGACY_APP_ID}"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${LEGACY_APP_ID}"
  legacy_done:

  ; 旧的 Electron 稳定版（.js 版）：直接覆盖升级。只移除旧程序目录，
  ; 账户、皮肤、实例配置仍保留在 %LOCALAPPDATA%\NovaLink Lumen\Data，由新版本首次启动迁移。
  ReadRegStr $0 HKCU "Software\${ELECTRON_APP_ID}" "InstallLocation"
  StrCmp $0 "" electron_done
    nsExec::Exec 'taskkill /IM NovaLink Lumen.exe /F'
    Pop $1
    Delete "$SMPROGRAMS\NovaLink Lumen.lnk"
    Delete "$DESKTOP\NovaLink Lumen.lnk"
    RMDir /r "$0"
    DeleteRegKey HKCU "Software\${ELECTRON_APP_ID}"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${ELECTRON_APP_ID}"
  electron_done:

  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  WriteRegStr HKCU "Software\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\卸载 ${APP_NAME}.lnk"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
  DeleteRegKey HKCU "Software\${APP_ID}"

  ; 只有用户主动卸载时才询问是否一并删除启动器数据；默认保留。
  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除 NovaLink Lumen 的账户、设置、皮肤与缓存？$\r$\n游戏目录、世界与整合包不会被删除。" IDNO keep_data
  RMDir /r "$LOCALAPPDATA\NovaLink Lumen\LauncherData"
  RMDir /r "$LOCALAPPDATA\NovaLink Lumen Preview\Data"
keep_data:
  RMDir /r "$INSTDIR"
SectionEnd
