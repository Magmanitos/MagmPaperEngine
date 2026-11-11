Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
Name "MagmPaper"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\MagmPaper"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "MagmPaper"
VIAddVersionKey /LANG=1033 "FileDescription" "MagmPaper Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "MagmPaper contributors"
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\MagmPaper.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

!define UNINSTALL "Software\Microsoft\Windows\CurrentVersion\Uninstall"
!define RUN "Software\Microsoft\Windows\CurrentVersion\Run"
!define ID "com.magmanitos.magmpaper"
Var PreviousDirectory
Var PreviousKey
Var PreviousNative
Var Startup
Var Updated

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $Startup HKCU "${RUN}" "${ID}"
  StrCpy $0 0
  ${Do}
    EnumRegKey $1 HKCU "${UNINSTALL}" $0
    ${If} $1 == ""
      ${ExitDo}
    ${EndIf}
    IntOp $0 $0 + 1
    ReadRegStr $2 HKCU "${UNINSTALL}\$1" "DisplayName"
    ${If} $2 == "MagmPaper"
      ReadRegStr $3 HKCU "${UNINSTALL}\$1" "InstallLocation"
      ${If} $3 == ""
        MessageBox MB_ICONSTOP "Не удалось определить папку прежней версии MagmPaper. Удалите её через параметры Windows; коллекция обоев сохранится."
        Abort
      ${EndIf}
      ${If} $PreviousKey != ""
        MessageBox MB_ICONSTOP "Найдено несколько установок MagmPaper. Удалите лишние через параметры Windows; коллекция обоев сохранится."
        Abort
      ${EndIf}
      StrCpy $PreviousKey $1
      StrCpy $PreviousDirectory $3
      StrCpy $INSTDIR $3
      ReadRegDWORD $PreviousNative HKCU "${UNINSTALL}\$1" "NativeDesktop"
    ${EndIf}
  ${Loop}
FunctionEnd

Section "MagmPaper"
  ${If} $PreviousKey != ""
    ${If} $PreviousNative == 1
      ExecWait '"$PreviousDirectory\MagmPaper.exe" --quit'
    ${EndIf}
    IfFileExists "$PreviousDirectory\Uninstall MagmPaper.exe" +3 0
      MessageBox MB_ICONSTOP "Не найден деинсталлятор прежней версии. Восстановите прежнюю установку перед обновлением."
      Abort
    ClearErrors
    ExecWait '"$PreviousDirectory\Uninstall MagmPaper.exe" /S /currentuser --updated _?=$PreviousDirectory' $0
    ${If} ${Errors}
    ${OrIf} $0 != 0
      MessageBox MB_ICONSTOP "Не удалось закрыть или удалить прежнюю версию. Завершите MagmPaper через трей и повторите установку."
      Abort
    ${EndIf}
    IfFileExists "$PreviousDirectory\MagmPaper.exe" 0 +3
      MessageBox MB_ICONSTOP "Прежняя версия ещё запущена. Завершите MagmPaper через трей и повторите установку."
      Abort
    Delete "$PreviousDirectory\Uninstall MagmPaper.exe"
    DeleteRegKey HKCU "${UNINSTALL}\$PreviousKey"
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall MagmPaper.exe"
  CreateShortCut "$DESKTOP\MagmPaper.lnk" "$INSTDIR\MagmPaper.exe"
  CreateShortCut "$SMPROGRAMS\MagmPaper.lnk" "$INSTDIR\MagmPaper.exe"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "DisplayName" "MagmPaper"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "Publisher" "MagmPaper contributors"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "DisplayIcon" "$INSTDIR\MagmPaper.exe"
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "UninstallString" '$\"$INSTDIR\Uninstall MagmPaper.exe$\"'
  WriteRegStr HKCU "${UNINSTALL}\MagmPaper" "QuietUninstallString" '$\"$INSTDIR\Uninstall MagmPaper.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL}\MagmPaper" "EstimatedSize" ${SIZE}
  WriteRegDWORD HKCU "${UNINSTALL}\MagmPaper" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL}\MagmPaper" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL}\MagmPaper" "NativeDesktop" 1
  ${If} $Startup != ""
    WriteRegStr HKCU "${RUN}" "${ID}" '$\"$INSTDIR\MagmPaper.exe$\" --autostart'
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ${GetParameters} $0
  ${GetOptions} $0 "--updated" $Updated
  ${IfNot} ${Errors}
    StrCpy $Updated "yes"
  ${EndIf}
FunctionEnd

Section "Uninstall"
  ; A native instance restores Explorer changes before it closes.
  ExecWait '"$INSTDIR\MagmPaper.exe" --quit'
  ; Only delete files shipped by this build. Never traverse AppData or remove
  ; unrelated files someone placed in the installation directory.
  ClearErrors
  Delete "$INSTDIR\MagmPaper.exe"
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Завершите MagmPaper через трей (сохраните изменения в редакторе), затем повторите удаление."
    SetErrorLevel 1
    Abort
  ${EndIf}
  !include "${DELETE_FILES}"
  Delete "$INSTDIR\Uninstall MagmPaper.exe"
  Delete "$DESKTOP\MagmPaper.lnk"
  Delete "$SMPROGRAMS\MagmPaper.lnk"
  DeleteRegKey HKCU "${UNINSTALL}\MagmPaper"
  ${If} $Updated != "yes"
    ReadRegStr $0 HKCU "${RUN}" "${ID}"
    ${If} $0 == '$\"$INSTDIR\MagmPaper.exe$\" --autostart'
      DeleteRegValue HKCU "${RUN}" "${ID}"
    ${EndIf}
  ${EndIf}
  RMDir "$INSTDIR"
SectionEnd
