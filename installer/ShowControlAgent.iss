; Show Control Agent installer (Inno Setup 6). Built by build-installer.ps1, which publishes the agent
; as one self-contained ShowControlAgent.exe first: show PCs don't need .NET installed.
;
; Interactive:  double-click ShowControlAgent-Setup-<version>.exe
; Silent (deploy to many PCs, e.g. over PsExec/Intune/GPO):
;   ShowControlAgent-Setup-1.4.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PCNAME="Stage Left"
;   Add /TASKS="firewall,startup" to choose tasks (default: both on, desktop shortcut off).
;
; Settings, apps, scenes and paired phones live in %LOCALAPPDATA%\ShowControl\agent.json for the user
; who runs the agent. Upgrading or uninstalling never touches that file.

#ifndef AppVersion
  #define AppVersion "1.6.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\agent"
#endif
#define AppName "Show Control Agent"
#define AppExe "ShowControlAgent.exe"
#define FirewallRule "Show Control Agent"

[Setup]
AppId={{6F1E4C2A-8B9D-4E3F-9A7C-5D2B1E0F3A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Moving Surface Interactive Media
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Show Control Agent
DefaultGroupName=Show Control
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=ShowControlAgent-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\branding\showcontrol.ico
UninstallDisplayName={#AppName}
; The agent is closed by PrepareToInstall below, so Restart Manager isn't needed.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "firewall"; Description: "Let phones and the dashboard on this network reach the agent (Windows Firewall rule)"; GroupDescription: "Network:"
Name: "startup"; Description: "Start the agent when I sign in to Windows (recommended for show PCs)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; Excludes: "{#AppExe},*.pdb,install.ps1"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\docs\SETUP.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\unity\ShowControlReceiver.cs"; DestDir: "{app}\unity"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Show Control: start and close show apps from your phone"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Firewall: one rule for the program (covers the WebSocket port, discovery and any port you change to later).
; profile=any because venue networks are often marked "Public" by Windows.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden waituntilterminated; Tasks: firewall; StatusMsg: "Adding the firewall rule…"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; Tasks: not firewall
; Start-with-Windows and the PC name belong to the signed-in user, not the admin account running setup.
Filename: "{app}\{#AppExe}"; Parameters: "--startup on"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: startup
Filename: "{app}\{#AppExe}"; Parameters: "--startup off"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: not startup
Filename: "{app}\{#AppExe}"; Parameters: "--set-name ""{code:PcName}"""; Flags: runasoriginaluser runhidden waituntilterminated; Check: HasPcName
Filename: "{app}\{#AppExe}"; Description: "Start Show Control Agent now"; Flags: postinstall nowait skipifsilent runasoriginaluser
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{app}\{#AppExe}"; Parameters: "--startup off"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"

[Code]
function PcName(Param: String): String;
begin
  Result := Trim(ExpandConstant('{param:PCNAME|}'));
end;

function HasPcName: Boolean;
begin
  Result := PcName('') <> '';
end;

{ Close a running agent so its exe can be replaced (upgrades). It starts again at the end. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Result := '';
end;
