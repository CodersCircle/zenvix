# ⚡ ZENVIX

[![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge&logo=windows)](https://microsoft.com)
[![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)](LICENSE.txt)

> **ZENVIX** is a professional, state-of-the-art developer suite and local web hosting engine built exclusively for **Windows**. Similar to Laravel Herd, VS Code, and Laragon, ZENVIX provides a premium local operating system orchestrator to seamlessly manage websites, databases, mail servers, and runtime environments with zero configuration.

---

## 📥 Direct Windows Download

Get the official ZENVIX Setup installer instantly in a single click:

[📥 DOWNLOAD ZENVIX SETUP FOR WINDOWS (73 MB)](https://github.com/CodersCircle/zenvix/raw/main/Zenvix-Setup.exe)

*(Note: ZENVIX is designed and built exclusively for Windows 10 & 11 64-bit systems).*

---

### 💻 System Requirements
* **Operating System:** Windows 10 or Windows 11 (64-bit strictly supported)
* **Privileges:** Administrator access (required to configure virtual hosts and register local domains automatically)
* **Framework:** None to install - .NET is bundled in the app

### 🚀 How to Install & Run
1. **Download:** Click the direct download button above.
2. **Launch Setup:** Double-click **`Zenvix-Setup.exe`**. The professional wizard will guide you through:
   * Accepting the License terms
   * Choosing your destination directory (`C:\ZENVIX` is recommended)
   * Creating Desktop & Start Menu shortcuts
3. **Run:** Launch the application from your new Desktop shortcut!

---

## ✨ Features

### 🖥️ Local Web Servers (Apache & Nginx)
* Launch high-performance, configured web servers with a single click.
* Auto-provision clean local virtual domains (e.g. `mysite.test`) mapped directly to your project paths.
* Integrated SSL trust mechanisms to support secure local HTTPS testing natively.

### 🗄️ Database Center & phpMyAdmin
* Provision MySQL / MariaDB databases instantly.
* Run a fully pre-configured, dynamic web administration panel via **phpMyAdmin** at:
  🔗 **`http://localhost:8080/phpmyadmin/`**

### 👤 Interactive Developer Profile
* A state-of-the-art profile manager located inside **Settings**.
* Customize your username, first name, and last name.
* **Dynamic Initials Avatar:** The system automatically calculates your initials for the global topbar (e.g. `John Smith` ➔ **`JS`**). If no last name is provided, it dynamically takes the first two characters of your first name (e.g. `John` ➔ **`JO`**), updating immediately across the UI!

### 📧 Mailpit Integration
* Built-in local mail server to capture and preview test outgoing emails, perfect for verifying contact forms and user activation flows.

---

## 📂 Project Structure

```
ZENVIX/
├── src/
│   ├── Zenvix.UI/             # WPF MVVM Presentation layer
│   ├── Zenvix.ViewModels/     # Core MVVM bindings, state navigation, & profiles
│   ├── Zenvix.Core/           # Server engines, SSL, directories, and orchestrators
│   └── Zenvix.Infrastructure/ # Database managers, Nginx/Apache configuration builders
├── tools/                     # Pre-packaged runtime configurations
├── Assets/                    # Application icons, graphics, & branding
└── zenvix.iss                 # Inno Setup installation wizard configuration
```

---

## 🛠️ Contribution & Development

If you want to compile ZENVIX from source code locally:

### 1. Publish the app (self-contained, single file)
```powershell
dotnet publish .\src\Zenvix.UI\Zenvix.UI.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o .\dist
Copy-Item .\tools .\dist\tools -Recurse -Force
```

### 2. Compile Setup Installer
Install **Inno Setup 6**, bump `AppVersion` in `zenvix.iss` (and `AssemblyVersion` in `Zenvix.UI.csproj`), then:
```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" .\zenvix.iss
Copy-Item .\Zenvix-Setup.exe .\versions\Zenvix-Setup-V110.exe   # V<major><minor><patch> - the in-app updater reads versions/
```
This generates the ready-to-distribute **`Zenvix-Setup.exe`** setup in the root workspace folder.

---

## 📄 License
This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.
