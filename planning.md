# HOSTIX: The Windows Local Hosting Operating System

Hostix is not just a server manager; it is a **Local Hosting Operating System** designed to bridge the gap between local development and production reality. It combines the simplicity of Laravel Herd with the customizability of a cPanel-like dashboard, all optimized for Windows.

---

## 1. STRATEGIC POSITIONING
*   **The Workflow:** "Code locally, simulate production, deploy with confidence."
*   **The Philosophy:** Automation of boring tasks (VHosts, SSL, Queues) with deep control over environment variables and runtime modes.
*   **The Target:** Developers who want a premium, stable, and "production-parity" workstation without the overhead of Docker.

---

## 2. DUAL-SERVER STRATEGY: APACHE & NGINX
Hostix leverages Laragon's ability to switch engines, but adds a strategic recommendation:
*   **Apache (Default/Beginner):** Best for WordPress, custom PHP apps using `.htaccess`, and beginners who want "things to just work" without complex config.
*   **Nginx (Production/Advanced):** Best for Laravel, modern SPAs (Vite), and high-performance API testing. Use this to simulate real-world hosting environments like Laravel Forge or DigitalOcean.
*   **Switching:** One-click toggle via the Hostix Dashboard.

---

## 3. THE INTELLIGENT PROJECT ENGINE
### A. Project Type Detection
Hostix automatically detects the framework upon folder creation:
*   **Laravel:** Looks for `artisan`. Auto-sets root to `/public`.
*   **WordPress:** Looks for `wp-config.php`. Auto-sets root to `/`.
*   **React/Vite:** Looks for `vite.config.js`. Auto-sets root to `/dist`.
*   **Static/Node:** Looks for `package.json` or `index.html`.

### B. The "Run Website" Workflow (Launcher)
When a user clicks "Run":
1.  **Dependency Check:** Verifies Port 80/443 and SSL validity.
2.  **Environment Mode:** Loads the selected `.env` profile.
3.  **Process Spawn:** 
    *   Starts `npm run dev` for Vite apps.
    *   Starts `php artisan queue:work` for Laravel.
    *   Starts `php artisan schedule:work`.
4.  **Browser Launch:** Opens the `{project}.test` domain.

---

## 4. ENVIRONMENT MODES (PRODUCTION SIMULATION)
Hostix introduces a **Production Simulation Mode** to catch bugs before they reach the server.

| Feature | Development Mode | Production Simulation |
| :--- | :--- | :--- |
| **APP_DEBUG** | `true` (Full stack traces) | `false` (Custom 500 pages) |
| **Caching** | Disabled | Enabled (Config, Route, View) |
| **Optimization** | Raw code | `composer install --optimize-autoloader` |
| **Errors** | Shown in browser | Logged to Error Dashboard only |
| **HTTPS** | Optional | Enforced |

**Backup & Rollback:** Before switching modes, Hostix creates a `.env.backup_{timestamp}` to ensure no data is lost.

---

## 5. PROJECT ISOLATION ARCHITECTURE
Each project is treated as a "Virtual Tenant":
*   **PHP Isolation:** Select PHP 7.4 for legacy app A, and PHP 8.3 for app B via custom Nginx `fastcgi_pass` mapping.
*   **SSL Isolation:** Unique certificates generated per domain using Hostix's internal CA.
*   **Log Isolation:**
    *   `C:\Hostix\projects\{project}\logs\access.log`
    *   `C:\Hostix\projects\{project}\logs\error.log`

---

## 6. SERVICE SUITE: QUEUES, MAIL & MONITORING
### A. Queue & Scheduler Management
*   **Dashboard View:** Monitor queue status (Active/Idle), see "Failed Jobs" count.
*   **Controls:** Restart workers or clear the queue with one click.

### B. Local Mail Testing (Mailpit)
*   Integrates **Mailpit** (the modern successor to Mailhog).
*   **SMTP:** `127.0.0.1:1025`
*   **UI:** `http://localhost:8025`
*   Captures all outgoing emails from any local project for testing without actual delivery.

### C. Resource Monitoring
A lightweight side-panel showing:
*   **Global Health:** CPU/RAM usage of Nginx + MariaDB.
*   **Port Monitor:** Identifies which app is using which port.
*   **Service Status:** Real-time "Healthy/Down" badges for PHP-FPM, MySQL, Redis, and Mailpit.

