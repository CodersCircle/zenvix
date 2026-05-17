git reset --hard HEAD
git clean -fd

git reset --hard HEAD — Instantly reverts every single file back to the exact code you had just now.
git clean -fd — Instantly deletes any new files or folders we might have created during Phase 2 that were a mistake.

Get-Command iscc -ErrorAction SilentlyContinue
Get-ChildItem -Filter nginx.exe -Recurse -ErrorAction SilentlyContinue
Copy-Item -Path .\src\Hostix.UI\bin\Debug\net8.0-windows\runtimes -Destination .\dist\runtimes -Recurse -Force
Copy-Item -Path .\tools -Destination .\dist\tools -Recurse -Force
Invoke-WebRequest -Uri "https://files.jrsoftware.org/is/6/innosetup-6.3.3.exe" -OutFile ".\is-setup.exe"
winget install --id JRSoftware.InnoSetup --silent --accept-source-agreements --accept-package-agreements

Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
Test-Path "C:\Program Files\Inno Setup 6\ISCC.exe"
Get-ChildItem -Path "C:\Program Files" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue; Get-ChildItem -Path "C:\Program Files (x86)" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue; Get-ChildItem -Path "C:\Users" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue



& "C:\Users\BABA\AppData\Local\Programs\Inno Setup 6\ISCC.exe" D:\RuningProjects\Hostix\zenvix.iss

Get-ChildItem -Filter *.bmp -Recurse -ErrorAction SilentlyContinue

taskkill /F /IM Zenvix-Setup.exe /T; Remove-Item -Path "D:\RuningProjects\Hostix\Zenvix-Setup.exe" -Force -ErrorAction SilentlyContinue; & "C:\Users\BABA\AppData\Local\Programs\Inno Setup 6\ISCC.exe" D:\RuningProjects\Hostix\zenvix.iss


taskkill /F /IM Zenvix-Setup.exe /T; Remove-Item -Path "D:\RuningProjects\Hostix\Zenvix-Setup.exe" -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; & "C:\Users\BABA\AppData\Local\Programs\Inno Setup 6\ISCC.exe" D:\RuningProjects\Hostix\zenvix.iss



For APP BUILD 
taskkill /F /IM Hostix.UI.exe /T
dotnet clean .\src\Hostix.sln
dotnet build .\src\Hostix.sln -c Release

Run App 
dotnet run --project .\src\Hostix.UI\Hostix.UI.csproj



