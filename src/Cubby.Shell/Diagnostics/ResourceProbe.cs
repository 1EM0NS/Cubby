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
    double UptimeMinutes,
    int Threads);

/// <summary>
/// 常驻稳定性（验收标准 A7）的观测点：句柄数、GDI / USER 对象数与线程数是长跑最容易泄漏的几项，
/// 且任务管理器默认看不到，所以由程序自己输出，便于挂机前后各取一次做对比。
///
/// **只读**：全走 <c>Process</c> 与 <c>GetGuiResources</c> 查询，不做分配。
/// 挂机采样（<c>--soak</c>）会反复调用它，采样本身的开销不能污染结论。
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
            (DateTime.Now - process.StartTime).TotalMinutes,
            process.Threads.Count);
    }
}