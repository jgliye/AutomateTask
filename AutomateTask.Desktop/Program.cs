using System.Windows;
using AutomateTask.Utils;

namespace AutomateTask.Desktop;

/// <summary>
/// 在用户登录会话运行托盘，不能作为 Windows 服务启动
/// </summary>
internal static class Program
{
    /// <summary>
    /// 默认使用发布包中 Tray 的上级目录，也可传入服务程序的绝对目录
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var root = args.Length == 1 ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

        if (!File.Exists(Path.Combine(root, "appsettings.json")))
        {
            MessageBox.Show("请从发布包 Tray 目录启动，或传入服务程序所在目录", "自动化任务");
            return 1;
        }

        using var instance = new Mutex(true, @"Local\" + NotificationProtocol.PipeName(root), out var created);

        if (!created)
            return 0;

        using var application = new TrayApplication(root);
        var exitCode = application.Run();
        application.Completion.GetAwaiter().GetResult();
        return exitCode;
    }
}
