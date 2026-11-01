; NovaLink Lumen 安装器
;
; 三条不变量（改这个文件之前必读，往后再改也必须守住）：
;   1. 卸载器只能删显式路径，永不出现 RMDir /r "$LOCALAPPDATA\NovaLink Lumen" 这类父目录通配；
;   2. 数据备份目录（Data_Backup_vX.Y.Z）永远与 LauncherData 同级、在它子树之外；
;   3. 升级安装不删除任何用户数据（只覆盖程序目录）。
;
; 版本号由 scripts/bump-version.ps1 逐条改写，本文件有四处：APP_VERSION、APP_VERSION_DISPLAY、
; VIProductVersion、以及 OUT 默认文件名里的版本号。别漏。
; 允许命令行覆盖：makensis /DAPP_VERSION=1.5.0 /DOUT=C:\tmp\test.exe installer\installer.nsi（/DOUT 仅用于自检）。

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
; StrStr 的函数体要先展开一次，之后才能按三参数形式调用 ${StrStr}。
; 新版 NSIS（3.09+）的 StrFunc.nsh 用 ${Using:StrFunc} 生成函数，旧版（3.0.x）用 FUNCTION_STRING_StrStr 宏，两个都要兼容。
!ifmacrodef STRFUNC_USING
  ${Using:StrFunc} StrStr
!else
  !insertmacro FUNCTION_STRING_StrStr
!endif

!define APP_NAME "NovaLink Lumen"
!define APP_EXE "NovaLink.Lumen.exe"
!ifndef APP_VERSION
  !define APP_VERSION "1.5.0"
!endif
; 显示用版本号：正式版与 APP_VERSION 相同，测试版是 "1.5.0 Preview"（由 build.ps1 传 -D 覆盖）。
; 只影响卸载项 DisplayVersion 与 BrandingText，不影响文件版本（文件版本必须纯数字）。
!ifndef APP_VERSION_DISPLAY
  !define APP_VERSION_DISPLAY "1.5.0"
!endif
!define APP_PUBLISHER "NovaLink"
!define APP_ID "NovaLink.Lumen"
!define LEGACY_APP_ID "NovaLink.Lumen.WinUI.Preview"
!define ELECTRON_APP_ID "c3290a45-29b6-5b93-8486-84c45adebe9f"
; 用户数据根与升级前备份目录（备份与 LauncherData 同级，卸载“删数据”不会连带删备份）。
!define DATA_ROOT "$LOCALAPPDATA\NovaLink Lumen"
!define LAUNCHER_DATA "${DATA_ROOT}\LauncherData"
!define BACKUP_DIR "${DATA_ROOT}\Data_Backup_v1.4.0"
; 安装/卸载日志：只追加到 %TEMP%，写失败不影响安装。
!define INSTALL_LOG "$TEMP\NovaLinkLumen-install.log"
; 程序文件来源目录：默认 dist/release，可用 /DSOURCE=... 指向别处（仅自检用）。
!ifndef SOURCE
  !define SOURCE "..\dist\release"
!endif
!ifndef OUT
  !define OUT "..\dist\NovaLinkLumen-WinUI-Setup-1.5.0.exe"
!endif

Name "${APP_NAME}"
OutFile "${OUT}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\NovaLink Lumen"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallDir"
SetCompressor /SOLID lzma
BrandingText "${APP_NAME} ${APP_VERSION_DISPLAY}"

