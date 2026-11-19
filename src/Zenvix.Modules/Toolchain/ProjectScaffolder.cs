using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zenvix.Core.Models;

namespace Zenvix.Modules.Toolchain
{
    public sealed class ScaffoldRequest
    {
        public string Framework { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>Folder name (safe slug); the project is created at ParentDir\Slug.</summary>
        public string Slug { get; init; } = "";
        public string ParentDir { get; init; } = "";
        public string Domain { get; init; } = "";
        public bool Https { get; init; }
        /// <summary>MySQL to use for PHP apps; null = use SQLite (Laravel) .</summary>
        public int? MySqlPort { get; init; }
        public string FilamentVersion { get; init; } = "5";
        /// <summary>Dev ports already assigned to other projects (so two Vite apps don't both get 5173).</summary>
        public IReadOnlyCollection<int> ReservedPorts { get; init; } = Array.Empty<int>();
        public string TargetPath => Path.Combine(ParentDir, Slug);
    }

    public sealed class ScaffoldResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public ProjectType Type { get; init; } = ProjectType.PHP;
        /// <summary>For Node / mobile apps: command that starts the dev server (run inside the project folder).</summary>
        public string? DevCommand { get; init; }
        public int? DevPort { get; init; }
        public string? AdminPath { get; init; }
        public string? AdminEmail { get; init; }
        public string? AdminPassword { get; init; }
        /// <summary>True when the project is served by Nginx/PHP (has a web root), false for dev-server apps.</summary>
        public bool ServedByWebServer => DevCommand == null;
    }

