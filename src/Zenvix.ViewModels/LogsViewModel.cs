using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Core.Services;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels
{
    public enum LogLevel { Info, Warning, Error, Debug }

    public sealed record LogLine(string Text, LogLevel Level);

    public sealed class LogSource
    {
        public string Name { get; init; } = "";
        public string Group { get; init; } = "";
        public string Path { get; init; } = "";
        public override string ToString() => Name;
    }

    /// <summary>Live viewer for Zenvix and runtime (Nginx, Apache, PHP, MySQL, Mailpit) log files.</summary>
    public partial class LogsViewModel : ObservableObject
    {
        private const int MaxLines = 2000;
        private readonly IDispatcherService? _dispatcher;
        private readonly IClipboardService? _clipboard;
        private readonly Timer _timer;
        private readonly string _root = AppDomain.CurrentDomain.BaseDirectory;
        private List<LogLine> _all = new();
        private long _lastLength = -1;
        private string? _lastPath;
        private int _busy;

        public ObservableCollection<LogSource> Sources { get; } = new();
        public ObservableCollection<LogLine> Lines { get; } = new();
        public string[] Levels { get; } = { "All", "Errors", "Warnings", "Info" };

        [ObservableProperty] private LogSource? _selectedSource;
        [ObservableProperty] private string _selectedLevel = "All";
        [ObservableProperty] private string _filterText = string.Empty;
        [ObservableProperty] private bool _autoRefresh = true;
        [ObservableProperty] private string _summary = "Select a log to view";
        [ObservableProperty] private int _errorCount;
        [ObservableProperty] private int _warningCount;

        public LogsViewModel() { _timer = new Timer(_ => { }); } // design-time

        public LogsViewModel(IDispatcherService dispatcher, IClipboardService clipboard)
        {
            _dispatcher = dispatcher;
            _clipboard = clipboard;
            _timer = new Timer(_ => { if (AutoRefresh) _ = LoadAsync(false); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Read files only while the Logs page is visible.</summary>
        public void SetActive(bool active)
        {
            if (active)
            {
                RefreshSources();
                _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
            else _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        [RelayCommand]
        private void RefreshSources()
        {
            var found = new List<LogSource>();
            void AddFiles(string group, string dir, string pattern, SearchOption opt = SearchOption.TopDirectoryOnly)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, pattern, opt).OrderByDescending(File.GetLastWriteTime))
                {
                    var name = group == "Zenvix" ? $"Zenvix — {Path.GetFileNameWithoutExtension(f).Replace("zenvix", "")}" : Path.GetFileName(f);
                    // Show which folder it came from (e.g. "Nginx · 1.26.0") so same-named files are distinguishable.
                    var parent = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                    if (parent.Equals("logs", StringComparison.OrdinalIgnoreCase)) parent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)) ?? "");
                    var label = group == "Zenvix" || parent.Equals(group, StringComparison.OrdinalIgnoreCase) || parent.Length == 0 ? group : $"{group} · {parent}";
                    found.Add(new LogSource { Group = label, Name = name, Path = f });
                }
            }

            AddFiles("Zenvix", Path.Combine(_root, "logs"), "zenvix*.log");
            var runtimes = Path.Combine(_root, "runtimes");
            AddFiles("Nginx", Path.Combine(runtimes, "nginx"), "*.log", SearchOption.AllDirectories);
            AddFiles("Apache", Path.Combine(runtimes, "apache"), "*.log", SearchOption.AllDirectories);
            AddFiles("PHP", Path.Combine(_root, "logs"), "php*.log");
            AddFiles("PHP", Path.Combine(runtimes, "phpfpm"), "*.log", SearchOption.AllDirectories);
            AddFiles("MySQL", Path.Combine(_root, "data", "mysql"), "*.err");
            AddFiles("Mailpit", Path.Combine(runtimes, "mailpit"), "*.log", SearchOption.AllDirectories);

            var keep = SelectedSource?.Path;
            Sources.Clear();
            foreach (var s in found) Sources.Add(s);
            SelectedSource = Sources.FirstOrDefault(s => s.Path == keep) ?? Sources.FirstOrDefault();
        }

        partial void OnSelectedSourceChanged(LogSource? value) { _lastLength = -1; _ = LoadAsync(true); }
        partial void OnSelectedLevelChanged(string value) => ApplyFilter();
        partial void OnFilterTextChanged(string value) => ApplyFilter();

        [RelayCommand]
        private Task Reload() { _lastLength = -1; return LoadAsync(true); }

        private async Task LoadAsync(bool force)
        {
            var src = SelectedSource;
            if (src == null || Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                if (!File.Exists(src.Path)) return;
                var length = new FileInfo(src.Path).Length;
                if (!force && length == _lastLength && src.Path == _lastPath) return; // unchanged – no work
                _lastLength = length; _lastPath = src.Path;

                var lines = await Task.Run(() => ReadTail(src.Path, MaxLines));
                _all = lines.Select(l => new LogLine(l, Classify(l))).ToList();
                _dispatcher?.BeginInvoke(ApplyFilter);
            }
            catch (IOException) { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        // Reads only the end of the file, shared with the writer (Serilog/nginx keep it open).
        private static List<string> ReadTail(string path, int maxLines)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int chunk = 512 * 1024;
            if (fs.Length > chunk) fs.Seek(-chunk, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (fs.Length > chunk && lines.Count > 0) lines.RemoveAt(0); // first line is probably partial
            return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }

        private static LogLevel Classify(string line)
        {
            if (line.Contains("[ERR]") || line.Contains("[FTL]") || line.Contains("[error]") || line.Contains("[emerg]") || line.Contains("[crit]")
                || line.Contains("[ERROR]") || line.Contains("Fatal") || line.Contains("Exception")) return LogLevel.Error;
            if (line.Contains("[WRN]") || line.Contains("[warn]") || line.Contains("[Warning]") || line.Contains("WARNING")) return LogLevel.Warning;
            if (line.Contains("[DBG]") || line.Contains("[debug]")) return LogLevel.Debug;
            return LogLevel.Info;
        }

        private void ApplyFilter()
        {
            IEnumerable<LogLine> q = _all;
            q = SelectedLevel switch
            {
                "Errors" => q.Where(l => l.Level == LogLevel.Error),
                "Warnings" => q.Where(l => l.Level == LogLevel.Warning),
                "Info" => q.Where(l => l.Level == LogLevel.Info),
                _ => q,
            };
            if (!string.IsNullOrWhiteSpace(FilterText))
                q = q.Where(l => l.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

            var list = q.ToList();
            Lines.Clear();
            foreach (var l in list) Lines.Add(l);
            ErrorCount = _all.Count(l => l.Level == LogLevel.Error);
            WarningCount = _all.Count(l => l.Level == LogLevel.Warning);
            Summary = SelectedSource == null ? "No log files yet" : $"{list.Count} of {_all.Count} lines";
        }

        [RelayCommand]
        private void CopyVisible()
        {
            if (Lines.Count == 0) return;
            _clipboard?.SetText(string.Join(Environment.NewLine, Lines.Select(l => l.Text)));
        }

        [RelayCommand]
        private void OpenFolder()
        {
            var path = SelectedSource?.Path;
            if (path != null && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                Process.Start("explorer.exe", $"\"{Path.Combine(_root, "logs")}\"");
        }
    }
}
