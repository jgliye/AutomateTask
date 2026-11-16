using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32.SafeHandles;
using AutomateTask.Models;
using AutomateTask.Utils;

namespace AutomateTask.Services;

/// <summary>
/// 按任务配置启停桌面端，通过长连接向各用户会话推送通知
/// </summary>
public sealed class NotificationService(ITaskService taskService, IHostApplicationLifetime applicationLifetime,
    ILogger<NotificationService> log) : BackgroundService
{
    private readonly ConcurrentDictionary<int, Channel<byte>> clients = new();
    private readonly Channel<byte> launches = CreateSignal();

    /// <summary>
    /// 订阅业务事件并维护通知连接，退出时等待所有连接任务结束
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        taskService.ConfigurationChanged += ConfigurationChanged;
        taskService.NotificationChanged += PushState;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var connections = new List<Task>();
                NamedPipeServerStream? listener = null;
                var launcher = Task.CompletedTask;

                try
                {
                    listener = CreatePipe(true);
                    launches.Writer.TryWrite(0);
                    launcher = LaunchDesktopAsync(lifetime.Token);

                    while (!lifetime.IsCancellationRequested)
                    {
                        await listener.WaitForConnectionAsync(lifetime.Token);
                        var connected = listener;

                        try
                        {
                            // 旧实例仍在时先建立下一个监听，持续保持管道名的所有权
                            listener = CreatePipe(false);
                        }
                        catch
                        {
                            connected.Dispose();
                            throw;
                        }

                        connections.RemoveAll(connection => connection.IsCompleted);
                        connections.Add(HandleClientAsync(connected, lifetime.Token));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "桌面通知通道不可用，将重建连接；任务调度继续运行");
                }
                finally
                {
                    lifetime.Cancel();
                    listener?.Dispose();
                    await Task.WhenAll(connections.Append(launcher));
                }

                // 仅通道建立失败时退避，不定时查询任务状态
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止，连接由各自的取消令牌退出
        }
        finally
        {
            taskService.ConfigurationChanged -= ConfigurationChanged;
            taskService.NotificationChanged -= PushState;
        }
    }

    /// <summary>
    /// 配置落库后唤起需要的桌面端，并更新已连接的会话
    /// </summary>
    private void ConfigurationChanged()
    {
        launches.Writer.TryWrite(0);
        PushState();
    }

    /// <summary>
    /// 合并各会话的待推送信号，不在保存配置的线程执行数据库或管道操作
    /// </summary>
    private void PushState()
    {
        foreach (var signal in clients.Values)
        {
            signal.Writer.TryWrite(0);
        }
    }

    /// <summary>
    /// 每个会话只保留一个待处理信号，连续变化读取最新状态
    /// </summary>
    private static Channel<byte> CreateSignal() => Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    /// <summary>
    /// 按事件判断运行需求，普通运行直接启动，服务模式通过交互计划任务启动
    /// </summary>
    private async Task LaunchDesktopAsync(CancellationToken ct)
    {
        try
        {
            // 登录检查脚本需要本机 HTTP 接口，等待宿主完成启动后再请求唤起
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = applicationLifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                await launches.Reader.ReadAsync(ct);

                try
                {
                    if (taskService.HasEnabledNotifications)
                        StartDesktop();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "桌面端启动失败，请检查桌面程序或重新安装服务注册交互计划任务");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停止时取消启动等待
        }
    }

    /// <summary>
    /// 为已登录且尚未连接的用户启动桌面端，不保存用户密码
    /// </summary>
    private void StartDesktop()
    {
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            using var current = Process.GetCurrentProcess();

            if (clients.ContainsKey(current.SessionId))
                return;

            var root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var path = Path.Combine(root, "Tray", "AutomateTask.Desktop.exe");

            if (!File.Exists(path))
            {
                var output = new DirectoryInfo(root);
                var solution = output.Parent?.Parent?.Parent?.Parent
                    ?? throw new DirectoryNotFoundException("未找到桌面端输出目录");
                path = Path.Combine(solution.FullName, "AutomateTask.Desktop", "bin",
                    output.Parent!.Name, output.Name, "AutomateTask.Desktop.exe");
            }

            if (!File.Exists(path))
                throw new FileNotFoundException("请先编译桌面端或检查发布包中的 Tray 目录", path);

            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(root);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("桌面端进程未创建");
            log.LogInformation("已请求启动桌面端，会话：{SessionId}", current.SessionId);
            return;
        }

        var sessions = new HashSet<int>();

        // 系统托盘依赖 Explorer；登录触发器负责尚未进入桌面的新会话
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId > 0 && !clients.ContainsKey(process.SessionId))
                    sessions.Add(process.SessionId);
            }
        }

        if (sessions.Count == 0)
            return;

        object? scheduler = null;
        object? folder = null;
        object? registered = null;

        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows 任务计划服务不可用"));
            dynamic service = scheduler!;
            service.Connect();
            folder = service.GetFolder("\\");
            registered = ((dynamic)folder).GetTask("AutomateTask.Desktop");

            if (!(bool)((dynamic)registered).Enabled)
                throw new InvalidOperationException("桌面端交互计划任务已停用");

            foreach (var session in sessions)
            {
                // TASK_RUN_USE_SESSION_ID：使用目标会话现有的交互登录身份
                object running = ((dynamic)registered).RunEx(null, 4, session, null);
                Marshal.FinalReleaseComObject(running);
                log.LogInformation("已请求通过交互计划任务启动桌面端，会话：{SessionId}", session);
            }
        }
        finally
        {
            foreach (var value in new[] { registered, folder, scheduler })
            {
                if (value != null && Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
            }
        }
    }

    /// <summary>
    /// 维护单个用户的长连接，只有状态变化或还有待处理通知时发送消息
    /// </summary>
    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Channel<byte>? signal = null;
        Task<int>? read = null;
        Task<byte>? change = null;
        var session = -1;
        var registered = false;
        var desktopRequired = true;
        var awaitingNotification = false;

        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
                throw new IOException("无法读取桌面端会话");

            using (var process = Process.GetProcessById(checked((int)processId)))
            {
                session = process.SessionId;
            }

            HashSet<string> receipts;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                receipts = await NotificationProtocol.Read<HashSet<string>>(pipe, deadline.Token)
                    ?? throw new InvalidDataException("通知回执为空");
            }

            if (receipts.Count > 512 || receipts.Any(id => string.IsNullOrEmpty(id) || id.Length > 150))
                throw new InvalidDataException("通知回执无效");

            signal = CreateSignal();
            registered = clients.TryAdd(session, signal);

            if (!registered)
                return;

            var receiptDate = DateOnly.FromDateTime(DateTime.Today);
            var acknowledgement = new byte[1];
            signal.Writer.TryWrite(0);
            read = pipe.ReadAsync(acknowledgement, lifetime.Token).AsTask();

            while (!lifetime.IsCancellationRequested)
            {
                change = signal.Reader.ReadAsync(lifetime.Token).AsTask();

                // 同时等待 EOF，使用户手动退出后立即移除连接，而非等待下一次配置变更
                if (await Task.WhenAny(change, read) == read)
                {
                    if (await read == 0)
                        return;

                    throw new InvalidDataException("收到未请求的桌面回执");
                }

                await change;
                var today = DateOnly.FromDateTime(DateTime.Today);

                if (receiptDate != today)
                {
                    receiptDate = today;
                    receipts.Clear();
                }

                var status = taskService.GetNotificationStatus(receipts);
                desktopRequired = status.DesktopRequired;
                awaitingNotification = status.Notification != null;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await NotificationProtocol.Write(pipe, status, deadline.Token);

                if (await read.WaitAsync(deadline.Token) != 1 || acknowledgement[0] != 1)
                    throw new InvalidDataException("桌面端未确认通知状态");

                if (!desktopRequired)
                    return;

                if (status.Notification is { } notification)
                {
                    receipts.Add(notification.Id);
                    taskService.Acknowledge(notification);
                    signal.Writer.TryWrite(0);
                }

                awaitingNotification = false;
                read = pipe.ReadAsync(acknowledgement, lifetime.Token).AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停止时取消所有挂起的读取
        }
        catch (Exception ex)
        {
            if (awaitingNotification)
                log.LogWarning(ex, "桌面端未确认接收通知，保留待处理状态");
            else if (ex is not IOException and not OperationCanceledException)
                log.LogWarning(ex, "桌面通知连接处理失败");
        }
        finally
        {
            if (registered)
                clients.TryRemove(session, out _);

            lifetime.Cancel();
            signal?.Writer.TryComplete();
            pipe.Dispose();

            try
            {
                if (read != null)
                    await read;

                if (change != null)
                    await change;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException)
            {
                // 关闭连接并观察已取消的异步读取
            }

            // 处理关闭任务后立即重新启用的竞争；手动退出不在此处重新唤起
            if (registered && !desktopRequired && !ct.IsCancellationRequested)
                launches.Writer.TryWrite(0);
        }
    }

    /// <summary>
    /// 读取客户端 Windows 会话，用于避免重复启动桌面端
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    /// <summary>
    /// 首个监听独占管道名称，后续实例保持相同 ACL 并支持多个用户会话
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(NotificationProtocol.PipeName(AppContext.BaseDirectory),
            PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            options, 0, NotificationProtocol.MaxMessageBytes, security);
    }
}
