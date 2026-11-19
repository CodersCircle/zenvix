using System;
using System.Runtime.InteropServices;

namespace Zenvix.ViewModels.Services
{
    /// <summary>Cheap whole-machine CPU and memory readings (no performance counters, no WMI).</summary>
    public sealed class SystemMetrics
    {
        private ulong _lastIdle, _lastKernel, _lastUser;

        /// <summary>CPU busy percentage since the previous call (0–100).</summary>
        public double SampleCpuPercent()
        {
            if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
            ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);

            ulong dIdle = idle - _lastIdle, dKernel = kernel - _lastKernel, dUser = user - _lastUser;
            bool first = _lastKernel == 0;
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            if (first) return 0;

            ulong total = dKernel + dUser; // kernel time includes idle time
            return total == 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }

        /// <summary>Used and total physical memory in GB.</summary>
        public (double UsedGb, double TotalGb) SampleMemory()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
            const double Gb = 1024d * 1024 * 1024;
            return ((status.ullTotalPhys - status.ullAvailPhys) / Gb, status.ullTotalPhys / Gb);
        }

        private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