    public interface IProjectScaffolder
    {
        /// <summary>Frameworks that need a real generator (others use Zenvix's built-in starter templates).</summary>
        bool Handles(string framework);
        Task<ScaffoldResult> CreateAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct = default);
    }

    /// <summary>
    /// Creates real projects with each framework's official generator, using only Zenvix-managed tools.
    /// Every step's exit code is checked; an existing non-empty folder is never touched.
    /// </summary>
    public sealed class ProjectScaffolder : IProjectScaffolder
    {
        private readonly IToolchainManager _tools;
        public ProjectScaffolder(IToolchainManager tools) => _tools = tools;

        private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        {
            "Laravel", "FilamentPHP", "Laravel API", "WordPress",
            "React", "Vue", "Next.js", "React Admin", "Vue Admin", "Express (Node.js)",
            "Flutter", "React Native", "Ionic",
        };

        public bool Handles(string framework) => Supported.Contains(framework);

        public async Task<ScaffoldResult> CreateAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct = default)
        {
            try
            {
                if (Directory.Exists(req.TargetPath) && Directory.EnumerateFileSystemEntries(req.TargetPath).Any())
                    return Fail($"The folder {req.TargetPath} already exists and is not empty. Pick another name or folder – Zenvix never overwrites existing files.");
                Directory.CreateDirectory(req.ParentDir);
                if (Directory.Exists(req.TargetPath)) Directory.Delete(req.TargetPath); // empty – generators want to create it

                return req.Framework switch
                {
                    "Laravel" or "Laravel API" or "FilamentPHP" => await LaravelAsync(req, log, ct),
                    "WordPress" => await WordPressAsync(req, log, ct),
                    "React" or "Vue" or "React Admin" or "Vue Admin" => await ViteAsync(req, log, ct),
                    "Next.js" => await NextAsync(req, log, ct),
                    "Express (Node.js)" => await ExpressAsync(req, log, ct),
                    "Flutter" => await FlutterAsync(req, log, ct),
                    "React Native" => await ExpoAsync(req, log, ct),
                    "Ionic" => await IonicAsync(req, log, ct),
                    _ => Fail($"No generator for {req.Framework}."),
                };
            }
            catch (OperationCanceledException) { return Fail("Cancelled."); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        // ───────────────────────── PHP ─────────────────────────

        private async Task<ScaffoldResult> LaravelAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Composer, log, ct)) return Fail("Composer is not available.");

            log.Report("Creating Laravel project (downloads packages, 1-3 minutes) ...");
            var r = await _tools.RunAsync($"composer create-project laravel/laravel \"{req.Slug}\" --prefer-dist --no-interaction", req.ParentDir, log, TimeSpan.FromMinutes(20), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "artisan"))) return Fail("composer create-project failed – see the log above.");

            var path = req.TargetPath;
            var env = Path.Combine(path, ".env");
            SetEnv(env, "APP_NAME", $"\"{req.Name.Replace("\"", "")}\"");
            SetEnv(env, "APP_URL", $"{(req.Https ? "https" : "http")}://{req.Domain}");

            if (req.MySqlPort is int port)
            {
                var db = req.Slug.Replace("-", "_");
                log.Report($"Using Zenvix MySQL on port {port}, database \"{db}\".");
                SetEnv(env, "DB_CONNECTION", "mysql");
                SetEnv(env, "DB_HOST", "127.0.0.1");
                SetEnv(env, "DB_PORT", port.ToString());
                SetEnv(env, "DB_DATABASE", db);
                SetEnv(env, "DB_USERNAME", "root");
                SetEnv(env, "DB_PASSWORD", "");
                var create = await _tools.RunAsync(
                    $"php -r \"$p=new PDO('mysql:host=127.0.0.1;port={port}','root','');$p->exec('CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci');\"",
                    path, log, TimeSpan.FromMinutes(1), ct);
                if (!create.Ok) return Fail("Could not create the MySQL database – is Zenvix MySQL running?");
                if (!(await Artisan("migrate --force", path, log, ct)).Ok) return Fail("php artisan migrate failed.");
            }
            else log.Report("MySQL is not running – the project uses SQLite (database/database.sqlite), ready to go.");

            string? adminPath = null, email = null, password = null;

            if (req.Framework == "Laravel API")
            {
                if (!(await Artisan("install:api --no-interaction", path, log, ct)).Ok) return Fail("php artisan install:api failed.");
            }

            if (req.Framework == "FilamentPHP")
            {
                var major = Regex.Match(req.FilamentVersion, @"\d+").Value is { Length: > 0 } m ? m : "5";
                log.Report($"Installing Filament {major}.x admin panel ...");
                var fr = await _tools.RunAsync($"composer require \"filament/filament:^{major}.0\" -W --no-interaction", path, log, TimeSpan.FromMinutes(15), ct);
                if (!fr.Ok) return Fail($"composer require filament/filament:^{major}.0 failed.");
                if (!(await Artisan("filament:install --panels --no-interaction", path, log, ct)).Ok) return Fail("filament:install failed.");

                email = "admin@zenvix.test";
                password = RandomPassword();
                var user = await Artisan($"make:filament-user --name=Admin --email={email} --password={password} --no-interaction", path, log, ct);
                if (!user.Ok) return Fail("Creating the Filament admin user failed.");
                adminPath = "/admin";
                log.Report($"✔ Admin panel: /admin  ·  {email}  ·  {password}");
            }

            log.Report("✔ Laravel project ready.");
            return new ScaffoldResult { Success = true, Type = ProjectType.Laravel, Message = "Laravel project created.", AdminPath = adminPath, AdminEmail = email, AdminPassword = password };
        }

        private Task<CommandResult> Artisan(string args, string path, IProgress<string> log, CancellationToken ct) =>
            _tools.RunAsync($"php artisan {args}", path, log, TimeSpan.FromMinutes(10), ct);

        private async Task<ScaffoldResult> WordPressAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Php, log, ct)) return Fail("PHP is not available.");
            log.Report("Downloading WordPress ...");
            var zip = Path.Combine(Path.GetTempPath(), $"wordpress-{Guid.NewGuid():N}.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync("https://wordpress.org/latest.zip", ct), ct);
            var tmp = zip + ".x";
            ZipFile.ExtractToDirectory(zip, tmp);
            Directory.Move(Path.Combine(tmp, "wordpress"), req.TargetPath);
            Directory.Delete(tmp, true);
            File.Delete(zip);

            var db = req.Slug.Replace("-", "_");
            var sample = File.ReadAllText(Path.Combine(req.TargetPath, "wp-config-sample.php"));
            var port = req.MySqlPort ?? 3306;
            var config = sample
                .Replace("database_name_here", db)
                .Replace("username_here", "root")
                .Replace("password_here", "")
                .Replace("'DB_HOST', 'localhost'", $"'DB_HOST', '127.0.0.1:{port}'");
            // Unique salts instead of the placeholder phrase.
            config = Regex.Replace(config, "put your unique phrase here", _ => RandomPassword(48));
            File.WriteAllText(Path.Combine(req.TargetPath, "wp-config.php"), config);

            if (req.MySqlPort != null)
            {
                await _tools.RunAsync(
                    $"php -r \"$p=new PDO('mysql:host=127.0.0.1;port={port}','root','');$p->exec('CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci');\"",
                    req.TargetPath, log, TimeSpan.FromMinutes(1), ct);
                log.Report($"✔ Database \"{db}\" created. Open the site to finish the 1-minute WordPress install.");
            }
            else log.Report("Start MySQL in Services, then open the site to finish the WordPress install.");

            return new ScaffoldResult { Success = true, Type = ProjectType.WordPress, Message = "WordPress downloaded and configured.", AdminPath = "/wp-admin" };
        }

        // ───────────────────────── JavaScript ─────────────────────────

        private async Task<ScaffoldResult> ViteAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Node, log, ct)) return Fail("Node.js is not available.");
            var template = req.Framework switch { "Vue" => "vue", "React Admin" => "react-ts", "Vue Admin" => "vue-ts", _ => "react" };
            log.Report($"Creating Vite + {template} project ...");
            var r = await _tools.RunAsync($"npm create vite@latest \"{req.Slug}\" -- --template {template} --no-interactive", req.ParentDir, log, TimeSpan.FromMinutes(10), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "package.json"))) return Fail("npm create vite failed.");

            var extra = req.Framework switch
            {
                "React Admin" => "react-admin ra-data-json-server",
                "Vue Admin" => "vue-router",
                _ => null,
            };
            log.Report("Installing packages ...");
            if (!(await _tools.RunAsync("npm install --no-fund --no-audit", req.TargetPath, log, TimeSpan.FromMinutes(15), ct)).Ok) return Fail("npm install failed.");
            if (extra != null && !(await _tools.RunAsync($"npm install {extra} --no-fund --no-audit", req.TargetPath, log, TimeSpan.FromMinutes(15), ct)).Ok)
                return Fail($"npm install {extra} failed.");

            if (req.Framework == "React Admin") WriteReactAdminStarter(req);
            if (req.Framework == "Vue Admin") WriteVueAdminStarter(req);

            var port = FreePort(5173, req.ReservedPorts);
            log.Report("✔ Project ready. Click Run to start the dev server (hot reload).");
            return new ScaffoldResult
            {
                Success = true, Type = req.Framework.StartsWith("Vue") ? ProjectType.Vue : ProjectType.React,
                Message = "Vite project created.", DevPort = port,
                DevCommand = $"npm run dev -- --port {port} --strictPort --host 127.0.0.1",
            };
        }

        private async Task<ScaffoldResult> NextAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Node, log, ct)) return Fail("Node.js is not available.");
            log.Report("Creating Next.js project (TypeScript, Tailwind, App Router) ...");
            var r = await _tools.RunAsync(
                $"npx -y create-next-app@latest \"{req.Slug}\" --ts --tailwind --eslint --app --use-npm --import-alias \"@/*\" --yes --disable-git",
                req.ParentDir, log, TimeSpan.FromMinutes(20), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "package.json"))) return Fail("create-next-app failed.");
            var port = FreePort(3000, req.ReservedPorts);
            return new ScaffoldResult { Success = true, Type = ProjectType.React, Message = "Next.js project created.", DevPort = port, DevCommand = $"npm run dev -- -p {port}" };
        }

        private async Task<ScaffoldResult> ExpressAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Node, log, ct)) return Fail("Node.js is not available.");
            var path = req.TargetPath;
            Directory.CreateDirectory(path);
            var port = FreePort(4000, req.ReservedPorts);
            File.WriteAllText(Path.Combine(path, "package.json"),
                "{\n  \"name\": \"" + req.Slug + "\",\n  \"version\": \"1.0.0\",\n  \"type\": \"module\",\n  \"scripts\": {\n" +
                "    \"dev\": \"node --watch index.js\",\n    \"start\": \"node index.js\"\n  }\n}\n");
            File.WriteAllText(Path.Combine(path, "index.js"),
$@"import express from 'express';

const app = express();
app.use(express.json());

// In-memory demo data – replace with your database.
const todos = [{{ id: 1, title: 'Try Zenvix', done: false }}];

app.get('/', (req, res) => res.json({{ name: '{req.Name.Replace("'", "")}', status: 'ok', endpoints: ['/api/todos'] }}));
app.get('/api/todos', (req, res) => res.json(todos));
app.post('/api/todos', (req, res) => {{
  const todo = {{ id: todos.length + 1, title: req.body.title ?? 'Untitled', done: false }};
  todos.push(todo);
  res.status(201).json(todo);
}});

const port = process.env.PORT || {port};
app.listen(port, () => console.log(`API running on http://localhost:${{port}}`));
");
            File.WriteAllText(Path.Combine(path, ".gitignore"), "node_modules\n.env\n");
            if (!(await _tools.RunAsync("npm install express --no-fund --no-audit", path, log, TimeSpan.FromMinutes(10), ct)).Ok) return Fail("npm install express failed.");
            return new ScaffoldResult { Success = true, Type = ProjectType.NodeJS, Message = "Express API created.", DevPort = port, DevCommand = $"set PORT={port}&& npm run dev" };
        }

        // ───────────────────────── Mobile ─────────────────────────

        private async Task<ScaffoldResult> FlutterAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Flutter, log, ct)) return Fail("Flutter SDK is not available.");
            var dartName = Regex.Replace(req.Slug.Replace("-", "_"), "^[^a-z]+", "");
            if (dartName.Length == 0) dartName = "app";
            log.Report("Creating Flutter app ...");
            var r = await _tools.RunAsync($"flutter create --org com.zenvix --project-name {dartName} \"{req.Slug}\"", req.ParentDir, log, TimeSpan.FromMinutes(15), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "pubspec.yaml"))) return Fail("flutter create failed.");
            var port = FreePort(8090, req.ReservedPorts);
            log.Report("✔ Flutter app ready. Run opens it in your browser; use Android Studio / a device for mobile builds.");
            return new ScaffoldResult { Success = true, Type = ProjectType.Flutter, Message = "Flutter app created.", DevPort = port, DevCommand = $"flutter run -d web-server --web-port {port} --web-hostname 127.0.0.1" };
        }

        private async Task<ScaffoldResult> ExpoAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Node, log, ct)) return Fail("Node.js is not available.");
            log.Report("Creating React Native (Expo) app ...");
            var r = await _tools.RunAsync($"npx -y create-expo-app@latest \"{req.Slug}\" --yes", req.ParentDir, log, TimeSpan.FromMinutes(20), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "package.json"))) return Fail("create-expo-app failed.");
            var port = FreePort(8081, req.ReservedPorts);
            log.Report("✔ React Native app ready. Run starts Expo: scan the QR with Expo Go on your phone, or press w for web.");
            return new ScaffoldResult { Success = true, Type = ProjectType.ReactNative, Message = "React Native (Expo) app created.", DevPort = port, DevCommand = $"npx expo start --port {port}" };
        }

        private async Task<ScaffoldResult> IonicAsync(ScaffoldRequest req, IProgress<string> log, CancellationToken ct)
        {
            if (!await _tools.EnsureAsync(ToolKind.Node, log, ct)) return Fail("Node.js is not available.");
            log.Report("Creating Ionic (React + Capacitor) app ...");
            var r = await _tools.RunAsync(
                $"npx -y @ionic/cli@latest start \"{req.Slug}\" tabs --type=react --capacitor --no-interactive --no-git",
                req.ParentDir, log, TimeSpan.FromMinutes(20), ct);
            if (!r.Ok || !File.Exists(Path.Combine(req.TargetPath, "package.json"))) return Fail("ionic start failed.");
            var port = FreePort(8100, req.ReservedPorts);
            return new ScaffoldResult { Success = true, Type = ProjectType.Ionic, Message = "Ionic app created.", DevPort = port, DevCommand = $"npx -y @ionic/cli@latest serve --port {port} --no-open" };
        }

        // ───────────────────────── starters ─────────────────────────

        private static void WriteReactAdminStarter(ScaffoldRequest req)
        {
            File.WriteAllText(Path.Combine(req.TargetPath, "src", "App.tsx"),
@"import { Admin, Resource, ListGuesser, EditGuesser, ShowGuesser } from 'react-admin';
import jsonServerProvider from 'ra-data-json-server';

// Demo REST API – point this at your own backend (e.g. a Laravel API project).
const dataProvider = jsonServerProvider('https://jsonplaceholder.typicode.com');

export default function App() {
  return (
    <Admin dataProvider={dataProvider} title=""" + req.Name.Replace("\"", "") + @""">
      <Resource name=""users"" list={ListGuesser} edit={EditGuesser} show={ShowGuesser} />
      <Resource name=""posts"" list={ListGuesser} edit={EditGuesser} show={ShowGuesser} />
      <Resource name=""comments"" list={ListGuesser} />
    </Admin>
  );
}
");
            // react-admin brings its own styles; drop Vite's demo CSS import.
            var main = Path.Combine(req.TargetPath, "src", "main.tsx");
            if (File.Exists(main)) File.WriteAllText(main, File.ReadAllText(main).Replace("import './index.css'\n", ""));
        }

        private static void WriteVueAdminStarter(ScaffoldRequest req)
        {
            var src = Path.Combine(req.TargetPath, "src");
            var title = req.Name.Replace("<", "").Replace(">", "");
            File.WriteAllText(Path.Combine(src, "main.ts"),
@"import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import Dashboard from './pages/Dashboard.vue'
import Users from './pages/Users.vue'
import './admin.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: Dashboard },
    { path: '/users', component: Users },
  ],
})

