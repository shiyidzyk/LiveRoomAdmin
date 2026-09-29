; ============================================================
;  直播间管理终端 (LiveRoomAdmin) 1.02.00.beta 安装程序
;  使用 NSIS 3.x + MUI2 构建
; ============================================================
Unicode True
Name "直播间管理终端"
OutFile "LiveRoomAdmin-Setup-1.02.00.exe"

; 安装到 64 位 Program Files
InstallDir "$PROGRAMFILES64\直播间管理终端"
InstallDirRegKey HKLM "Software\LiveRoomAdmin" "InstallLocation"

; 安装器本身请求管理员（写 Program Files / 创建快捷方式 / 注册表）
RequestExecutionLevel admin

; 压缩
SetCompressor lzma

; ---------------- 头文件 ----------------
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

; ---------------- MUI 定义 ----------------
!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "直播间管理终端 安装向导"
!define MUI_WELCOMEPAGE_TEXT "本向导将在你的电脑上安装「直播间管理终端」（含局域网 OBS 推流直播服务、网页直播间与后台管理）。$\r$\n$\r$\n软件启动时会请求管理员权限，用于添加直播所需的防火墙入站规则。$\r$\n$\r$\n点击「下一步」继续。"

; 安装结束后可选择立即启动
!define MUI_FINISHPAGE_RUN "$INSTDIR\LiveRoomAdmin.exe"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动直播间管理终端"
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "「直播间管理终端」已成功安装到你的电脑。"

; ---------------- 页面 ----------------
Page custom PageLangCreate PageLangLeave       ; 最开头：选择客户端界面语言（默认简体中文）
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom PageDesktopCreate PageDesktopLeave       ; 倒数第二步：桌面快捷方式
Page custom PageStartCreate PageStartLeave           ; 最后一步：开始菜单（便于固定到开始屏幕）
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---------------- 变量 ----------------
Var LangZh
Var LangEn
Var LangChoice
Var ChkDesktop
Var ChkStart
Var DesktopShortcut
Var StartShortcut

; ============================================================
;  自定义页 0：选择客户端界面语言（默认简体中文）
; ============================================================
Function PageLangCreate
  !insertmacro MUI_HEADER_TEXT "选择语言 / Choose Language" "选择客户端界面显示语言 / Select the UI language"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24u "客户端界面语言 / UI Language："
  Pop $0
  ${NSD_CreateRadioButton} 0 24u 100% 24u "简体中文（默认 / Default）"
  Pop $LangZh
  ${NSD_CreateRadioButton} 0 52u 100% 24u "English"
  Pop $LangEn
  ${NSD_SetState} $LangZh ${BST_CHECKED}
  StrCpy $LangChoice "zh"

  nsDialogs::Show
FunctionEnd

Function PageLangLeave
  ${NSD_GetState} $LangZh $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $LangChoice "zh"
  ${Else}
    StrCpy $LangChoice "en"
  ${EndIf}
FunctionEnd

; ============================================================
;  自定义页 1：桌面快捷方式
; ============================================================
Function PageDesktopCreate
  !insertmacro MUI_HEADER_TEXT "桌面快捷方式" "选择是否在桌面创建快捷方式"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateCheckBox} 0 0 100% 18u "在桌面创建「直播间管理终端」快捷方式"
  Pop $ChkDesktop
  ${NSD_SetState} $ChkDesktop ${BST_CHECKED}

  nsDialogs::Show
FunctionEnd

Function PageDesktopLeave
  ${NSD_GetState} $ChkDesktop $0
  StrCpy $DesktopShortcut $0
FunctionEnd

; ============================================================
;  自定义页 2：开始菜单（固定到开始屏幕）
; ============================================================
Function PageStartCreate
  !insertmacro MUI_HEADER_TEXT "开始菜单与开始屏幕" "创建开始菜单快捷方式，可自行固定到开始屏幕"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateCheckBox} 0 0 100% 24u "在「开始菜单 → 所有应用」创建「直播间管理终端」快捷方式$\r$\n（之后可在开始菜单右键该程序，选择「固定到开始屏幕」）"
  Pop $ChkStart
  ${NSD_SetState} $ChkStart ${BST_CHECKED}

  nsDialogs::Show
FunctionEnd

Function PageStartLeave
  ${NSD_GetState} $ChkStart $0
  StrCpy $StartShortcut $0
FunctionEnd

; ============================================================
;  安装段
; ============================================================
Section "直播间管理终端（必选）" SecCore
  SectionIn RO

  ; 安装前结束正在运行的主程序及其子进程树（解锁 mediamtx/node/ffmpeg 文件）
  nsExec::ExecToLog 'taskkill /IM LiveRoomAdmin.exe /T /F'
  Pop $0
  Sleep 500

  SetOutPath "$INSTDIR"
  ; 复制全部程序文件（含 bins、server、运行时）
  File /r "..\WindowsClient\publish\*"

  ; ---- 保留升级数据：已有账号不被覆盖 ----
  SetOverwrite off
  File "..\WindowsClient\publish\server\accounts.json"
  SetOverwrite on

  ; ---- 桌面快捷方式 ----
  ${If} $DesktopShortcut == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\直播间管理终端.lnk" "$INSTDIR\LiveRoomAdmin.exe" "" "$INSTDIR\LiveRoomAdmin.exe" 0
  ${EndIf}

  ; ---- 开始菜单程序组快捷方式 ----
  ${If} $StartShortcut == ${BST_CHECKED}
    CreateDirectory "$SMPROGRAMS\直播间管理终端"
    CreateShortcut "$SMPROGRAMS\直播间管理终端\直播间管理终端.lnk" "$INSTDIR\LiveRoomAdmin.exe" "" "$INSTDIR\LiveRoomAdmin.exe" 0
    CreateShortcut "$SMPROGRAMS\直播间管理终端\卸载直播间管理终端.lnk" "$INSTDIR\Uninstall.exe"
  ${EndIf}

  ; ---- 卸载器 ----
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; ---- 「应用和功能 / 添加删除程序」注册信息 ----
  SetRegView 64
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "DisplayName" "直播间管理终端"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "DisplayVersion" "1.02.00.beta"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "Publisher" "局域网直播测试"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "DisplayIcon" "$INSTDIR\LiveRoomAdmin.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin" "NoRepair" 1
  WriteRegStr HKLM "Software\LiveRoomAdmin" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\LiveRoomAdmin" "Language" "$LangChoice"

  DetailPrint "安装完成。"
SectionEnd

; ============================================================
;  卸载段
; ============================================================
Section "Uninstall"
  ; 结束正在运行的主程序及其拉起的子进程树（node / mediamtx / ffmpeg）
  nsExec::ExecToLog 'taskkill /IM LiveRoomAdmin.exe /T /F'
  Pop $0

  ; 删除快捷方式
  Delete "$DESKTOP\直播间管理终端.lnk"
  Delete "$SMPROGRAMS\直播间管理终端\直播间管理终端.lnk"
  Delete "$SMPROGRAMS\直播间管理终端\卸载直播间管理终端.lnk"
  RMDir "$SMPROGRAMS\直播间管理终端"

  ; 删除安装目录
  RMDir /r "$INSTDIR"

  ; 清理注册表
  SetRegView 64
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveRoomAdmin"
  DeleteRegKey HKLM "Software\LiveRoomAdmin"
SectionEnd
