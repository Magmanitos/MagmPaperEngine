!macro customInstall
  ; Preserve a previously chosen autostart setting while moving from VS Code/ZIP.
  ; Keeping the value name also preserves Windows' StartupApproved state.
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_ID}"
  ${If} $0 != ""
    StrCpy $1 '"$INSTDIR\${APP_EXECUTABLE_FILENAME}" --autostart'
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_ID}" $1
  ${EndIf}
!macroend

!macro customUnInstall
  ; The update uninstaller must leave the preference for the new installer.
  ${IfNot} ${isUpdated}
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_ID}"
    StrCpy $1 '"$INSTDIR\${APP_EXECUTABLE_FILENAME}" --autostart'
    ${If} $0 == $1
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_ID}"
    ${EndIf}
  ${EndIf}
!macroend
