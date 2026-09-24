using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace DuckDesktopPet;

internal static class Program
{
    internal const int WakeMessage = 0x8051;

    [STAThread]
    private static void Main()
    {
        // FindWindow only sees windows on this Windows desktop. A process
        // launched by a background helper must not block the user's desktop.
        IntPtr existing = FindWindow(null, "小鸭桌宠");
        if (existing != IntPtr.Zero)
        {
            PostMessage(existing, WakeMessage, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(180);
            SetForegroundWindow(existing);
            return;
        }

        try
        {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var pet = new PetWindow();
            app.MainWindow = pet;
            pet.ShowForLaunch();
            app.Run();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "小鸭桌宠启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class PetWindow : Window
{
    private const double FixedWidth = 140;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _gestureTimer;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly string _settingsPath;
    private readonly double _aspectRatio;
    private readonly Image _image;
    private readonly TranslateTransform _actionJump = new();
    private readonly RotateTransform _actionTurn = new();
    private readonly ScaleTransform _actionStretch = new(1, 1);
    private readonly DispatcherTimer _dragTimer;
    private readonly TranslateTransform _dragBounce = new();
    private readonly RotateTransform _dragTurn = new();
    private readonly ScaleTransform _dragStretch = new(1, 1);
    private PetSettings _settings;
    private Point? _mouseDown;
    private bool _isDragging;
    private DragDirection _dragDirection;
    private DateTime _lastDragMotion;
    private double _dragPhase;
    private bool _actionPlaying;
    private DateTime _launchGraceUntil;

    private enum DragDirection { Left, Right, Up, Down }

    public PetWindow()
    {
        string spritePath = Path.Combine(AppContext.BaseDirectory, "duck-magenta.png");
        if (!File.Exists(spritePath)) throw new FileNotFoundException("找不到玩偶图片，请将 duck-magenta.png 与程序放在同一文件夹。", spritePath);

        BitmapSource sprite = LoadChromaKeyedSprite(spritePath);
        _aspectRatio = (double)sprite.PixelHeight / sprite.PixelWidth;
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuckDesktopPet", "settings.json");
        _settings = LoadSettings(_settingsPath);

        Title = "小鸭桌宠";
        Width = FixedWidth;
        Height = Width * _aspectRatio;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WakeHook);
        };

        _image = new Image { Source = sprite, Stretch = Stretch.Fill, Cursor = Cursors.Hand };
        _image.RenderTransformOrigin = new Point(0.5, 0.8);
        var idleBreath = new ScaleTransform(1, 1);
        var idleSway = new RotateTransform();
        var idleBob = new TranslateTransform();
        var idleTransforms = new TransformGroup();
        idleTransforms.Children.Add(idleBreath);
        idleTransforms.Children.Add(idleSway);
        idleTransforms.Children.Add(idleBob);
        _image.RenderTransform = idleTransforms;
        idleBreath.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.025, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        idleSway.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-1.4, 1.4, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        idleBob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -3, TimeSpan.FromSeconds(1.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        var actionTransforms = new TransformGroup();
        actionTransforms.Children.Add(_actionStretch);
        actionTransforms.Children.Add(_actionTurn);
        actionTransforms.Children.Add(_actionJump);
        var stage = new Grid { RenderTransform = actionTransforms, RenderTransformOrigin = new Point(0.5, 0.9) };
        stage.Children.Add(_image);
        var dragTransforms = new TransformGroup();
        dragTransforms.Children.Add(_dragStretch);
        dragTransforms.Children.Add(_dragTurn);
        dragTransforms.Children.Add(_dragBounce);
        var dragStage = new Grid { RenderTransform = dragTransforms, RenderTransformOrigin = new Point(0.5, 0.85) };
        dragStage.Children.Add(stage);
        Content = dragStage;
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _dragTimer.Tick += (_, _) => UpdateDragPose();
        _image.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                EndDrag();
                _mouseDown = null;
                _image.ReleaseMouseCapture();
                OpenGptApp();
                e.Handled = true;
                return;
            }
            _mouseDown = e.GetPosition(this);
            _image.CaptureMouse();
        };
        _image.MouseMove += (_, e) =>
        {
            if (_mouseDown is not Point start) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            Point now = e.GetPosition(this);
            double dx = now.X - start.X;
            double dy = now.Y - start.Y;
            bool justStarted = false;
            if (!_isDragging)
            {
                if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
                _isDragging = true;
                justStarted = true;
                _dragPhase = 0;
                CancelDragReturnAnimation();
                _dragTimer.Start();
            }
            if (Math.Abs(dx) + Math.Abs(dy) < 0.5) return;
            DragDirection previousDirection = _dragDirection;
            _dragDirection = Math.Abs(dx) >= Math.Abs(dy)
                ? (dx >= 0 ? DragDirection.Right : DragDirection.Left)
                : (dy >= 0 ? DragDirection.Down : DragDirection.Up);
            _lastDragMotion = DateTime.UtcNow;
            if (justStarted || previousDirection != _dragDirection) UpdateDragPose();
            // The pointer position is measured in window coordinates. Moving the
            // window by this delta keeps the original grab point under the mouse.
            Left = Math.Clamp(Left + dx,
                SystemParameters.VirtualScreenLeft - Width + 35,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 35);
            Top = Math.Clamp(Top + dy,
                SystemParameters.VirtualScreenTop - Height + 35,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 35);
        };
        _image.MouseLeftButtonUp += (_, _) =>
        {
            if (_mouseDown is null) return;
            bool dragged = _isDragging;
            EndDrag();
            if (!dragged) PlayWiggle();
        };
        _image.LostMouseCapture += (_, _) => EndDrag();
        _image.ContextMenu = BuildPetMenu();

        if (double.IsFinite(_settings.Left) && double.IsFinite(_settings.Top))
        {
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else
        {
            PlaceBottomRight();
        }

        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示小鸭桌宠", null, (_, _) => ShowForLaunch());
        var trayStartup = new System.Windows.Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        trayStartup.Click += (_, _) =>
        {
            SetStartupEnabled(trayStartup.Checked);
            trayStartup.Checked = StartupManager.IsEnabled();
        };
        trayMenu.Items.Add(trayStartup);
        trayMenu.Opening += (_, _) => trayStartup.Checked = StartupManager.IsEnabled();
        trayMenu.Items.Add("退出小鸭桌宠", null, (_, _) => Close());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "小鸭桌宠：回到桌面时显示",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _tray.DoubleClick += (_, _) => ShowForLaunch();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => RefreshVisibility();
        _timer.Start();
        _gestureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _gestureTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_actionPlaying && !_isDragging)
            {
                if (Random.Shared.Next(2) == 0) PlayHop();
                else PlayWiggle();
            }
        };
        _gestureTimer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _gestureTimer.Stop();
            _dragTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            trayMenu.Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        RefreshVisibility();
    }

