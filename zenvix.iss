; ══════════════════════════════════════════════════════════════════════
; ZENVIX Local Workstations Setup Script (Inno Setup)
; ══════════════════════════════════════════════════════════════════════

[Setup]
AppName=Zenvix
AppVersion=1.0.4
AppPublisher=Zenvix Development Team
DefaultDirName=C:\Zenvix
DefaultGroupName=Zenvix
UninstallDisplayIcon={app}\Zenvix.exe
Compression=lzma2/max
SolidCompression=yes
OutputDir=D:\RuningProjects\Hostix
OutputBaseFilename=Zenvix-Setup
SetupIconFile=D:\RuningProjects\Hostix\dist\Assets\Icons\zenvix.ico
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=D:\RuningProjects\Hostix\LICENSE.txt



[Messages]
WelcomeLabel1=Welcome to ZENVIX Setup
WelcomeLabel2=Zenvix is a professional multi-framework development workstation and local hosting system (NGINX, Apache, PHP, Node.js, Python, and Databases).

[Dirs]
Name: "{app}\runtimes"
Name: "{app}\config"
Name: "{app}\ssl"
Name: "{app}\logs"
Name: "{app}\temp"
Name: "{app}\projects"
Name: "{app}\backups"
Name: "{app}\snapshots"

[Files]
; Main Executable
Source: "D:\RuningProjects\Hostix\dist\Zenvix.exe"; DestDir: "{app}"; Flags: ignoreversion
; Database Driver
Source: "D:\RuningProjects\Hostix\dist\e_sqlite3.dll"; DestDir: "{app}"; Flags: ignoreversion
; Manifest file for runtime engine downloads
Source: "D:\RuningProjects\Hostix\dist\runtime-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
; Runtimes & Tools folder (Keep existing settings intact on upgrades)
Source: "D:\RuningProjects\Hostix\dist\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "D:\RuningProjects\Hostix\dist\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "D:\RuningProjects\Hostix\dist\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Zenvix"; Filename: "{app}\Zenvix.exe"
Name: "{group}\Uninstall Zenvix"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Zenvix"; Filename: "{app}\Zenvix.exe"

[Run]
Description: "Launch Zenvix (Admin Mode Recommended)"; Filename: "{app}\Zenvix.exe"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Safely terminate running runtimes on uninstall
Filename: "taskkill"; Parameters: "/F /IM nginx.exe /T"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillNginx"
Filename: "taskkill"; Parameters: "/F /IM httpd.exe /T"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillApache"
Filename: "taskkill"; Parameters: "/F /IM php-cgi.exe /T"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillPhp"
Filename: "taskkill"; Parameters: "/F /IM mysqld.exe /T"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillMysql"
Filename: "taskkill"; Parameters: "/F /IM Zenvix.exe /T"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillZenvix"

[Code]
// Custom installation logic if needed in the future
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
