; â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
; ZENVIX Setup (Inno Setup 6)
;
; Build:
;   dotnet publish .\src\Zenvix.UI\Zenvix.UI.csproj -c Release -r win-x64 --self-contained true ^
;       -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;       -p:EnableCompressionInSingleFile=true -p:DebugType=none -o .\dist
;   xcopy /E /I /Y tools dist\tools
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" zenvix.iss
;
; Output: Zenvix-Setup.exe (repo root). Copy it to versions\Zenvix-Setup-V<major><minor><patch>.exe
; so the in-app updater (which reads the versions/ folder on GitHub) offers it.
; â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

#define AppVersion "1.1.0"

[Setup]
; No explicit AppId: Inno uses AppName ("Zenvix"), the same as every earlier installer,
; so this upgrades existing installs in place instead of adding a second entry.
AppName=Zenvix
AppVersion={#AppVersion}
AppVerName=Zenvix {#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=CodersCircle
AppPublisherURL=https://github.com/CodersCircle/zenvix
AppSupportURL=https://github.com/CodersCircle/zenvix/issues
DefaultDirName=C:\Zenvix
DefaultGroupName=Zenvix
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Zenvix.exe
Compression=lzma2/max
SolidCompression=yes
OutputDir=.
OutputBaseFilename=Zenvix-Setup
SetupIconFile=src\Zenvix.UI\Assets\Icons\zenvix.ico
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
LicenseFile=LICENSE.txt
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Zenvix keeps its runtimes, data and config next to the exe, so close it before upgrading.
CloseApplications=yes
RestartApplications=no

[Messages]
WelcomeLabel1=Welcome to ZENVIX {#AppVersion} Setup
WelcomeLabel2=Zenvix is a local development workstation for Windows: Nginx, Apache, PHP, MySQL, built-in containers, project scaffolding (Laravel, Filament, React, Vue, Next.js, Flutter, React Native, Ionic) and one-click deployment.%n%nYour existing websites, databases and settings are kept when upgrading.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
Name: "{app}\runtimes"
Name: "{app}\config"
Name: "{app}\ssl"
Name: "{app}\logs"
Name: "{app}\projects"

[Files]
; Single-file, self-contained app (no .NET install needed)
Source: "dist\Zenvix.exe"; DestDir: "{app}"; Flags: ignoreversion
; Download list for runtimes (PHP, MySQL, Nginx, ...) â€“ always refreshed on upgrade
Source: "dist\runtime-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "dist\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Zenvix"; Filename: "{app}\Zenvix.exe"
Name: "{group}\Uninstall Zenvix"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Zenvix"; Filename: "{app}\Zenvix.exe"; Tasks: desktopicon

[Run]
Description: "Launch Zenvix"; Filename: "{app}\Zenvix.exe"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop only processes running from the Zenvix folder (never XAMPP/Herd/Laragon or system services).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process | Where-Object {{ $_.Path -like '{app}\*' } | Stop-Process -Force"""; Flags: runhidden; RunOnceId: "StopZenvixProcesses"

[Code]
// Before files are replaced on upgrade: stop Zenvix and the runtimes it started from {app}
// (nginx, mysqld, php-cgi ...), otherwise their files are locked.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

