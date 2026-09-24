using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TogetherServer;

internal sealed class DesktopWindow
{
    private const string RuntimeDownload = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
    private static readonly TimeSpan GuiLoadDeadline = TimeSpan.FromSeconds(15);
    private static readonly Color WindowBorderColor = Color.FromArgb(48, 46, 44);
    private static readonly Color WindowCanvasColor = Color.FromArgb(14, 14, 15);
    private static readonly Color TitleBarColor = Color.FromArgb(17, 17, 18);
    private static readonly Color TextColor = Color.FromArgb(243, 240, 237);
    private static readonly Color SecondaryTextColor = Color.FromArgb(215, 210, 205);
    private static readonly Color AccentColor = Color.FromArgb(255, 138, 31);
    private static readonly Color AccentInkColor = Color.FromArgb(26, 14, 5);
    private static readonly Color AccentHoverColor = Color.FromArgb(73, 41, 19);
    private static readonly Color AccentPressedColor = Color.FromArgb(52, 32, 20);
    private readonly Uri address;
    private readonly string browserDataDirectory;
    private readonly Action stopApplication;
    private readonly bool startInTray;
    private readonly TaskCompletionSource<bool> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form? form;
    private NotifyIcon? trayIcon;
    private bool closing;
    private bool requestingQuit;
    private bool trayHintShown;
    private volatile bool closeToTray;
    private int started;
    private int startupShowRequested;
    private int fileDialogOpen;
    private int loadState = (int)GuiLoadState.NotStarted;
    private int loadErrorCode = (int)GuiLoadError.None;
    private int loadFailureKind = (int)GuiLoadFailureKind.None;
    private int loadFailureHResult;
    private int hasLoadFailureHResult;
    private int loadTerminal;
    private volatile bool rendered;
    private volatile bool visible;

    public DesktopWindow(Uri address, string dataDirectory, Action stopApplication, bool closeToTray, bool startInTray)
    {
        this.address = address;
        this.stopApplication = stopApplication;
        this.closeToTray = closeToTray;
        this.startInTray = startInTray;
        browserDataDirectory = Path.Combine(dataDirectory, "webview2");
    }