---

## 7. CONFIG PROTECTION SYSTEM
To prevent Laragon from overwriting custom Nginx/Apache tweaks:
1.  **Template Inheritance:** Modify `laragon/usr/tpl/*.tpl` to set global defaults.
2.  **Config Locking:** Hostix Dashboard can set files to "Read Only" after generation to prevent auto-reversion.
3.  **Safe Merge:** Manual edits should be done in `etc/nginx/sites-enabled/manual/` which Hostix never touches.

---

## 8. ERROR DASHBOARD (CENTRALIZED LOGS)
A unified interface to filter logs by:
*   **Severity:** Info, Warning, Error, Critical.
*   **Source:** Nginx, PHP-FPM, MySQL, or Application (Laravel `storage/logs`).
*   **Real-time Tail:** Watch errors as they happen during a "Run" session.

---

## 9. UPDATED FOLDER HIERARCHY
```text
C:\Hostix\
├── bin\                    # Laragon / Core Binaries
├── projects\               # Parked Root
│   └── {project_name}\
│       ├── public_html\    # The web-visible root
│       ├── .hostix\        # Hostix metadata (Type, Mode, Icons)
│       ├── logs\           # Site-specific logs
│       └── backups\        # Local DB/Env backups
├── data\                   # Persistent DB Data (MariaDB, Redis)
├── templates\              # Starter skeletons (Laravel, React, WP)
└── dashboard\              # The Hostix UI source/binaries
```

---

## 10. UI/UX ARCHITECTURE (DASHBOARD CONCEPT)
*   **Theme:** "Deep Slate" Dark Mode with "Hostix Purple" accents.
*   **Sidebar:** `Dashboard`, `My Sites`, `Databases`, `Mail`, `Logs`, `System Health`.
*   **Website Card Components:**
    *   **Status Badge:** Pulsing green (Running), Static grey (Stopped).
    *   **Action Bar:** Play/Stop, Folder icon, VS Code icon, Terminal icon, DB icon.
    *   **Quick Info:** PHP version tag, Domain link, Environment Mode badge.

---

## 11. BACKUP & RESTORE SYSTEM
*   **Snapshots:** Right-click a site to "Take Snapshot" (Zips `public_html`, exports DB, saves `.env`).
*   **Restore:** One-click rollback to a previous snapshot if an update breaks the project.

---

## 12. MAINTENANCE & BEST PRACTICES
1.  **Always Run as Admin:** Required for Windows `hosts` file and SSL certificate binding.
2.  **Keep `C:\Hostix` Root:** Moving the root can break hard-coded paths in some PHP extensions.
3.  **Regular Log Clearing:** Hostix includes a "Purge Logs" button to save disk space on large dev projects.

---

# PART II: INTERNAL SOFTWARE ENGINEERING ARCHITECTURE

---

## 13. CORE APPLICATION ARCHITECTURE
Hostix is built using the **MVVM (Model-View-ViewModel)** pattern on **.NET 8**.

*   **Language:** C# 12
*   **Framework:** WPF (MahApps.Metro)
*   **Toolkit:** CommunityToolkit.Mvvm
*   **DI:** Microsoft.Extensions.DependencyInjection
*   **Metadata DB:** SQLite + EF Core
*   **Logging:** Serilog (Rolling Files + Memory Sink)

---

## 14. MODULAR SERVICE SYSTEM
*   **Infrastructure:** ServiceManager, RuntimeEngine, ProcessManager, SecurityManager.
*   **Project Logic:** WebsiteManager, ProjectScanner, EnvironmentManager, DomainManager, SSLManager.
*   **Monitoring:** QueueManager, SchedulerManager, ResourceMonitor, ErrorManager.

---

## 15. SOLUTION & PROJECT ARCHITECTURE
The `Hostix.sln` is organized into functional layers to support massive scalability:

*   **Hostix.Core:** Domain models, base interfaces, and constants.
*   **Hostix.Infrastructure:** SQLite DB context, FileSystem observers, and VHost templates.
*   **Hostix.Runtime:** Process orchestration, Shell execution, and Service lifecycles.
*   **Hostix.Modules:** Individual logic blocks (SSL, Domain, Queue, etc.).
*   **Hostix.UI:** WPF Views and Resource Dictionaries (XAML).
*   **Hostix.ViewModels:** MVVM UI logic and state bindings.
*   **Hostix.Events:** Internal Message Bus types and Dispatchers.
*   **Hostix.Shared:** Utilities, Path sanitizers, and extension methods.
*   **Hostix.Plugins.Abstractions:** SDK for building framework-specific extensions.

