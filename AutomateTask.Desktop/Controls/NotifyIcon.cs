using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AutomateTask.Desktop.Controls;

/// <summary>
/// 提供可在 C# 和 XAML 中使用的 Windows 托盘图标
/// </summary>
internal sealed class NotifyIcon : FrameworkElement, IDisposable
{
    private const uint IconId = 1;
    private HwndSource? messageWindow;
    private IntPtr iconHandle;
    private IntPtr transparentIconHandle;
    private readonly DispatcherTimer blinkTimer;
    private readonly uint taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private bool isAdded;
    private bool isBlinkHidden;
    private bool isDisposed;

    /// <summary>
    /// 图标源变化时刷新已显示的托盘图标
    /// </summary>
    public static readonly DependencyProperty IconSourceProperty =
        DependencyProperty.Register(nameof(IconSource), typeof(ImageSource), typeof(NotifyIcon), new PropertyMetadata(null, OnIconSourceChanged));

    /// <summary>
    /// Tooltip 文本变化时更新已显示的托盘信息
    /// </summary>
    public static readonly DependencyProperty ToolTipTextProperty =
        DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(NotifyIcon), new PropertyMetadata(string.Empty, OnToolTipTextChanged));

    /// <summary>
    /// 控制托盘图标的显示与移除
    /// </summary>
    public static readonly DependencyProperty IsIconVisibleProperty =
        DependencyProperty.Register(nameof(IsIconVisible), typeof(bool), typeof(NotifyIcon), new PropertyMetadata(false, OnIsIconVisibleChanged));

    /// <summary>
    /// 控制托盘图标闪烁计时器的启停
    /// </summary>
    public static readonly DependencyProperty IsBlinkingProperty =
        DependencyProperty.Register(nameof(IsBlinking), typeof(bool), typeof(NotifyIcon), new PropertyMetadata(false, OnIsBlinkingChanged));

    /// <summary>
    /// 设置托盘图标的闪烁间隔，值必须大于零
    /// </summary>
    public static readonly DependencyProperty BlinkIntervalProperty =
        DependencyProperty.Register(nameof(BlinkInterval), typeof(TimeSpan), typeof(NotifyIcon),
            new PropertyMetadata(TimeSpan.FromMilliseconds(500), OnBlinkIntervalChanged), IsValidBlinkInterval);

    /// <summary>
    /// 指定左键单击时执行的命令
    /// </summary>
    public static readonly DependencyProperty LeftClickCommandProperty =
        DependencyProperty.Register(nameof(LeftClickCommand), typeof(ICommand), typeof(NotifyIcon));

    /// <summary>
    /// 指定左键双击时执行的命令
    /// </summary>
    public static readonly DependencyProperty DoubleClickCommandProperty =
        DependencyProperty.Register(nameof(DoubleClickCommand), typeof(ICommand), typeof(NotifyIcon));

    /// <summary>
    /// 指定右键单击时执行的命令
    /// </summary>
    public static readonly DependencyProperty RightClickCommandProperty =
        DependencyProperty.Register(nameof(RightClickCommand), typeof(ICommand), typeof(NotifyIcon));

    /// <summary>
    /// 指定传递给托盘交互命令的参数
    /// </summary>
    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(NotifyIcon));

    /// <summary>
    /// 左键单击托盘图标时触发
    /// </summary>
    public static readonly RoutedEvent LeftClickEvent =
        EventManager.RegisterRoutedEvent(nameof(LeftClick), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(NotifyIcon));

    /// <summary>
    /// 左键双击托盘图标时触发
    /// </summary>
    public static readonly RoutedEvent DoubleClickEvent =
        EventManager.RegisterRoutedEvent(nameof(DoubleClick), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(NotifyIcon));

    /// <summary>
    /// 右键单击托盘图标时触发，可阻止默认菜单
    /// </summary>
    public static readonly RoutedEvent RightClickEvent =
        EventManager.RegisterRoutedEvent(nameof(RightClick), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(NotifyIcon));

    /// <summary>
    /// 获取或设置托盘显示的图标
    /// </summary>
    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    /// <summary>
    /// 获取或设置鼠标悬停时显示的文本
    /// </summary>
    public string ToolTipText
    {
        get => (string)GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    /// <summary>
    /// 获取或设置托盘图标是否可见
    /// </summary>
    public bool IsIconVisible
    {
        get => (bool)GetValue(IsIconVisibleProperty);
        set => SetValue(IsIconVisibleProperty, value);
    }

    /// <summary>
    /// 获取或设置托盘图标是否闪烁
    /// </summary>
    public bool IsBlinking
    {
        get => (bool)GetValue(IsBlinkingProperty);
        set => SetValue(IsBlinkingProperty, value);
    }

    /// <summary>
    /// 获取或设置闪烁间隔
    /// </summary>
    public TimeSpan BlinkInterval
    {
        get => (TimeSpan)GetValue(BlinkIntervalProperty);
        set => SetValue(BlinkIntervalProperty, value);
    }

    /// <summary>
    /// 获取或设置左键单击时执行的命令
    /// </summary>
    public ICommand? LeftClickCommand
    {
        get => (ICommand?)GetValue(LeftClickCommandProperty);
        set => SetValue(LeftClickCommandProperty, value);
    }

    /// <summary>
    /// 获取或设置左键双击时执行的命令
    /// </summary>
    public ICommand? DoubleClickCommand
    {
        get => (ICommand?)GetValue(DoubleClickCommandProperty);
        set => SetValue(DoubleClickCommandProperty, value);
    }

    /// <summary>
    /// 获取或设置右键单击时执行的命令
    /// </summary>
    public ICommand? RightClickCommand
    {
        get => (ICommand?)GetValue(RightClickCommandProperty);
        set => SetValue(RightClickCommandProperty, value);
    }

    /// <summary>
    /// 获取或设置传递给单击命令的参数
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>
    /// 托盘图标被左键单击时发生
    /// </summary>
    public event RoutedEventHandler LeftClick
    {
        add => AddHandler(LeftClickEvent, value);
        remove => RemoveHandler(LeftClickEvent, value);
    }

    /// <summary>
    /// 托盘图标被左键双击时发生
    /// </summary>
    public event RoutedEventHandler DoubleClick
    {
        add => AddHandler(DoubleClickEvent, value);
        remove => RemoveHandler(DoubleClickEvent, value);
    }

    /// <summary>
    /// 托盘图标被右键单击时发生
    /// </summary>
    public event RoutedEventHandler RightClick
    {
        add => AddHandler(RightClickEvent, value);
        remove => RemoveHandler(RightClickEvent, value);
    }

    /// <summary>
    /// 初始化托盘图标与任务栏恢复消息
    /// </summary>
    public NotifyIcon()
    {
        if (taskbarCreatedMessage == 0)
            throw CreateShellException("注册任务栏恢复消息失败");

        blinkTimer = new DispatcherTimer();
        blinkTimer.Tick += BlinkTimer_Tick;
    }

    /// <summary>
    /// 显示或刷新托盘图标
    /// </summary>
    public void Show()
    {
        VerifyAccess();
        ThrowIfDisposed();

        if (!IsIconVisible)
            SetCurrentValue(IsIconVisibleProperty, true);
        else
            ShowCore();
    }

    /// <summary>
    /// 隐藏托盘图标
    /// </summary>
    public void Hide()
    {
        VerifyAccess();

        if (isDisposed)
            return;

        if (IsIconVisible)
            SetCurrentValue(IsIconVisibleProperty, false);
        else
            HideCore();
    }

    /// <summary>
    /// 通过系统通知显示简短提醒，提交失败时保留待处理状态
    /// </summary>
    public void ShowBalloonTip(string title, string message, bool success)
    {
        VerifyAccess();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (title.Length > 63 || message.Length > 255)
            throw new ArgumentException("通知标题或内容超出系统长度限制");

        if (!isAdded)
            Show();

        var data = CreateData(NativeMethods.NIF_INFO, iconHandle);
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = success ? NativeMethods.NIIF_INFO : NativeMethods.NIIF_WARNING;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
            throw CreateShellException("发送系统通知失败");
    }

    /// <summary>
    /// 开始闪烁托盘图标
    /// </summary>
    public void StartBlinking()
    {
        VerifyAccess();
        ThrowIfDisposed();
        SetCurrentValue(IsBlinkingProperty, true);
    }

    /// <summary>
    /// 停止闪烁并恢复显示托盘图标
    /// </summary>
    public void StopBlinking()
    {
        VerifyAccess();

        if (isDisposed)
            return;

        SetCurrentValue(IsBlinkingProperty, false);
    }

    /// <summary>
    /// 隐藏托盘图标并释放原生资源
    /// </summary>
    public void Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Dispose);
            return;
        }

        if (isDisposed)
            return;

        HideCore();

        if (messageWindow != null)
        {
            messageWindow.RemoveHook(WndProc);
            messageWindow.Dispose();
            messageWindow = null;
        }

        blinkTimer.Tick -= BlinkTimer_Tick;
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 图标源变化时刷新已显示的托盘图标
    /// </summary>
    private static void OnIconSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var notifyIcon = (NotifyIcon)d;
        if (notifyIcon.IsIconVisible)
            notifyIcon.ShowCore();
    }

    /// <summary>
    /// Tooltip 变化时更新托盘信息
    /// </summary>
    private static void OnToolTipTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var notifyIcon = (NotifyIcon)d;
        if (notifyIcon.isAdded)
            notifyIcon.UpdateToolTip();
    }

    /// <summary>
    /// 可见状态变化时添加或移除托盘图标
    /// </summary>
    private static void OnIsIconVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var notifyIcon = (NotifyIcon)d;
        if ((bool)e.NewValue)
        {
            notifyIcon.ShowCore();
            if (notifyIcon.IsBlinking)
                notifyIcon.StartBlinkingCore();
        }
        else
        {
            notifyIcon.StopBlinkingCore();
            notifyIcon.HideCore();
        }
    }

    /// <summary>
    /// 闪烁状态变化时启动或停止计时器
    /// </summary>
    private static void OnIsBlinkingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var notifyIcon = (NotifyIcon)d;
        if ((bool)e.NewValue)
            notifyIcon.StartBlinkingCore();
        else
            notifyIcon.StopBlinkingCore();
    }

    /// <summary>
    /// 闪烁间隔变化时更新计时器
    /// </summary>
    private static void OnBlinkIntervalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NotifyIcon)d).blinkTimer.Interval = (TimeSpan)e.NewValue;
    }

    /// <summary>
    /// 验证闪烁间隔为正数
    /// </summary>
    private static bool IsValidBlinkInterval(object value)
    {
        return value is TimeSpan interval && interval > TimeSpan.Zero;
    }

    /// <summary>
    /// 创建或刷新原生托盘图标
    /// </summary>
    private void ShowCore()
    {
        ThrowIfDisposed();

        if (IconSource == null)
            return;

        EnsureMessageWindow();
        var newIconHandle = CreateIconHandle(IconSource);

        if (isAdded)
        {
            var wasBlinkHidden = isBlinkHidden;
            var modifyData = CreateData(NativeMethods.NIF_ICON, newIconHandle);
            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref modifyData))
            {
                NativeMethods.DestroyIcon(newIconHandle);
                throw CreateShellException("修改托盘图标失败。");
            }

            ReleaseIconHandle();
            ReleaseTransparentIconHandle();
            iconHandle = newIconHandle;
            isBlinkHidden = false;
            if (wasBlinkHidden)
                SetIconHidden(true);

            return;
        }

        iconHandle = newIconHandle;
        var addData = CreateData(
            NativeMethods.NIF_MESSAGE |
            NativeMethods.NIF_ICON |
            NativeMethods.NIF_TIP |
            NativeMethods.NIF_SHOWTIP,
            iconHandle);

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref addData))
        {
            ReleaseIconHandle();
            throw CreateShellException("创建托盘图标失败。");
        }

        isAdded = true;

        var versionData = CreateData(0, iconHandle);
        versionData.Version = NativeMethods.NOTIFYICON_VERSION_4;
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref versionData))
        {
            HideCore();
            throw CreateShellException("设置托盘图标版本失败。");
        }

        if (IsBlinking)
            StartBlinkingCore();
    }

    /// <summary>
    /// 从系统托盘移除图标并释放图标句柄
    /// </summary>
    private void HideCore()
    {
        blinkTimer.Stop();

        if (isAdded && messageWindow != null)
        {
            var data = CreateData(0, iconHandle);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            isAdded = false;
        }

        isBlinkHidden = false;
        ReleaseIconHandle();
        ReleaseTransparentIconHandle();
    }

    /// <summary>
    /// 更新已显示图标的 Tooltip
    /// </summary>
    private void UpdateToolTip()
    {
        if (!isAdded)
            return;

        var data = CreateData(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP, iconHandle);
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
            throw CreateShellException("更新托盘 Tooltip 失败。");
    }

    /// <summary>
    /// 启动闪烁计时器
    /// </summary>
    private void StartBlinkingCore()
    {
        if (!IsIconVisible || !isAdded)
            return;

        blinkTimer.Interval = BlinkInterval;
        blinkTimer.Start();
    }

    /// <summary>
    /// 停止闪烁并恢复图标显示
    /// </summary>
    private void StopBlinkingCore()
    {
        blinkTimer.Stop();
        SetIconHidden(false);
    }

    /// <summary>
    /// 切换闪烁时的图标显示状态
    /// </summary>
    private void BlinkTimer_Tick(object? sender, EventArgs e)
    {
        SetIconHidden(!isBlinkHidden);
    }

    /// <summary>
    /// 在同一托盘项中切换真实图标和透明图标
    /// </summary>
    private void SetIconHidden(bool hidden)
    {
        if (!isAdded || isBlinkHidden == hidden)
            return;

        var displayedIconHandle = hidden ? GetTransparentIconHandle() : iconHandle;
        var data = CreateData(NativeMethods.NIF_ICON, displayedIconHandle);
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            blinkTimer.Stop();
            throw CreateShellException("更新托盘图标闪烁状态失败。");
        }

        isBlinkHidden = hidden;
    }

    /// <summary>
    /// 获取用于保持托盘槽位的透明图标句柄
    /// </summary>
    private IntPtr GetTransparentIconHandle()
    {
        if (transparentIconHandle != IntPtr.Zero)
            return transparentIconHandle;

        transparentIconHandle = CreateTransparentIconHandle();
        return transparentIconHandle;
    }

    /// <summary>
    /// 创建与当前图标尺寸一致且带全透明遮罩的图标句柄
    /// </summary>
    private IntPtr CreateTransparentIconHandle()
    {
        if (IconSource is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new InvalidOperationException("创建透明托盘图标时缺少有效的 IconSource。");

        var stride = source.PixelWidth * 4;
        var bitmapInfo = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = source.PixelWidth,
                Height = -source.PixelHeight,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)(stride * source.PixelHeight),
            }
        };

        var colorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref bitmapInfo,
            NativeMethods.DIB_RGB_COLORS,
            out var colorBitmapBits,
            IntPtr.Zero,
            0);
        if (colorBitmap == IntPtr.Zero || colorBitmapBits == IntPtr.Zero)
        {
            if (colorBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(colorBitmap);

            throw CreateShellException("创建透明托盘图标颜色位图失败。");
        }

        var maskBitmap = IntPtr.Zero;
        try
        {
            Marshal.Copy(new byte[stride * source.PixelHeight], 0, colorBitmapBits, stride * source.PixelHeight);

            var maskStride = ((source.PixelWidth + 31) / 32) * 4;
            var maskBits = new byte[maskStride * source.PixelHeight];
            Array.Fill(maskBits, byte.MaxValue);
            maskBitmap = NativeMethods.CreateBitmap(source.PixelWidth, source.PixelHeight, 1, 1, maskBits);
            if (maskBitmap == IntPtr.Zero)
                throw CreateShellException("创建透明托盘图标遮罩位图失败。");

            var iconInfo = new NativeMethods.IconInfo
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap,
            };
            var createdIconHandle = NativeMethods.CreateIconIndirect(ref iconInfo);
            if (createdIconHandle == IntPtr.Zero)
                throw CreateShellException("创建透明托盘图标句柄失败。");

            return createdIconHandle;
        }
        finally
        {
            if (maskBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(maskBitmap);

            NativeMethods.DeleteObject(colorBitmap);
        }
    }

    /// <summary>
    /// 创建用于接收托盘回调的隐藏消息窗口
    /// </summary>
    private void EnsureMessageWindow()
    {
        if (messageWindow != null)
            return;

        var parameters = new HwndSourceParameters($"AutomateTask.NotifyIcon.{GetHashCode()}")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        messageWindow = new HwndSource(parameters);
        messageWindow.AddHook(WndProc);
    }

    /// <summary>
    /// 接收并分发托盘图标的 Win32 消息
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启后重新添加托盘项，保留原 WinForms 托盘的恢复能力
        if (message == taskbarCreatedMessage)
        {
            isAdded = false;
            isBlinkHidden = false;
            ReleaseIconHandle();
            ReleaseTransparentIconHandle();

            if (IsIconVisible)
                ShowCore();

            handled = true;
            return IntPtr.Zero;
        }

        if (message != NativeMethods.CallbackMessage)
            return IntPtr.Zero;

        var notification = unchecked((int)(long)lParam) & 0xFFFF;
        switch (notification)
        {
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
                RaiseClick(LeftClickEvent, LeftClickCommand);
                handled = true;
                break;
            case NativeMethods.WM_LBUTTONDBLCLK:
                RaiseClick(DoubleClickEvent, DoubleClickCommand);
                handled = true;
                break;
            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                HandleRightClick();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 执行单击命令并触发对应路由事件
    /// </summary>
    private void RaiseClick(RoutedEvent routedEvent, ICommand? command)
    {
        if (command?.CanExecute(CommandParameter) == true)
            command.Execute(CommandParameter);

        RaiseEvent(new RoutedEventArgs(routedEvent, this));
    }

    /// <summary>
    /// 分发右键事件、命令和默认菜单
    /// </summary>
    private void HandleRightClick()
    {
        var args = new RoutedEventArgs(RightClickEvent, this);
        RaiseEvent(args);
        if (args.Handled)
            return;

        if (RightClickCommand?.CanExecute(CommandParameter) == true)
            RightClickCommand.Execute(CommandParameter);

        OpenContextMenu();
    }

    /// <summary>
    /// 在鼠标位置打开 WPF 右键菜单
    /// </summary>
    private void OpenContextMenu()
    {
        if (ContextMenu == null || messageWindow == null)
            return;

        if (ContextMenu.ReadLocalValue(DataContextProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(
                ContextMenu,
                DataContextProperty,
                new Binding(nameof(DataContext)) { Source = this });
        }

        NativeMethods.SetForegroundWindow(messageWindow.Handle);
        ContextMenu.PlacementTarget = null;
        // 由 WPF 换算鼠标位置，避免将屏幕像素直接当成设备无关单位
        ContextMenu.Placement = PlacementMode.MousePoint;
        ContextMenu.HorizontalOffset = 0;
        ContextMenu.VerticalOffset = 0;
        ContextMenu.IsOpen = true;
        NativeMethods.PostMessage(messageWindow.Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 构造 Shell_NotifyIcon 使用的原生数据
    /// </summary>
    private NativeMethods.NotifyIconData CreateData(uint flags, IntPtr currentIconHandle)
    {
        return new NativeMethods.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            WindowHandle = messageWindow?.Handle ?? IntPtr.Zero,
            Id = IconId,
            Flags = flags,
            CallbackMessage = NativeMethods.CallbackMessage,
            IconHandle = currentIconHandle,
            ToolTipText = GetToolTipText(),
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
    }

    /// <summary>
    /// 获取符合 Win32 长度限制的 Tooltip 文本
    /// </summary>
    private string GetToolTipText()
    {
        var text = ToolTipText ?? string.Empty;
        return text.Length <= 127 ? text : text[..127];
    }

    /// <summary>
    /// 将 WPF 图像转换为原生 HICON 句柄
    /// </summary>
    private static IntPtr CreateIconHandle(ImageSource source)
    {
        if (source is not BitmapSource bitmapSource || bitmapSource.PixelWidth <= 0 || bitmapSource.PixelHeight <= 0)
            throw new ArgumentException("IconSource 必须是有效的 BitmapSource。", nameof(source));

        var convertedBitmap = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);
        var stride = convertedBitmap.PixelWidth * 4;
        var pixels = new byte[stride * convertedBitmap.PixelHeight];
        convertedBitmap.CopyPixels(pixels, stride, 0);

        var bitmapInfo = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = convertedBitmap.PixelWidth,
                Height = -convertedBitmap.PixelHeight,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)pixels.Length,
            }
        };

        var colorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref bitmapInfo,
            NativeMethods.DIB_RGB_COLORS,
            out var bitmapBits,
            IntPtr.Zero,
            0);

        if (colorBitmap == IntPtr.Zero)
            throw CreateShellException("创建托盘图标颜色位图失败。");

        if (bitmapBits == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(colorBitmap);
            throw CreateShellException("获取托盘图标颜色位图数据失败。");
        }

        var maskBitmap = IntPtr.Zero;
        try
        {
            Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);

            var maskStride = ((convertedBitmap.PixelWidth + 31) / 32) * 4;
            var maskBits = new byte[maskStride * convertedBitmap.PixelHeight];
            maskBitmap = NativeMethods.CreateBitmap(
                convertedBitmap.PixelWidth,
                convertedBitmap.PixelHeight,
                1,
                1,
                maskBits);

            if (maskBitmap == IntPtr.Zero)
                throw CreateShellException("创建托盘图标遮罩位图失败。");

            var iconInfo = new NativeMethods.IconInfo
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap,
            };

            var createdIconHandle = NativeMethods.CreateIconIndirect(ref iconInfo);
            if (createdIconHandle == IntPtr.Zero)
                throw CreateShellException("创建托盘图标句柄失败。");

            return createdIconHandle;
        }
        finally
        {
            if (maskBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(maskBitmap);

            NativeMethods.DeleteObject(colorBitmap);
        }
    }

    /// <summary>
    /// 释放当前持有的 HICON 句柄
    /// </summary>
    private void ReleaseIconHandle()
    {
        if (iconHandle == IntPtr.Zero)
            return;

        NativeMethods.DestroyIcon(iconHandle);
        iconHandle = IntPtr.Zero;
    }

    /// <summary>
    /// 释放用于闪烁的透明图标句柄
    /// </summary>
    private void ReleaseTransparentIconHandle()
    {
        if (transparentIconHandle == IntPtr.Zero)
            return;

        NativeMethods.DestroyIcon(transparentIconHandle);
        transparentIconHandle = IntPtr.Zero;
    }

    /// <summary>
    /// 根据最后一个 Win32 错误创建异常
    /// </summary>
    private static Win32Exception CreateShellException(string message)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    /// <summary>
    /// 确保组件尚未释放
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }
}
