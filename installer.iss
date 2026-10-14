; =====================================================================
;  Whatever IT PC Care - Inno Setup installer script
;  A proper graphical install wizard (NOT a command window).
;
;  Build the app first, then compile this with Inno Setup:
;     setup.bat installer
;  Download Inno Setup (free): https://jrsoftware.org/isdl.php
;  Result: Output\WhateverITPCCare-Setup.exe  <- give THIS to customers.
; =====================================================================

#define AppName "Whatever IT PC Care"
#define AppVersion "1.0.0"
#define AppPublisher "Whatever IT"
#define AppURL "https://www.whateverit.tech/"
#define AppExe "WhateverITPCCare.exe"

[Setup]
AppId={{A4F2C1E8-9D3B-4E7A-B2C6-7F1A2D3E4B50}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\Whatever IT PC Care
DefaultGroupName=Whatever IT PC Care
DisableProgramGroupPage=yes
DisableDirPage=no
DisableReadyPage=no
AllowNoIcons=yes
OutputBaseFilename=WhateverITPCCare-Setup
SetupIconFile=src\Assets\app.ico

; ---- A real, modern wizard look -------------------------------------
WizardStyle=modern
WizardSizePercent=120
WizardImageFile=installer\assets\wizard-large.bmp
WizardSmallImageFile=installer\assets\wizard-small.bmp
WizardImageStretch=yes
ShowLanguageDialog=no
SetupLogging=yes

; ---- Show the licence agreement the customer must accept ------------
LicenseFile=EULA.txt

; ---- Uninstall entry (Settings > Apps) with the wolf icon -----------
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\app.ico
CreateUninstallRegKey=yes

; ---- Packaging ------------------------------------------------------
Compression=lzma2/max
SolidCompression=yes

; A self-contained .NET app runs on 64-bit Windows only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Friendlier wording on the welcome / finished pages.
WelcomeLabel2=This will install [name/ver] on your computer.%n%nWhatever IT PC Care checks your PC's health, explains what it finds in plain English, and offers safe, one-click fixes.%n%nClick Next to continue.
FinishedHeadingLabel=Whatever IT PC Care is ready
FinishedLabelNoIcons=Setup has finished installing [name] on your computer.
FinishedLabel=Setup has finished installing [name] on your computer. You can start it from the desktop shortcut or the Start menu.

; NOTE: The desktop shortcut is intentionally created by the APP itself on
; first launch (see src/Helpers/ShortcutHelper.cs), NOT here. Having both the
; installer and the app create one produced TWO identical desktop icons (one on
; the shared "all users" desktop, one on the signed-in user's desktop). The app
; targets the real current-user desktop and honours folder redirection, so it is
; the single source of truth for the desktop icon.

[Files]
; The single-file self-contained build - no .NET install needed on the customer PC.
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; Ship the wolf logo icon so every shortcut can point at it explicitly.
Source: "src\Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion
; Recorded companion voice clips. These are loose files that sit beside the exe
; (not embedded in the single-file build), one folder per character
; (Assets\voices\male, Assets\voices\female), so the companion plays its real
; recorded voice. Without this the wolf would silently fall back to text-to-speech.
Source: "publish\Assets\voices\*"; DestDir: "{app}\Assets\voices"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\app.ico"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent shellexec

; =====================================================================
;  The in-app payment window uses the Microsoft Edge WebView2 runtime,
;  which ships with Windows 10/11. On the rare PC where it is missing,
;  offer to download the free Evergreen runtime from Microsoft.
; =====================================================================
[Code]
function WebView2Installed(): Boolean;
var
  pv: string;
  guid: string;
begin
  guid := '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + guid, 'pv', pv) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + guid, 'pv', pv) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + guid, 'pv', pv);
  if Result and (pv = '0.0.0.0') then
    Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ec: Integer;
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) then
  begin
    if MsgBox('This app needs the free Microsoft Edge WebView2 runtime for its in-app ' +
              'payment window, and it is not installed on this PC.' + #13#10#13#10 +
              'Download it from Microsoft now? (A browser will open.)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703',
                '', '', SW_SHOWNORMAL, ewNoWait, ec);
  end;
end;