---

## 16. INTER-PROCESS COMMUNICATION (IPC)
Hostix maintains a low-latency communication channel between the main UI and its background workers/plugins.

*   **Named Pipes:** Primary IPC for high-speed, secure command transmission from the UI to the Runtime Engine.
*   **WebSockets (Internal):** Used for real-time log streaming from child processes (Nginx/Artisan) to the Error Dashboard.
*   **Message Channels:** System.Threading.Channels for thread-safe event broadcasting across the application.

---

## 17. INSTALLER & SETUP ARCHITECTURE
*   **Portable Mode:** Runs directly from `C:\Hostix\bin`. No registry entries.
*   **Installer Mode:** (Inno Setup / WiX)
    *   **Admin Privilege Flow:** Automatically requests UAC on first run to configure `hosts` and CA.
    *   **SSL CA Installation:** Injects the Hostix Root CA into the Windows Trusted Root Store.
    *   **PATH Integration:** Optionally adds `C:\Hostix\bin\php` and `composer` to system PATH.
    *   **Service Registration:** Registers Nginx/MySQL as Windows Services if "Run at Startup" is enabled.

---

## 18. ENTERPRISE TESTING ARCHITECTURE
*   **Unit Tests:** Testing individual logic in `Hostix.Modules` (e.g., .env parsing).
*   **Integration Tests:** End-to-end VHost generation and `hosts` file modification (using file-system mocks).
*   **Runtime Recovery Tests:** Simulating process crashes and verifying the `RuntimeEngine`'s restart logic.
*   **Mock Runtime:** A sandbox environment where we simulate Nginx/MySQL binaries to test UI responsiveness.

---

## 19. CI/CD & BUILD PIPELINE
*   **GitHub Actions:**
    *   **Build:** Compiles .NET 8 source on every commit.
    *   **Test:** Runs the full test suite.
    *   **Package:** Automates the creation of `Hostix_Portable.zip` and `Hostix_Setup.exe`.
*   **Versioning:** Semantic Versioning (SemVer) with auto-incrementing build numbers.

---

## 20. COMMAND EXECUTION SANDBOX
Hostix implements a "Safe Shell" to prevent malicious activity:
*   **Command Whitelist:** Only specific binaries (php, nginx, git, npm, composer) are allowed.
*   **Path Sanitization:** Rejects commands that attempt to access files outside `C:\Hostix`.
*   **Validation Wrapper:** Every command is wrapped in a validation layer that checks permissions before `Process.Start()`.

---

## 21. MULTI-RUNTIME VERSION MAPPING
*   **PHP/Node Selector:** Users can drop any PHP version into `bin/php` and select it via the Website Card.
*   **Isolation Logic:** Hostix generates unique `fastcgi_pass` ports for each PHP version, allowing PHP 7.4 and 8.3 to run concurrently without conflict.

---

## 22. AI-AGENT DEVELOPMENT LAYER
The architecture is specifically designed to be "Agent-Friendly":
*   **Auto-Diagnostics:** Hostix generates a JSON-based system state report that an AI agent can read to identify misconfigurations.
*   **Template Generators:** AI agents can generate new project templates (SaaS, API) by dropping files into the `templates/` folder.
*   **Code-First Config:** All Nginx/Apache configs are based on JSON-mappable templates, allowing agents to generate complex VHosts without syntax errors.

---

## 23. ADVANCED RECOVERY & FAILOVER
*   **Startup Safe Mode:** If Hostix fails to start 3 times, it offers to start without loading project plugins.
*   **Config Auto-Rollback:** If a config change breaks Nginx, the `ConfigManager` restores the last known good `.bak` automatically.
*   **Corrupted DB Recovery:** Hostix maintains a daily backup of the `hostix.db` for instant restoration.

---

## 24. FINAL ENGINEERING POSITIONING
Hostix is a **Modular Windows-Native Local Hosting Operating System**. It is not a wrapper; it is an **orchestration runtime** that transforms a standard Windows PC into a professional-grade web production environment.

---
**Status:** Enterprise Engineering Architecture Complete. Ready for Implementation.
