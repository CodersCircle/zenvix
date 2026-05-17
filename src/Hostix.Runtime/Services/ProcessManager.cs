using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Hostix.Runtime.Services
{
    public class ProcessDiagnostics
    {
        public string FileName { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string StdOut { get; set; } = string.Empty;
        public string StdErr { get; set; } = string.Empty;
        public int ExitCode { get; set; } = -1;
        public bool TimedOut { get; set; }
    }

    public interface IProcessManager
    {
        Task<Process> StartProcessAsync(string fileName, string arguments,
            string? workingDirectory = null, bool useShellExecute = false);
        
        Task<ProcessDiagnostics> RunCommandAsync(string fileName, string arguments,
            string? workingDirectory = null, int timeoutMs = 10000);

        void StopProcess(int processId);
        bool IsProcessRunning(int processId);
        
        /// <summary>Finds which PID is using a local port. Returns null if free.</summary>
        int? GetPidUsingPort(int port);
    }

    public class ProcessManager : IProcessManager
    {
        public async Task<Process> StartProcessAsync(
            string fileName, string arguments,
            string? workingDirectory = null, bool useShellExecute = false)
        {
            if (!File.Exists(fileName))
                throw new FileNotFoundException($"Runtime binary not found: {fileName}", fileName);

            var workDir = workingDirectory ?? Path.GetDirectoryName(fileName)
                          ?? AppDomain.CurrentDomain.BaseDirectory;

            var startInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                UseShellExecute        = useShellExecute,
                CreateNoWindow         = true,
                RedirectStandardOutput = !useShellExecute,
                RedirectStandardError  = !useShellExecute,
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            var stdout = new StringBuilder();

            if (!useShellExecute)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            }

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                if (!useShellExecute)
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }

                // Initial crash check (600ms) - only for processes we expect to keep running
                await Task.Delay(600);
                if (process.HasExited)
                {
                    var diagnostics = new ProcessDiagnostics
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = workDir,
                        StdOut = stdout.ToString().Trim(),
                        StdErr = stderr.ToString().Trim(),
                        ExitCode = process.ExitCode
                    };
                    
                    var errorMsg = $"Process crashed immediately (Code {process.ExitCode}).\nSTDERR: {diagnostics.StdErr}";
                    var ex = new InvalidOperationException(errorMsg);
                    ex.Data["Diagnostics"] = diagnostics;
                    throw ex;
                }

                return process;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
        }

        public async Task<ProcessDiagnostics> RunCommandAsync(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 10000)
        {
            var workDir = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppDomain.CurrentDomain.BaseDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            var diagnostics = new ProcessDiagnostics
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir
            };

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!process.Start()) throw new Exception($"Failed to start: {fileName}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                diagnostics.ExitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                diagnostics.TimedOut = true;
            }

            diagnostics.StdOut = stdout.ToString().Trim();
            diagnostics.StdErr = stderr.ToString().Trim();
            return diagnostics;
        }

        public void StopProcess(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
            }
            catch (ArgumentException) { }
            catch (Exception ex) { Log.Error(ex, "Error stopping PID {Pid}", processId); }
        }

        public bool IsProcessRunning(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch { return false; }
        }

        public int? GetPidUsingPort(int port)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = $"-ano -p tcp",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return null;

                var output = process.StandardOutput.ReadToEnd();
                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    // Use exact match to avoid :80 matching :8001, :8080 etc.
                    var portPattern = $":{port} ";
                    if (line.Contains(portPattern) && line.Contains("LISTENING"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            var pidStr = parts[parts.Length - 1];
                            if (int.TryParse(pidStr, out var pid)) return pid;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex, "Error checking port {Port}", port); }
            return null;
        }
    }
}
