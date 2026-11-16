using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutomateTask.Models;
using AutomateTask.Desktop.Controls;
using AutomateTask.Utils;

namespace AutomateTask.Desktop;

/// <summary>
/// 在用户会话中显示 WPF 托盘菜单并接收后台通知
/// </summary>
internal sealed class TrayApplication : Application
{
    private readonly string root;
    private readonly string receiptPath;
    private readonly NotifyIcon icon;
    private readonly ContextMenu menu = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> receipts = [];
    private DateOnly receiptDate = DateOnly.FromDateTime(DateTime.Today);
    private bool fetching;

    /// <summary>
    /// 加载托盘资源与本用户当天回执，不访问业务数据库
    /// </summary>
    public TrayApplication(string root)
    {
        this.root = root;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.Source = new Uri("/AutomateTask.Desktop;component/Styles/TrayMenu.xaml", UriKind.Relative);
        receiptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomateTask", NotificationProtocol.PipeName(root) + ".txt");
        icon = new NotifyIcon
        {
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/AutomateTask.Desktop;component/Assets/Logo.ico")),
            ToolTipText = "自动化任务：等待服务",
            ContextMenu = menu
        };

        var openItem = new MenuItem { Header = "打开管理页面" };
        openItem.Click += OpenManagement;
        var exitItem = new MenuItem { Header = "退出提醒（后台任务继续）" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        icon.DoubleClick += OpenManagement;
        timer.Tick += Poll;

        try
        {
            if (File.Exists(receiptPath) && new FileInfo(receiptPath).Length < NotificationProtocol.MaxMessageBytes)
            {
                var lines = File.ReadAllLines(receiptPath);

                if (lines.FirstOrDefault() == receiptDate.ToString("yyyy-MM-dd"))
                    receipts.UnionWith(lines.Skip(1).Where(id => id.Length is > 0 and <= 150).Take(512));
            }
        }
        catch (IOException)
        {
            icon.ToolTipText = "自动化任务：通知回执不可读";
        }
        catch (UnauthorizedAccessException)
        {
            icon.ToolTipText = "自动化任务：通知回执无读取权限";
        }
    }

    /// <summary>
    /// 启动托盘与十秒轮询，不要求主程序已连接
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        icon.Show();
        timer.Start();
    }

    /// <summary>
    /// 有界读取当天通知，系统通知提交成功并记录回执后再确认接收
    /// </summary>
    private async void Poll(object? sender, EventArgs e)
    {
        if (fetching || stopping.IsCancellationRequested)
            return;

        fetching = true;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            // 只申请数据读写权限，避免额外要求创建管道实例的权限
            using var pipe = new NamedPipeClientStream(".", NotificationProtocol.PipeName(root),
                PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous, HandleInheritability.None);
            await pipe.ConnectAsync(deadline.Token);
            var today = DateOnly.FromDateTime(DateTime.Today);

            if (receiptDate != today)
            {
                receiptDate = today;
                receipts.Clear();
            }

            await NotificationProtocol.Write(pipe, receipts, deadline.Token);
            var notification = await NotificationProtocol.Read<TaskNotification>(pipe, deadline.Token);

            if (stopping.IsCancellationRequested)
                return;

            icon.ToolTipText = "自动化任务：已连接服务";

            if (notification == null)
            {
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
                return;
            }

            if (notification.Date != today || string.IsNullOrWhiteSpace(notification.Id) || notification.Id.Length > 150 ||
                string.IsNullOrWhiteSpace(notification.Title) || notification.Title.Length > 60 ||
                string.IsNullOrWhiteSpace(notification.Message) || notification.Message.Length > 200)
                throw new InvalidDataException("仅接受当天有效通知");

            if (!receipts.Contains(notification.Id))
            {
                icon.ShowBalloonTip(notification.Title, notification.Message, notification.Success);
                receipts.Add(notification.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                var temp = receiptPath + ".tmp";
                File.WriteAllLines(temp, [today.ToString("yyyy-MM-dd"), .. receipts], new UTF8Encoding(false));
                File.Move(temp, receiptPath, true);
            }

            // 写入本用户回执后才确认，保持与后台现有协议一致
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or Win32Exception)
        {
            if (!stopping.IsCancellationRequested)
                icon.ToolTipText = "自动化任务：等待服务或通知通道恢复";
        }
        finally
        {
            fetching = false;
        }
    }

    /// <summary>
    /// 由用户点击菜单或双击图标后打开管理页面
    /// </summary>
    private void OpenManagement(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "Open-Management.ps1")}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception)
        {
            icon.ShowBalloonTip("自动化任务", "管理页面未打开，请检查服务目录和打开页面脚本", false);
        }
    }

    /// <summary>
    /// 退出时关闭菜单并释放托盘和轮询资源，后台服务继续运行
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop();
        stopping.Cancel();
        menu.IsOpen = false;
        icon.Dispose();
        stopping.Dispose();
        base.OnExit(e);
    }
}