    private IntPtr WakeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Program.WakeMessage)
        {
            ShowForLaunch();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ShowForLaunch()
    {
        ShowIfHidden();
        Activate();
        IntPtr ownWindow = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetForegroundWindow(ownWindow);
        // Windows can decline focus when another process starts an existing app.
        // Keep the pet visible briefly while that launcher brings it forward.
        if (GetForegroundWindow() != ownWindow)
            _launchGraceUntil = DateTime.UtcNow.AddSeconds(2);
        else
            _launchGraceUntil = DateTime.MinValue;
    }

    private ContextMenu BuildPetMenu()
    {
        var menu = new ContextMenu();
        var startup = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = StartupManager.IsEnabled() };
        startup.Click += (_, _) =>
        {
            SetStartupEnabled(startup.IsChecked);
            startup.IsChecked = StartupManager.IsEnabled();
        };
        menu.Opened += (_, _) => startup.IsChecked = StartupManager.IsEnabled();
        menu.Items.Add(startup);
        var reset = new MenuItem { Header = "移到右下角" };
        reset.Click += (_, _) => { PlaceBottomRight(); SaveSettings(); };
        menu.Items.Add(reset);
        var chooseApp = new MenuItem { Header = "设置双击打开的程序…" };
        chooseApp.Click += (_, _) => ChooseGptApp();
        menu.Items.Add(chooseApp);
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);
        return menu;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            StartupManager.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show($"设置开机自启失败：{ex.Message}", "小鸭桌宠");
        }
    }

    private void UpdateDragPose()
    {
        if (!_isDragging) return;
        if ((DateTime.UtcNow - _lastDragMotion).TotalMilliseconds > 160)
        {
            SetDragPose(0, 1, 1, 0, 0);
            return;
        }

        _dragPhase += 0.58;
        double step = Math.Sin(_dragPhase);
        double bounce = Math.Abs(step);
        switch (_dragDirection)
        {
            case DragDirection.Left:
                SetDragPose(-7 - 2 * step, 1.025, 0.985 - 0.01 * bounce, -2 * bounce, -2.5 * bounce);
                break;
            case DragDirection.Right:
                SetDragPose(7 + 2 * step, 1.025, 0.985 - 0.01 * bounce, 2 * bounce, -2.5 * bounce);
                break;
            case DragDirection.Up:
                SetDragPose(2 * step, 0.97, 1.055 + 0.01 * bounce, 0, -4 - 3 * bounce);
                break;
            case DragDirection.Down:
                SetDragPose(1.5 * step, 1.05, 0.94 - 0.01 * bounce, 0, 2 + 1.5 * bounce);
                break;
        }
    }

    private void SetDragPose(double angle, double scaleX, double scaleY, double x, double y)
    {
        _dragTurn.Angle = angle;
        _dragStretch.ScaleX = scaleX;
        _dragStretch.ScaleY = scaleY;
        _dragBounce.X = x;
        _dragBounce.Y = y;
    }

    private void EndDrag()
    {
        bool dragged = _isDragging;
        _mouseDown = null;
        _isDragging = false;
        _dragTimer.Stop();
        if (_image.IsMouseCaptured) _image.ReleaseMouseCapture();
        if (!dragged) return;

        AnimateDragReturn(_dragTurn, RotateTransform.AngleProperty, _dragTurn.Angle, 0);
        AnimateDragReturn(_dragStretch, ScaleTransform.ScaleXProperty, _dragStretch.ScaleX, 1);
        AnimateDragReturn(_dragStretch, ScaleTransform.ScaleYProperty, _dragStretch.ScaleY, 1);
        AnimateDragReturn(_dragBounce, TranslateTransform.XProperty, _dragBounce.X, 0);
        AnimateDragReturn(_dragBounce, TranslateTransform.YProperty, _dragBounce.Y, 0);
        SaveSettings();
    }

    private static void AnimateDragReturn(Animatable transform, DependencyProperty property, double from, double target)
    {
        transform.SetValue(property, target);
        transform.BeginAnimation(property, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void CancelDragReturnAnimation()
    {
        _dragTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        _dragStretch.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _dragStretch.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _dragBounce.BeginAnimation(TranslateTransform.XProperty, null);
        _dragBounce.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void PlayHop()
    {
        if (_actionPlaying) return;
        _actionPlaying = true;
        var jump = Keyframes((0, 0), (0.18, -17), (0.45, 0), (0.58, -6), (0.8, 0));
        jump.Completed += (_, _) => _actionPlaying = false;
        _actionJump.BeginAnimation(TranslateTransform.YProperty, jump);
        _actionStretch.BeginAnimation(ScaleTransform.ScaleYProperty,
            Keyframes((0, 1), (0.15, 1.045), (0.45, 0.965), (0.8, 1)));
    }

    private void PlayWiggle()
    {
        if (_actionPlaying) return;
        _actionPlaying = true;
        var wiggle = Keyframes((0, 0), (0.13, -7), (0.28, 7), (0.43, -5), (0.6, 5), (0.78, 0));
        wiggle.Completed += (_, _) => _actionPlaying = false;
        _actionTurn.BeginAnimation(RotateTransform.AngleProperty, wiggle);
    }

    private static DoubleAnimationUsingKeyFrames Keyframes(params (double seconds, double value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (seconds, value) in frames)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        return animation;
    }

    private void ChooseGptApp()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择双击玩偶时打开的程序或快捷方式",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Filter = "程序或快捷方式|*.lnk;*.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        _settings = _settings with { LaunchPath = picker.FileName };
        SaveSettings();
    }

    private void OpenGptApp()
    {
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ChatGPT.lnk");
        string target = _settings.LaunchPath ?? desktopShortcut;
        if (!File.Exists(target))
        {
            System.Windows.MessageBox.Show("找不到 ChatGPT 快捷方式。请右键玩偶，选择“设置双击打开的程序…”。", "小鸭桌宠");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show($"打开程序失败：{ex.Message}\n可右键玩偶重新选择程序。", "小鸭桌宠");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // A removed monitor can leave the pet outside the visible desktop.
            var desktop = SystemParameters.WorkArea;
            if (Left >= desktop.Right || Top >= desktop.Bottom || Left + Width <= desktop.Left || Top + Height <= desktop.Top)
            {
                PlaceBottomRight();
                SaveSettings();
            }
        });
    }

    private void PlaceBottomRight()
    {
        var desktop = SystemParameters.WorkArea;
        Left = desktop.Right - Width - 24;
        Top = desktop.Bottom - Height - 12;
    }

    private void SaveSettings()
    {
        _settings = _settings with { Width = FixedWidth, Left = Left, Top = Top };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch (IOException) { /* Display still works if settings cannot be saved. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static PetSettings LoadSettings(string path)
    {
        try { return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(path)) ?? new PetSettings(); }
        catch (IOException) { return new PetSettings(); }
        catch (JsonException) { return new PetSettings(); }
        catch (UnauthorizedAccessException) { return new PetSettings(); }
    }

    private void RefreshVisibility()
    {
        IntPtr foreground = GetForegroundWindow();
        // A detached/background desktop can report no foreground window.
        // Treat this as unknown and keep the current visibility state.
        if (foreground == IntPtr.Zero) return;
        if (IsDesktopForeground(foreground))
        {
            _launchGraceUntil = DateTime.MinValue;
            ShowIfHidden();
        }
        else if (DateTime.UtcNow < _launchGraceUntil) return;
        else if (IsVisible) Hide();
    }

    private void ShowIfHidden()
    {
        if (!IsVisible) Show();
    }

    private bool IsDesktopForeground(IntPtr foreground)
    {
        IntPtr ownWindow = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (foreground == ownWindow) return true;
        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == Environment.ProcessId) return true;

        var name = new StringBuilder(256);
        GetClassName(foreground, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    // The source was generated against a magenta screen. Removing that color in
    // memory keeps the distributed photo untouched and preserves soft fur edges.
    private static BitmapSource LoadChromaKeyedSprite(string path)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        // The pet is displayed at 140 px. Three-times resolution keeps fur
        // edges crisp while making startup much faster than decoding 1281 px.
        source.DecodePixelWidth = 420;
        source.UriSource = new Uri(path);
        source.EndInit();
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (r <= g + 65 || b <= g + 65 || r < 80 || b < 80) continue;

            double alpha = Math.Clamp((g - 20.0) / 155.0, 0, 1);
            if (alpha <= 0.025)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                continue;
            }
            if (alpha >= 0.98) continue;

            pixels[i] = (byte)Math.Clamp((b - 245 * (1 - alpha)) / alpha, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp((g - 5 * (1 - alpha)) / alpha, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp((r - 245 * (1 - alpha)) / alpha, 0, 255);
            pixels[i + 3] = (byte)(alpha * 255);
        }

        var sprite = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        sprite.Freeze();
        return sprite;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}

internal sealed record PetSettings(double Width = 240, double Left = double.NaN, double Top = double.NaN, string? LaunchPath = null);

internal static class StartupManager
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DuckDesktopPet.lnk");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return;
        }

        string executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "DuckDesktopPet.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
        object? shortcut = null;
        try
        {
            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(ShortcutPath);
            dynamic link = shortcut!;
            link.TargetPath = executable;
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = "小鸭桌宠开机自启";
            link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        if (!File.Exists(ShortcutPath))
            throw new IOException("未能在 Windows 启动文件夹创建快捷方式。");
    }
}
