using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutomateTask.Utils;

/// <summary>
/// 用单次系统定时器等待绝对时刻，兼顾系统校时和休眠期间经过的时间
/// </summary>
public sealed class ScheduleTimer : WaitHandle
{
    private readonly RegisteredWaitHandle registration;

    /// <summary>
    /// 注册异步到期回调，不为每个业务任务创建计时器
    /// </summary>
    public ScheduleTimer(Action elapsed)
    {
        SafeWaitHandle = CreateWaitableTimer(IntPtr.Zero, false, null);

        if (SafeWaitHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建任务定时器");

        registration = ThreadPool.RegisterWaitForSingleObject(this, (_, _) => elapsed(),
            null, Timeout.Infinite, false);
    }

    /// <summary>
    /// 更新最近截止时刻，无计划时取消计时，仅等待业务变更
    /// </summary>
    public void Set(DateTimeOffset? due)
    {
        if (due == null)
        {
            if (!CancelWaitableTimer(SafeWaitHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取消任务定时器");

            return;
        }

        // 业务使用本机时间，只有 Windows 定时器参数转换为 FILETIME
        var fileTime = due.Value.ToFileTime();

        if (!SetWaitableTimer(SafeWaitHandle, in fileTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置任务定时器");
    }

    /// <summary>
    /// 注销等待并释放系统句柄
    /// </summary>
    protected override void Dispose(bool explicitDisposing)
    {
        if (explicitDisposing)
            registration.Unregister(null);

        base.Dispose(explicitDisposing);
    }

    /// <summary>
    /// 创建未命名、自动复位的系统定时器
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimer(IntPtr attributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset, string? name);

    /// <summary>
    /// 按绝对 FILETIME 设置单次到期，不主动唤醒休眠中的电脑
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long due, int period,
        IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    /// <summary>
    /// 取消尚未到期的计时
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
