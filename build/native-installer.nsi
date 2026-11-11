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
Var StartupPreference
Var Updated

!macro AbortWithError Message
  FileOpen $R9 "$TEMP\MagmPaper-setup-error.txt" w
  FileWriteUTF16LE $R9 "${Message}"
  FileClose $R9
  SetErrorLevel 1
  MessageBox MB_ICONSTOP "${Message}" /SD IDOK
  Abort
!macroend

Function .onInit
  Delete "$TEMP\MagmPaper-setup-error.txt"
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $StartupPreference HKCU "${RUN}" "${ID}"
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
      ; electron-builder stores InstallLocation outside the Windows uninstall
      ; entry. Recover the directory from its quoted UninstallString instead.
      ${If} $3 == ""
        ReadRegStr $4 HKCU "${UNINSTALL}\$1" "UninstallString"
        StrCpy $5 $4 1
        ${If} $5 == '$\"'
          StrCpy $6 1
          ${Do}
            StrCpy $5 $4 1 $6
            ${If} $5 == ""
              ${ExitDo}
            ${EndIf}
            ${If} $5 == '$\"'
              IntOp $6 $6 - 1
              StrCpy $4 $4 $6 1
              ${GetFileName} $4 $5
              ${If} $5 == "Uninstall MagmPaper.exe"
                ${GetParent} $4 $3
              ${EndIf}
              ${ExitDo}
            ${EndIf}
            IntOp $6 $6 + 1
          ${Loop}
        ${EndIf}
      ${EndIf}
      ${If} $3 == ""
        !insertmacro AbortWithError "Не удалось определить папку прежней версии MagmPaper. Удалите её через параметры Windows; коллекция обоев сохранится."
      ${EndIf}
      ${If} $PreviousKey != ""
        !insertmacro AbortWithError "Найдено несколько установок MagmPaper. Удалите лишние через параметры Windows; коллекция обоев сохранится."
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
    ${IfNot} ${FileExists} "$PreviousDirectory\Uninstall MagmPaper.exe"
      !insertmacro AbortWithError "Не найден деинсталлятор прежней версии. Восстановите прежнюю установку перед обновлением."
    ${EndIf}
    InitPluginsDir
    ClearErrors
    CopyFiles /SILENT "$PreviousDirectory\Uninstall MagmPaper.exe" "$PLUGINSDIR\PreviousUninstall.exe"
    ${If} ${Errors}
      !insertmacro AbortWithError "Не удалось подготовить удаление прежней версии MagmPaper."
    ${EndIf}
    ClearErrors
    ExecWait '"$PLUGINSDIR\PreviousUninstall.exe" /S /currentuser --updated _?=$PreviousDirectory' $0
    ${If} ${Errors}
    ${OrIf} $0 != 0
      !insertmacro AbortWithError "Не удалось закрыть или удалить прежнюю версию. Завершите MagmPaper через трей и повторите установку."
    ${EndIf}
    ${If} ${FileExists} "$PreviousDirectory\MagmPaper.exe"
      !insertmacro AbortWithError "Прежняя версия ещё запущена. Завершите MagmPaper через трей и повторите установку."
    ${EndIf}
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
  ${If} $StartupPreference != ""
    WriteRegStr HKCU "${RUN}" "${ID}" '$\"$INSTDIR\MagmPaper.exe$\" --autostart'
  ${EndIf}
SectionEnd

Function un.onInit
  Delete "$TEMP\MagmPaper-setup-error.txt"
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
  StrCpy $0 0
  ${Do}
    ClearErrors
    Delete "$INSTDIR\MagmPaper.exe"
    ${IfNot} ${Errors}
      ${ExitDo}
    ${EndIf}
    ; CLR shutdown can briefly retain the image after the instance mutex exits.
    Sleep 100
    IntOp $0 $0 + 1
  ${LoopWhile} $0 < 50
  ${If} ${FileExists} "$INSTDIR\MagmPaper.exe"
    !insertmacro AbortWithError "Завершите MagmPaper через трей (сохраните изменения в редакторе), затем повторите удаление."
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
