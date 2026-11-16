namespace AutomateTask.Models;

/// <summary>
/// 桌面端运行需求与下一条待处理通知
/// </summary>
public sealed record NotificationStatus
{
    /// <summary>
    /// 是否存在已启用的定时通知任务，需要运行桌面端
    /// </summary>
    public required bool DesktopRequired { get; init; }

    /// <summary>
    /// 当前用户尚未确认的当天通知，没有时为空
    /// </summary>
    public TaskNotification? Notification { get; init; }
}
