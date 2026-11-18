using System.Runtime.InteropServices;

namespace ShowControl.Agent;

/// <summary>CPU %, RAM and uptime from Win32, no extra packages.</summary>
public sealed class SystemStats
{
    private ulong _lastIdle, _lastKernel, _lastUser;

    public object Sample()
    {
        try { return SampleCore(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new { cpu = 0.0, ramUsedMb = 0L, ramTotalMb = 0L, uptimeSec = Environment.TickCount64 / 1000 };
        }
    }

    private object SampleCore()
    {
        double cpu = 0;
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
            ulong dIdle = idle - _lastIdle, dKernel = kernel - _lastKernel, dUser = user - _lastUser;
            ulong total = dKernel + dUser; // kernel time includes idle time
            if (_lastKernel != 0 && total > 0) cpu = Math.Round((1.0 - (double)dIdle / total) * 100.0, 1);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }

        long usedMb = 0, totalMb = 0;
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            totalMb = (long)(mem.ullTotalPhys / 1024 / 1024);
            usedMb = totalMb - (long)(mem.ullAvailPhys / 1024 / 1024);
        }

        return new
        {
            cpu = Math.Clamp(cpu, 0, 100),
            ramUsedMb = usedMb,
            ramTotalMb = totalMb,
            uptimeSec = Environment.TickCount64 / 1000,
        };
    }

    private static ulong ToUlong(FileTime ft) => ((ulong)ft.High << 32) | ft.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
