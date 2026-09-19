using System.Diagnostics;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Diagnostics;

/// <summary>本进程的资源占用快照。</summary>
public readonly record struct ResourceSample(
    double WorkingSetMb,
    double PrivateMb,
    int Handles,
    uint GdiObjects,
    uint UserObjects,
    double UptimeMinutes);

/// <summary>
/// 常驻稳定性（验收标准 A7）的观测点：句柄数与 GDI / USER 对象数是长跑最容易泄漏的三项，
/// 且任务管理器默认看不到，所以由程序自己输出，便于挂机前后各取一次做对比。
/// </summary>
public static class ResourceProbe
{
    public static ResourceSample Sample()
    {
        using var process = Process.GetCurrentProcess();

        return new ResourceSample(
            process.WorkingSet64 / (1024.0 * 1024),
            process.PrivateMemorySize64 / (1024.0 * 1024),
            process.HandleCount,
            NativeMethods.GetGuiResources(process.Handle, 0),
            NativeMethods.GetGuiResources(process.Handle, 1),
            (DateTime.Now - process.StartTime).TotalMinutes);
    }
}