createApp(App).use(router).mount('#app')
");
            File.WriteAllText(Path.Combine(src, "App.vue"),
$@"<template>
  <div class=""layout"">
    <aside>
      <h1>{title}</h1>
      <RouterLink to=""/"">Dashboard</RouterLink>
      <RouterLink to=""/users"">Users</RouterLink>
    </aside>
    <main><RouterView /></main>
  </div>
</template>
");
            Directory.CreateDirectory(Path.Combine(src, "pages"));
            File.WriteAllText(Path.Combine(src, "pages", "Dashboard.vue"),
@"<script setup lang=""ts"">
const stats = [
  { label: 'Users', value: 1280 },
  { label: 'Orders', value: 342 },
  { label: 'Revenue', value: '$12,450' },
]
</script>

<template>
  <h2>Dashboard</h2>
  <div class=""cards"">
    <div v-for=""s in stats"" :key=""s.label"" class=""card""><span>{{ s.label }}</span><strong>{{ s.value }}</strong></div>
  </div>
</template>
");
            File.WriteAllText(Path.Combine(src, "pages", "Users.vue"),
@"<script setup lang=""ts"">
import { ref, onMounted } from 'vue'
type User = { id: number; name: string; email: string }
const users = ref<User[]>([])
// Demo API – replace with your backend.
onMounted(async () => { users.value = await (await fetch('https://jsonplaceholder.typicode.com/users')).json() })
</script>

<template>
  <h2>Users</h2>
  <table>
    <thead><tr><th>#</th><th>Name</th><th>Email</th></tr></thead>
    <tbody><tr v-for=""u in users"" :key=""u.id""><td>{{ u.id }}</td><td>{{ u.name }}</td><td>{{ u.email }}</td></tr></tbody>
  </table>
</template>
");
            File.WriteAllText(Path.Combine(src, "admin.css"),
@"* { box-sizing: border-box; }
body { margin: 0; font-family: system-ui, sans-serif; background: #f1f5f9; color: #0f172a; }
.layout { display: grid; grid-template-columns: 220px 1fr; min-height: 100vh; }
aside { background: #0f172a; color: #e2e8f0; padding: 24px 16px; display: flex; flex-direction: column; gap: 6px; }
aside h1 { font-size: 18px; margin: 0 0 18px; }
aside a { color: #cbd5e1; text-decoration: none; padding: 8px 10px; border-radius: 8px; }
aside a.router-link-exact-active { background: #4f46e5; color: #fff; }
main { padding: 32px; }
.cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 16px; }
.card { background: #fff; border-radius: 14px; padding: 18px; display: flex; flex-direction: column; gap: 6px; border: 1px solid #e2e8f0; }
.card strong { font-size: 26px; }
table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 12px; overflow: hidden; }
th, td { text-align: left; padding: 12px 16px; border-bottom: 1px solid #e2e8f0; }
");
            foreach (var f in new[] { "style.css", Path.Combine("components", "HelloWorld.vue") })
            {
                var p = Path.Combine(src, f);
                if (File.Exists(p)) File.Delete(p);
            }
        }

        // ───────────────────────── helpers ─────────────────────────

        private static ScaffoldResult Fail(string message) => new() { Success = false, Message = message };

        private static string RandomPassword(int length = 14)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            return new string(RandomNumberGenerator.GetItems<char>(chars, length));
        }

        private static int FreePort(int start, IReadOnlyCollection<int> reserved)
        {
            var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            used.UnionWith(reserved);
            var p = start;
            while (used.Contains(p)) p++;
            return p;
        }

        private static void SetEnv(string envPath, string key, string value)
        {
            if (!File.Exists(envPath)) return;
            var lines = File.ReadAllLines(envPath).ToList();
            var idx = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*#?\s*{Regex.Escape(key)}="));
            var line = $"{key}={value}";
            if (idx >= 0) lines[idx] = line; else lines.Add(line);
            File.WriteAllLines(envPath, lines);
        }
    }
}
