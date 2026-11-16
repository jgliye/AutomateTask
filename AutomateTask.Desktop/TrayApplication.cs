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
internal sealed class TrayApplication : Application, IDisposable
{
    private readonly string root;
    private readonly string receiptPath;
    private readonly NotifyIcon icon;
    private readonly ContextMenu menu = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> receipts = [];
    private DateOnly receiptDate = DateOnly.FromDateTime(DateTime.Today);
    private Task? listening;
    private bool disposed;

    /// <summary>
    /// 退出前等待通知连接结束
    /// </summary>
    public Task Completion => listening ?? Task.CompletedTask;

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

        var openItem = new MenuItem { Header = "管理页面" };
        openItem.Click += OpenManagement;
        var exitItem = new MenuItem { Header = "退出提醒" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        icon.DoubleClick += OpenManagement;

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
    /// 建立通知连接，无启用的通知任务时由首次状态决定退出
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        listening = Task.Run(ListenAsync);
    }

    /// <summary>
    /// 持续等待管道推送，退出或服务断开后结束连接
    /// </summary>
    private async Task ListenAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", NotificationProtocol.PipeName(root),
                PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous, HandleInheritability.None);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                await NotificationProtocol.Write(pipe, receipts, deadline.Token).ConfigureAwait(false);
            }

            while (!stopping.IsCancellationRequested)
            {
                // 空闲读取只受退出取消控制，不使用状态轮询或空闲超时
                var status = await NotificationProtocol.Read<NotificationStatus>(pipe, stopping.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("通知状态为空");
                var keepRunning = await Dispatcher.InvokeAsync(() => ApplyStatus(status),
                    DispatcherPriority.Normal, stopping.Token).Task.ConfigureAwait(false);

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);

                if (!keepRunning)
                {
                    await Dispatcher.InvokeAsync(() => Shutdown(),
                        DispatcherPriority.Normal, stopping.Token).Task.ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // 用户退出时取消挂起的连接与读取
        }
        catch (Exception ex)
        {
            Trace.TraceError("桌面通知连接失败：{0}", ex);

            try
            {
                await Dispatcher.InvokeAsync(() => Shutdown(1)).Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted)
            {
                // 应用已退出，无需再次请求关闭
            }
        }
    }

    /// <summary>
    /// 在界面线程处理通知，返回是否仍需要运行桌面端
    /// </summary>
    private bool ApplyStatus(NotificationStatus status)
    {
        if (!status.DesktopRequired)
        {
            menu.IsOpen = false;
            icon.Hide();
            return false;
        }

        icon.ToolTipText = "自动化任务：已连接服务";

        if (!icon.IsIconVisible)
            icon.Show();

        var today = DateOnly.FromDateTime(DateTime.Today);

        if (receiptDate != today)
        {
            receiptDate = today;
            receipts.Clear();
        }

        var notification = status.Notification;

        if (notification == null)
            return true;

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

        return true;
    }

    /// <summary>
    /// 读取服务地址，通过默认浏览器打开管理页面
    /// </summary>
    private void OpenManagement(object? sender, RoutedEventArgs e)
    {
        try
        {
            var configuration = SerializeHelper.Load<Dictionary<string, JsonElement>>(
                Path.Combine(root, "appsettings.Production.json"));
            var address = configuration.TryGetValue("Urls", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

            if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp || url.Host != "127.0.0.1")
                throw new InvalidDataException("请在 appsettings.Production.json 中配置有效的本机服务地址");

            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
        {
            var message = "管理页面未打开：" + ex.Message;
            icon.ShowBalloonTip("自动化任务", message.Length > 200 ? message[..200] : message, false);
        }
    }

    /// <summary>
    /// 退出提醒时取消连接并隐藏图标，后台任务继续运行
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        stopping.Cancel();
        menu.IsOpen = false;
        icon.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 通知连接结束后在主线程释放托盘资源
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        icon.Dispose();
        stopping.Dispose();
    }
}