VIProductVersion "1.5.0.0"
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
  ; 无条件先结束正在运行的启动器：旧版本运行中直接跑新安装包时，File /r 会因文件占用失败。
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM ${APP_EXE} /F'
  Pop $1

  ; 旧的 WinUI 预览版：只移除旧程序文件，账户与实例数据由新版本在首次启动时迁移。
  ReadRegStr $0 HKCU "Software\${LEGACY_APP_ID}" "InstallDir"
  StrCmp $0 "" legacy_done
    nsExec::Exec 'taskkill /IM NovaLink.Lumen.exe /F'
    Pop $1
    Delete "$SMPROGRAMS\NovaLink Lumen WinUI Preview.lnk"
    Delete "$DESKTOP\NovaLink Lumen WinUI Preview.lnk"
    ; 路径守卫：注册表里的 InstallDir 被污染成数据目录时，绝不做 RMDir /r。
    StrCpy $R0 "$0"
    Call IsDataPath
    StrCmp $R1 "1" legacy_skip_rmdir
      RMDir /r "$0"
      Goto legacy_rmdir_done
    legacy_skip_rmdir:
      StrCpy $R9 "legacy InstallDir 疑似用户数据目录，跳过删除：$0"
      Call WriteLog
  legacy_rmdir_done:
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
    StrCpy $R0 "$0"
    Call IsDataPath
    StrCmp $R1 "1" electron_skip_rmdir
      RMDir /r "$0"
      Goto electron_rmdir_done
    electron_skip_rmdir:
      StrCpy $R9 "Electron InstallLocation 疑似用户数据目录，跳过删除：$0"
      Call WriteLog
  electron_rmdir_done:
    DeleteRegKey HKCU "Software\${ELECTRON_APP_ID}"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${ELECTRON_APP_ID}"
  electron_done:

  ; 安装器快照（v1.4.1 第 4 层防线）：覆盖程序文件之前，先把当前数据落一份到备份目录。
  ; 失败不中断安装，只写日志 —— 快照是“多一层网”，不是安装的前置条件。
  IfFileExists "${LAUNCHER_DATA}\lumen.json" snapshot_plugins
    CreateDirectory "${BACKUP_DIR}\installer-snapshot"
    CopyFiles /SILENT "${LAUNCHER_DATA}\lumen.json" "${BACKUP_DIR}\installer-snapshot"
    StrCpy $R9 "安装器快照：lumen.json → ${BACKUP_DIR}\installer-snapshot"
    Call WriteLog
  snapshot_plugins:
  IfFileExists "${LAUNCHER_DATA}\plugins\*.*" snapshot_done
    CreateDirectory "${BACKUP_DIR}\installer-snapshot\plugins"
    nsExec::ExecToLog 'robocopy "${LAUNCHER_DATA}\plugins" "${BACKUP_DIR}\installer-snapshot\plugins" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP'
    Pop $1
    StrCpy $R9 "安装器快照：plugins → installer-snapshot\plugins（robocopy 退出码 $1）"
    Call WriteLog
  snapshot_done:

  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  WriteRegStr HKCU "Software\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayVersion" "${APP_VERSION_DISPLAY}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  ; 备份目录也记进卸载项：卸载器与诊断工具都靠它找到升级前快照。
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DataBackupDir" "${BACKUP_DIR}"
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
  ; 第一问明确列出将被删除的目录，不列游戏目录（那不在删除范围内）。
  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除 NovaLink Lumen 的账户、设置、皮肤与缓存？$\r$\n$\r$\n将删除：$\r$\n  ${LAUNCHER_DATA}$\r$\n  $LOCALAPPDATA\NovaLink Lumen Preview\Data$\r$\n$\r$\n游戏目录、世界与整合包不会被删除。" IDNO un_keep_launcher_data
    RMDir /r "${LAUNCHER_DATA}"
    RMDir /r "$LOCALAPPDATA\NovaLink Lumen Preview\Data"
  un_keep_launcher_data:

  ; 第二问：数据备份与 LauncherData 同级，只有用户明确选择才删除。默认 No（保留以便回退到 v1.4.0）。
  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除升级前的数据备份？$\r$\n$\r$\n${BACKUP_DIR}$\r$\n$\r$\n建议保留：回退到上一版本时用它恢复账户与实例数据。" IDNO un_keep_backup
    RMDir /r "${BACKUP_DIR}"
  un_keep_backup:

  ; 删程序目录前先断言：$INSTDIR 不能是数据根，且必须以 "NovaLink Lumen" 结尾；
  ; 否则只跳过删除并记日志（防注册表被改成父目录，连带删掉同级的数据备份）。
  StrCmp $INSTDIR "" un_skip_installdir
  StrCmp $INSTDIR "${DATA_ROOT}" un_skip_installdir
  StrCpy $R2 $INSTDIR "" -14
  StrCmp $R2 "NovaLink Lumen" 0 un_skip_installdir
    RMDir /r "$INSTDIR"
    Goto un_installdir_done
  un_skip_installdir:
    StrCpy $R9 "INSTDIR 路径异常，跳过删除程序目录：$INSTDIR"
    Call un.WriteLog
  un_installdir_done:
SectionEnd

; ---- 辅助函数 ----

; 判断 $R0 是否像“用户数据目录/数据根”：是则 $R1=1，调用方跳过 RMDir /r。
; 覆盖三种污染形态：空路径、数据根本身、以 LauncherData / Data_Backup / \Data 结尾的路径。
Function IsDataPath
  StrCpy $R1 "0"
  StrCmp $R0 "" isdatapath_root
  StrCmp $R0 "${DATA_ROOT}" isdatapath_root
  StrCmp $R0 "${LAUNCHER_DATA}" isdatapath_root
  StrCmp $R0 "${BACKUP_DIR}" isdatapath_root
  StrCpy $R2 $R0 "" -12
  StrCmp $R2 "LauncherData" isdatapath_root
  StrCpy $R2 $R0 "" -5
  StrCmp $R2 "\Data" isdatapath_root
  ${StrStr} $R2 "$R0" "Data_Backup"
  StrCmp $R2 "" 0 isdatapath_root
  Goto isdatapath_end
  isdatapath_root:
    StrCpy $R1 "1"
  isdatapath_end:
FunctionEnd

; 追加一行安装日志（入参 $R9）。日志写不进去就算了，绝不因日志失败中断安装。
Function WriteLog
  FileOpen $R8 "${INSTALL_LOG}" a
  StrCmp $R8 "" writelog_done
    FileWrite $R8 "[${APP_VERSION}] $R9$\r$\n"
    FileClose $R8
  writelog_done:
FunctionEnd

; 卸载器侧的日志函数（NSIS 的卸载函数必须加 un. 前缀）。
Function un.WriteLog
  FileOpen $R8 "${INSTALL_LOG}" a
  StrCmp $R8 "" un_writelog_done
    FileWrite $R8 "[uninstall] $R9$\r$\n"
    FileClose $R8
  un_writelog_done:
FunctionEnd
