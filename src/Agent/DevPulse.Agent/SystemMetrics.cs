using System.Globalization;
using System.Runtime.InteropServices;

namespace DevPulse.Agent;

public sealed record MetricsSnapshot(double CpuPercent, double MemoryPercent, double DiskPercent, long UptimeSeconds);

public interface ISystemMetrics
{
    /// <summary>Le processeur se mesure sur un intervalle : le premier appel sert de référence et renvoie 0.</summary>
    MetricsSnapshot Read();
}

/// <summary>Temps processeur cumulés : la charge est la part non inactive entre deux lectures.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total)
{
    public static double UsagePercent(CpuTimes previous, CpuTimes current)
    {
        var total = current.Total - previous.Total;
        if (current.Total <= previous.Total || total == 0) return 0;
        var idle = current.Idle - previous.Idle;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}

/// <summary>Analyse des fichiers de /proc, séparée de la lecture pour être testée sur n'importe quel système.</summary>
public static class ProcParser
{
    /// <summary>Première ligne de /proc/stat : « cpu user nice system idle iowait irq softirq steal … ».</summary>
    public static CpuTimes ParseStat(string stat)
    {
        var line = stat.Split('\n').First(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        return new CpuTimes(idle, values.Aggregate(0UL, (sum, v) => sum + v));
    }

    /// <summary>Mémoire utilisée d'après MemAvailable, qui compte le cache récupérable comme disponible.</summary>
    public static double ParseMemoryPercent(string meminfo)
    {
        long Read(string key) => long.Parse(meminfo.Split('\n').First(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);

        var total = Read("MemTotal");
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - Read("MemAvailable")) / total, 0, 100);
    }

    public static long ParseUptime(string uptime) =>
        (long)double.Parse(uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
}

public static class Disk
{
    /// <summary>Occupation du volume système (« / » ou le lecteur de Windows).</summary>
    public static double UsedPercent()
    {
        var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory)! : "/";
        var drive = new DriveInfo(root);
        return drive.TotalSize == 0 ? 0 : Math.Clamp(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize, 0, 100);
    }
}

public sealed class LinuxMetrics : ISystemMetrics
{
    private CpuTimes? _previous;

    public MetricsSnapshot Read()
    {
        var current = ProcParser.ParseStat(File.ReadAllText("/proc/stat"));
        var cpu = _previous is { } previous ? CpuTimes.UsagePercent(previous, current) : 0;
        _previous = current;

        return new MetricsSnapshot(
            cpu,
            ProcParser.ParseMemoryPercent(File.ReadAllText("/proc/meminfo")),
            Disk.UsedPercent(),
            ProcParser.ParseUptime(File.ReadAllText("/proc/uptime")));
    }
}

public sealed partial class WindowsMetrics : ISystemMetrics
{
    private CpuTimes? _previous;

    public MetricsSnapshot Read()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException("GetSystemTimes a échoué.");
        // Sous Windows, le temps noyau inclut déjà le temps inactif.
        var current = new CpuTimes(idle, kernel + user);
        var cpu = _previous is { } previous ? CpuTimes.UsagePercent(previous, current) : 0;
        _previous = current;

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("GlobalMemoryStatusEx a échoué.");

        return new MetricsSnapshot(cpu, memory.MemoryLoad, Disk.UsedPercent(), Environment.TickCount64 / 1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