    public bool Visible => visible;
    public bool Rendered => rendered;
    public string LoadState => ((GuiLoadState)Volatile.Read(ref loadState)).ToString();
    public string? LoadErrorCode
    {
        get
        {
            var code = (GuiLoadError)Volatile.Read(ref loadErrorCode);
            return code == GuiLoadError.None ? null : code.ToString();
        }
    }
    public string? LoadFailureKind
    {
        get
        {
            if (Volatile.Read(ref hasLoadFailureHResult) == 0) return null;
            var kind = (GuiLoadFailureKind)Volatile.Read(ref loadFailureKind);
            return kind == GuiLoadFailureKind.None ? null : kind.ToString();
        }
    }
    public string? LoadFailureHResult => Volatile.Read(ref hasLoadFailureHResult) == 0
        ? null
        : unchecked((uint)Volatile.Read(ref loadFailureHResult)).ToString("X8", CultureInfo.InvariantCulture);
    public bool FileDialogOpen => Volatile.Read(ref fileDialogOpen) != 0;
    public bool CustomChrome => form is ChromeForm;
    public void SetCloseToTray(bool enabled) => closeToTray = enabled;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        var thread = new Thread(Run) { Name = "TogetherServer window", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task<bool> ShowAsync()
    {
        Interlocked.Exchange(ref startupShowRequested, 1);
        try { await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { return false; }
        var target = form;
        if (target is null || target.IsDisposed) return false;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (target.IsDisposed) { completed.TrySetResult(false); return; }
                try
                {
                    RevealWindow(target);
                    completed.TrySetResult(true);
                }
                catch (InvalidOperationException) { completed.TrySetResult(false); }
            }));
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return false; }
    }

    public async Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null)
    {
        try { await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { return null; }
        var target = form;
        if (target is null || target.IsDisposed) return null;
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (Interlocked.CompareExchange(ref fileDialogOpen, 1, 0) != 0)
                {
                    selected.TrySetResult(null);
                    return;
                }
                try
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = title,
                        Filter = filter,
                        CheckFileExists = true,
                        Multiselect = false,
                        RestoreDirectory = true
                    };
                    if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                        dialog.InitialDirectory = initialDirectory;
                    selected.TrySetResult(dialog.ShowDialog(target) == DialogResult.OK ? dialog.FileName : null);
                }
                catch (Exception ex) { selected.TrySetException(ex); }
                finally { Volatile.Write(ref fileDialogOpen, 0); }
            }));
        }
        catch (InvalidOperationException) { return null; }
        return await selected.Task;
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        try { await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { return null; }
        var target = form;
        if (target is null || target.IsDisposed) return null;
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (Interlocked.CompareExchange(ref fileDialogOpen, 1, 0) != 0)
                {
                    selected.TrySetResult(null);
                    return;
                }
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = title,
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = false,
                        AutoUpgradeEnabled = true
                    };
                    if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                        dialog.SelectedPath = initialDirectory;
                    selected.TrySetResult(dialog.ShowDialog(target) == DialogResult.OK ? dialog.SelectedPath : null);
                }
                catch (Exception ex) { selected.TrySetException(ex); }
                finally { Volatile.Write(ref fileDialogOpen, 0); }
            }));
        }
        catch (InvalidOperationException) { return null; }
        return await selected.Task;
    }

    public void Exit()
    {
        closing = true;
        var target = form;
        if (target is null || !target.IsHandleCreated || target.IsDisposed) return;
        try { target.BeginInvoke(new Action(target.Close)); }
        catch (InvalidOperationException) { }
    }

    public void Notify(string title, string message, bool warning = false)
    {
        var target = form;
        var tray = trayIcon;
        if (target is null || tray is null || target.IsDisposed || !target.IsHandleCreated) return;
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (!target.IsDisposed && tray.Visible)
                    tray.ShowBalloonTip(6000, title, message,
                        warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        try
        {
            TrySetLoadState(GuiLoadState.WindowStarting);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var window = new ChromeForm(startInTray)
            {
                Text = "TogetherServer",
                StartPosition = startInTray ? FormStartPosition.Manual : FormStartPosition.CenterScreen,
                Size = new Size(1180, 820),
                MinimumSize = new Size(380, 560),
                BackColor = WindowBorderColor,
                FormBorderStyle = FormBorderStyle.None,
                Padding = new Padding(1),
                ShowInTaskbar = !startInTray
            };
            if (startInTray) window.Location = OutsideVirtualDesktop(window.Size);
            form = window;
            var content = BuildChrome(window);
            using var trayIconImage = CreateTrayIcon();
            window.Icon = trayIconImage;
            using var trayMenu = new ContextMenuStrip();
            using var tray = new NotifyIcon
            {
                Icon = trayIconImage,
                Text = "TogetherServer",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon = tray;
            trayMenu.Items.Add("Open TogetherServer", null, (_, _) => _ = ShowAsync());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quit TogetherServer", null, (_, _) =>
            {
                if (!requestingQuit) _ = RequestQuitAsync(window);
            });
            tray.DoubleClick += (_, _) => _ = ShowAsync();
            window.FormClosing += (_, eventArgs) =>
            {
                if (closing || eventArgs.CloseReason == CloseReason.WindowsShutDown) return;
                eventArgs.Cancel = true;
                if (closeToTray) HideToTray(window, tray);
                else if (!requestingQuit) _ = RequestQuitAsync(window);
            };
            window.Shown += (_, _) =>
            {
                TrySetLoadState(GuiLoadState.WindowShown);
                shown.TrySetResult(true);
                if (Volatile.Read(ref startupShowRequested) != 0) RevealWindow(window);
                _ = LoadGuiAsync(window, content);
                visible = window.Visible;
            };
            window.VisibleChanged += (_, _) => visible = window.Visible;
            Application.Run(window);
        }
        catch (Exception ex)
        {
            TrySetLoadFailure(GuiLoadError.WindowInitializationFailed, ex);
            shown.TrySetException(ex);
            DesktopLaunch.ShowError("TogetherServer could not open its window.\n\n" + ex.Message);
            stopApplication();
        }
        finally
        {
            trayIcon = null;
            visible = false;
        }
    }

    private Control BuildChrome(Form window)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WindowCanvasColor,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleBar = new Panel { Dock = DockStyle.Fill, BackColor = TitleBarColor, Margin = Padding.Empty };
        var mark = new Label
        {
            Text = "T",
            ForeColor = AccentInkColor,
            BackColor = AccentColor,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(11, 8),
            Size = new Size(26, 26)
        };
        var title = new Label
        {
            Text = "TogetherServer",
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(47, 12)
        };
        var close = ChromeButton("×", "Close TogetherServer");
        var maximize = ChromeButton("□", "Maximize TogetherServer");
        var minimize = ChromeButton("—", "Minimize TogetherServer");
        close.Dock = DockStyle.Right;
        maximize.Dock = DockStyle.Right;
        minimize.Dock = DockStyle.Right;
        close.ForeColor = SecondaryTextColor;
        close.MouseEnter += (_, _) => close.BackColor = Color.FromArgb(160, 52, 52);
        close.MouseLeave += (_, _) => close.BackColor = Color.Transparent;
        minimize.MouseEnter += ChromeHover;
        minimize.MouseLeave += ChromeLeave;
        maximize.MouseEnter += ChromeHover;
        maximize.MouseLeave += ChromeLeave;
        minimize.Click += (_, _) => window.WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => ToggleMaximize(window, maximize);
        close.Click += (_, _) => window.Close();

        void BeginDrag(object? _, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button != MouseButtons.Left || window.WindowState == FormWindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(window.Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }
        void Toggle(object? _, EventArgs __) => ToggleMaximize(window, maximize);
        titleBar.MouseDown += BeginDrag;
        mark.MouseDown += BeginDrag;
        title.MouseDown += BeginDrag;
        titleBar.DoubleClick += Toggle;
        mark.DoubleClick += Toggle;
        title.DoubleClick += Toggle;
        window.Resize += (_, _) =>
        {
            maximize.Text = window.WindowState == FormWindowState.Maximized ? "❐" : "□";
            maximize.AccessibleName = window.WindowState == FormWindowState.Maximized
                ? "Restore TogetherServer" : "Maximize TogetherServer";
        };

        titleBar.Controls.Add(mark);
        titleBar.Controls.Add(title);
        titleBar.Controls.Add(minimize);
        titleBar.Controls.Add(maximize);
        titleBar.Controls.Add(close);
        mark.BringToFront();
        title.BringToFront();
        var content = new Panel { Dock = DockStyle.Fill, BackColor = WindowCanvasColor, Margin = Padding.Empty };
        layout.Controls.Add(titleBar, 0, 0);
        layout.Controls.Add(content, 0, 1);
        window.Controls.Add(layout);
        return content;
    }

    private static Button ChromeButton(string text, string accessibleName)
    {
        var button = new Button
        {
            Text = text,
            AccessibleName = accessibleName,
            TabStop = false,
            Width = 46,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = SecondaryTextColor,
            Font = new Font("Segoe UI Symbol", 11),
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseDownBackColor = AccentPressedColor;
        button.FlatAppearance.MouseOverBackColor = AccentHoverColor;
        return button;
    }

    private static void ChromeHover(object? sender, EventArgs _) =>
        ((Button)sender!).BackColor = AccentHoverColor;

    private static void ChromeLeave(object? sender, EventArgs _) =>
        ((Button)sender!).BackColor = Color.Transparent;

    private static void ToggleMaximize(Form window, Button maximize)
    {
        if (window.WindowState == FormWindowState.Maximized) window.WindowState = FormWindowState.Normal;
        else if (window is ChromeForm chrome) chrome.MaximizeWithinWorkingArea();
        else window.WindowState = FormWindowState.Maximized;
        maximize.Text = window.WindowState == FormWindowState.Maximized ? "❐" : "□";
    }

    private async Task LoadGuiAsync(Form window, Control content)
    {
        var loading = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            BackColor = content.BackColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15),
            Text = "Opening TogetherServer..."
        };
        content.Controls.Add(loading);
        _ = EnforceLoadDeadlineAsync(window, content, loading);
        try
        {
            if (!TrySetLoadState(GuiLoadState.CreatingEnvironment)) return;
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: browserDataDirectory);
            if (window.IsDisposed || !TrySetLoadState(GuiLoadState.InitializingWebView)) return;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = content.BackColor };
            content.Controls.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            if (window.IsDisposed || IsLoadTerminal)
            {
                view.Dispose();
                return;
            }
            view.CoreWebView2.NavigationStarting += (_, eventArgs) =>
            {
                if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var destination) &&
                    destination.Scheme == address.Scheme && destination.Host == address.Host &&
                    destination.Port == address.Port) return;
                eventArgs.Cancel = true;
                OpenApprovedExternal(eventArgs.Uri);
            };
            view.CoreWebView2.NewWindowRequested += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                OpenApprovedExternal(eventArgs.Uri);
            };
            view.CoreWebView2.NavigationCompleted += async (_, eventArgs) =>
            {
                if (rendered || window.IsDisposed) return;
                if (!eventArgs.IsSuccess)
                {
                    if (!TrySetLoadFailure(GuiLoadError.NavigationFailed)) return;
                    ShowLoadError(window, content, loading,
                        "TogetherServer could not load its local interface. Close the app and try again.", false);
                    CompleteStartupPresentation(window);
                    return;
                }
                if (!TrySetLoadState(GuiLoadState.ProbingRender)) return;
                for (var attempt = 0; attempt < 20 && !window.IsDisposed; attempt++)
                {
                    try
                    {
                        var value = await view.ExecuteScriptAsync("document.querySelector('.shell') !== null");
                        if (JsonSerializer.Deserialize<bool>(value))
                        {
                            if (!TrySetRendered()) return;
                            rendered = true;
                            loading.Dispose();
                            CompleteStartupPresentation(window);
                            return;
                        }
                    }
                    catch (Exception) when (window.IsDisposed) { return; }
                    catch (Exception) { /* Navigation may still be settling; retry briefly. */ }
                    await Task.Delay(100);
                    if (IsLoadTerminal) return;
                }
                if (!TrySetLoadFailure(GuiLoadError.RenderProbeTimedOut)) return;
                ShowLoadError(window, content, loading, "TogetherServer loaded its local page, but the interface did not render. Close the app and try again.", false);
                CompleteStartupPresentation(window);
            };
            if (!TrySetLoadState(GuiLoadState.Navigating))
            {
                view.Dispose();
                return;
            }
            view.Source = address;
            view.BringToFront();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            if (!TrySetLoadFailure(GuiLoadError.WebViewRuntimeMissing, ex)) return;
            ShowLoadError(window, content, loading, "Microsoft Edge WebView2 Runtime is needed to display TogetherServer. Install it from Microsoft, then reopen this app.", true);
            CompleteStartupPresentation(window);
        }
        catch (Exception ex)
        {
            if (!TrySetLoadFailure(CurrentStageFailure(), ex)) return;
            ShowLoadError(window, content, loading, "TogetherServer could not display its interface.\n\n" + ex.Message, false);
            CompleteStartupPresentation(window);
        }
    }

    private bool IsLoadTerminal => Volatile.Read(ref loadTerminal) != 0;

    private bool TrySetLoadState(GuiLoadState state)
    {
        if (IsLoadTerminal) return false;
        Volatile.Write(ref loadState, (int)state);
        return true;
    }

    private bool TrySetRendered()
    {
        if (Interlocked.CompareExchange(ref loadTerminal, 1, 0) != 0) return false;
        Volatile.Write(ref hasLoadFailureHResult, 0);
        Volatile.Write(ref loadFailureHResult, 0);
        Volatile.Write(ref loadFailureKind, (int)GuiLoadFailureKind.None);
        Volatile.Write(ref loadErrorCode, (int)GuiLoadError.None);
        Volatile.Write(ref loadState, (int)GuiLoadState.Rendered);
        return true;
    }

    private bool TrySetLoadFailure(GuiLoadError error, Exception? exception = null)
    {
        if (Interlocked.CompareExchange(ref loadTerminal, 1, 0) != 0) return false;
        if (exception is null)
        {
            Volatile.Write(ref hasLoadFailureHResult, 0);
            Volatile.Write(ref loadFailureHResult, 0);
            Volatile.Write(ref loadFailureKind, (int)GuiLoadFailureKind.None);
        }
        else
        {
            Volatile.Write(ref loadFailureHResult, exception.HResult);
            Volatile.Write(ref loadFailureKind, (int)FailureKind(exception));
            Volatile.Write(ref hasLoadFailureHResult, 1);
        }
        Volatile.Write(ref loadErrorCode, (int)error);
        Volatile.Write(ref loadState, (int)GuiLoadState.Failed);
        return true;
    }

    private async Task EnforceLoadDeadlineAsync(Form window, Control content, Label loading)
    {
        await Task.Delay(GuiLoadDeadline).ConfigureAwait(false);
        if (window.IsDisposed) return;
        try
        {
            window.BeginInvoke(new Action(() =>
            {
                if (window.IsDisposed || !TrySetLoadFailure(GuiLoadError.StartupTimedOut)) return;
                ShowLoadError(window, content, loading,
                    "TogetherServer could not initialize its local interface in time. Close the app and try again.", false);
                CompleteStartupPresentation(window);
            }));
        }
        catch (InvalidOperationException) { /* The window closed before the deadline. */ }
    }

    private static GuiLoadFailureKind FailureKind(Exception exception) => exception switch
    {
        COMException => GuiLoadFailureKind.Com,
        UnauthorizedAccessException or System.Security.SecurityException => GuiLoadFailureKind.Unauthorized,
        InvalidOperationException => GuiLoadFailureKind.InvalidOperation,
        ArgumentException => GuiLoadFailureKind.Argument,
        IOException => GuiLoadFailureKind.IO,
        _ => GuiLoadFailureKind.Unexpected
    };

    private GuiLoadError CurrentStageFailure() =>
        (GuiLoadState)Volatile.Read(ref loadState) switch
        {
            GuiLoadState.CreatingEnvironment => GuiLoadError.EnvironmentCreationFailed,
            GuiLoadState.InitializingWebView => GuiLoadError.WebViewInitializationFailed,
            GuiLoadState.Navigating => GuiLoadError.NavigationSetupFailed,
            _ => GuiLoadError.InitializationFailed
        };

    private void CompleteStartupPresentation(Form window)
    {
        if (!startInTray || window.IsDisposed) return;
        if (Volatile.Read(ref startupShowRequested) != 0) return;
        window.Hide();
        if (window is ChromeForm chrome) chrome.AllowActivation();
    }

    private static void RevealWindow(Form window)
    {
        if (window is ChromeForm chrome) chrome.PrepareForReveal();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.ShowInTaskbar = true;
        window.Show();
        window.BringToFront();
        window.Activate();
    }

    private static Point OutsideVirtualDesktop(Size windowSize)
    {
        const int gap = 64;
        var bounds = SystemInformation.VirtualScreen;
        var right = (long)bounds.Right + gap;
        if (right <= int.MaxValue - windowSize.Width)
            return new Point((int)right, bounds.Top);
        var left = (long)bounds.Left - windowSize.Width - gap;
        if (left >= int.MinValue)
            return new Point((int)left, bounds.Top);
        var bottom = (long)bounds.Bottom + gap;
        if (bottom <= int.MaxValue - windowSize.Height)
            return new Point(bounds.Left, (int)bottom);
        var top = (long)bounds.Top - windowSize.Height - gap;
        return new Point(bounds.Left, top >= int.MinValue ? (int)top : int.MinValue);
    }

    private static void ShowLoadError(Form window, Control content, Label loading, string message, bool offerRuntimeLink)
    {
        if (window.IsDisposed) return;
        content.Controls.Clear();
        loading.Text = message;
        content.Controls.Add(loading);
        if (!offerRuntimeLink) return;
        var button = new Button
        {
            Text = "Get WebView2 from Microsoft",
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = AccentColor,
            ForeColor = AccentInkColor
        };
        button.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RuntimeDownload) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { DesktopLaunch.ShowError("Could not open Microsoft's WebView2 download page.\n\n" + ex.Message); }
        };
        content.Controls.Add(button);
    }

    private static void OpenApprovedExternal(string? target)
    {
        if (target is null || !new[] { "steam://install/896660", "https://www.minecraft.net/en-us/eula",
                "https://www.microsoft.com/en-us/privacy/privacystatement" }
            .Contains(target, StringComparer.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { DesktopLaunch.ShowError("Could not open the selected page.\n\n" + ex.Message); }
    }

    private void HideToTray(Form window, NotifyIcon tray)
    {
        window.Hide();
        window.ShowInTaskbar = false;
        if (trayHintShown) return;
        trayHintShown = true;
        tray.ShowBalloonTip(4000, "TogetherServer is still running",
            "Open it from the tray icon. Right-click the icon to quit.", ToolTipIcon.Info);
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(AccentColor))
        using (var foreground = new SolidBrush(AccentInkColor))
        using (var font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(background, 1, 1, 30, 30);
            graphics.DrawString("T", font, foreground, new RectangleF(0, 1, 32, 30), centered);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private async Task RequestQuitAsync(Form window)
    {
        requestingQuit = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "api/local/quit"));
            request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-TogetherServer-Local", "1");
            using var response = await client.SendAsync(request);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (result.RootElement.GetProperty("ok").GetBoolean()) return;
            var message = result.RootElement.GetProperty("message").GetString();
            if (!closing && !window.IsDisposed)
            {
                await ShowAsync();
                MessageBox.Show(window, message, "TogetherServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (!closing && !window.IsDisposed)
            {
                await ShowAsync();
                MessageBox.Show(window, "TogetherServer could not close yet.\n\n" + ex.Message,
                    "TogetherServer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally { requestingQuit = false; }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class ChromeForm : Form
    {
        private const int ResizeBorder = 7;
        private bool suppressActivation;
        private bool centerBeforeReveal;

        public ChromeForm(bool startupHidden)
        {
            suppressActivation = startupHidden;
            centerBeforeReveal = startupHidden;
        }

        public void AllowActivation() => suppressActivation = false;

        public void PrepareForReveal()
        {
            suppressActivation = false;
            if (!centerBeforeReveal) return;
            centerBeforeReveal = false;
            var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
        }

        protected override bool ShowWithoutActivation => suppressActivation;

        public void MaximizeWithinWorkingArea()
        {
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        protected override void WndProc(ref Message message)
        {
            const int hitTest = 0x0084;
            if (message.Msg == hitTest && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref message);
                var screen = new Point(unchecked((short)(long)message.LParam), unchecked((short)((long)message.LParam >> 16)));
                var point = PointToClient(screen);
                var left = point.X <= ResizeBorder;
                var right = point.X >= ClientSize.Width - ResizeBorder;
                var top = point.Y <= ResizeBorder;
                var bottom = point.Y >= ClientSize.Height - ResizeBorder;
                message.Result = (left, right, top, bottom) switch
                {
                    (true, _, true, _) => new IntPtr(13),
                    (_, true, true, _) => new IntPtr(14),
                    (true, _, _, true) => new IntPtr(16),
                    (_, true, _, true) => new IntPtr(17),
                    (true, _, _, _) => new IntPtr(10),
                    (_, true, _, _) => new IntPtr(11),
                    (_, _, true, _) => new IntPtr(12),
                    (_, _, _, true) => new IntPtr(15),
                    _ => message.Result
                };
                return;
            }
            base.WndProc(ref message);
        }
    }

    private enum GuiLoadState
    {
        NotStarted,
        WindowStarting,
        WindowShown,
        CreatingEnvironment,
        InitializingWebView,
        Navigating,
        ProbingRender,
        Rendered,
        Failed
    }

    private enum GuiLoadError
    {
        None,
        WindowInitializationFailed,
        WebViewRuntimeMissing,
        EnvironmentCreationFailed,
        WebViewInitializationFailed,
        NavigationSetupFailed,
        NavigationFailed,
        RenderProbeTimedOut,
        StartupTimedOut,
        InitializationFailed
    }

    private enum GuiLoadFailureKind
    {
        None,
        Com,
        Unauthorized,
        InvalidOperation,
        Argument,
        IO,
        Unexpected
    }
}
