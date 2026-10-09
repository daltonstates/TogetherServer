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
    private static readonly TimeSpan DraftFlushDeadline = TimeSpan.FromSeconds(12);
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
    private readonly string displayName;
    private readonly bool isStaging;
    private readonly QolLocalPreferences? localPreferences;
    private readonly DesktopNotificationPreferences? notifications;
    private readonly DesktopDraftFlushGate draftFlush;
    private readonly TaskCompletionSource<bool> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form? form;
    private NotifyIcon? trayIcon;
    private ToolStripItem? trayRunSummary;
    private ToolStripItem? trayFriendSummary;
    private CoreWebView2? uiBridge;
    private readonly Queue<NativeNotification> notificationQueue = new();
    private NativeNotification? activeNotification;
    private bool activeNotificationClicked;
    private DesktopNotificationDestination? pendingDestination;
    private DesktopTraySummary traySummary = new(0, 0, "Unknown");
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

    public DesktopWindow(Uri address, string dataDirectory, Action stopApplication, bool closeToTray, bool startInTray,
        string displayName = "TogetherServer", bool isStaging = false,
        QolLocalPreferences? localPreferences = null, DesktopNotificationPreferences? notifications = null)
    {
        this.address = address;
        this.stopApplication = stopApplication;
        this.closeToTray = closeToTray;
        this.startInTray = startInTray;
        this.displayName = displayName;
        this.isStaging = isStaging;
        this.localPreferences = localPreferences;
        this.notifications = notifications;
        draftFlush = new(address);
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

    public bool OpenFolder(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
            var full = Path.GetFullPath(path);
            for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(full);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public bool OpenTextFile(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
            var full = Path.GetFullPath(path);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            for (var current = new DirectoryInfo(Path.GetDirectoryName(full)!); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (!File.Exists(notepad)) return false;
            var start = new ProcessStartInfo(notepad) { UseShellExecute = false };
            start.ArgumentList.Add(full);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public void Exit()
    {
        closing = true;
        draftFlush.CancelAll();
        var target = form;
        if (target is null || !target.IsHandleCreated || target.IsDisposed) return;
        try { target.BeginInvoke(new Action(target.Close)); }
        catch (InvalidOperationException) { }
    }

    public void Notify(string title, string message, bool warning = false) =>
        NotifyCore(title, message, warning, "Lifecycle", null, null, null);

    public void Notify(ActivityEvent item, bool friendMode = false, Guid? connectionId = null)
    {
        if (item.Severity is not (ActivitySeverity.Important or ActivitySeverity.Warning)) return;
        NotifyCore($"{displayName} - {item.Category}", item.Message, item.Severity == ActivitySeverity.Warning,
            item.Category, item.ProfileId, connectionId,
            DesktopNotificationPreferences.Destination(item, friendMode, connectionId));
    }

    public void NotifyUpdate(string message) => NotifyCore(displayName + " update", message, false, "Update",
        null, null, new("settings", "app"));

    public void SetTraySummary(DesktopTraySummary summary)
    {
        if (summary.RunningServers is < 0 or > 128 || summary.ServersNeedingReview is < 0 or > 128 ||
            summary.FriendState is not ("Connected" or "Disabled" or "Unknown" or "Disconnected/Unknown" or
                "Revoked" or "Awaiting approval" or "No saved connection" or "Access expired")) return;
        traySummary = summary;
        var target = form;
        if (target is null || target.IsDisposed || !target.IsHandleCreated) return;
        try { target.BeginInvoke(new Action(UpdateTraySummary)); }
        catch (InvalidOperationException) { }
    }

    internal static string TraySummaryText(string displayName, DesktopTraySummary summary)
    {
        var text = $"{displayName}\nHosting: {summary.RunningServers} running, {summary.ServersNeedingReview} need review\nJoin: {summary.FriendState}";
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    private void UpdateTraySummary()
    {
        var summary = traySummary;
        if (trayIcon is not null) trayIcon.Text = TraySummaryText(displayName, summary);
        if (trayRunSummary is not null)
            trayRunSummary.Text = $"Hosting: {summary.RunningServers} running · {summary.ServersNeedingReview} need review";
        if (trayFriendSummary is not null) trayFriendSummary.Text = "Join: " + summary.FriendState;
    }

    private bool MayNotify(string category, Guid? profileId, Guid? connectionId) => notifications is not null
        ? notifications.ShouldNotify(category, profileId, connectionId)
        : WindowsNotificationState.Read() == DesktopUserNotificationState.AcceptsNotifications;

    private void NotifyCore(string title, string message, bool warning, string category, Guid? profileId,
        Guid? connectionId, DesktopNotificationDestination? destination)
    {
        if (destination is not null && !DesktopNotificationPreferences.IsDestination(destination)) return;
        var target = form;
        var tray = trayIcon;
        if (target is null || tray is null || target.IsDisposed || !target.IsHandleCreated) return;
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (target.IsDisposed || !tray.Visible || !MayNotify(category, profileId, connectionId)) return;
                if (notificationQueue.Count >= 8) return; // All events remain available in Attention.
                notificationQueue.Enqueue(new(BoundedNotificationText(title, 63), BoundedNotificationText(message, 240),
                    warning, category, profileId, connectionId, destination));
                ShowNextNotification();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void ShowNextNotification()
    {
        if (activeNotification is not null || trayIcon is not { Visible: true } tray) return;
        while (notificationQueue.TryDequeue(out var next))
        {
            if (!MayNotify(next.Category, next.ProfileId, next.ConnectionId)) continue;
            activeNotification = next;
            activeNotificationClicked = false;
            tray.ShowBalloonTip(6000, next.Title, next.Message, next.Warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            return;
        }
    }

    private void PostNotificationDestination()
    {
        if (pendingDestination is null || uiBridge is null || !rendered) return;
        try
        {
            uiBridge.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "together-notification", destination = pendingDestination },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            pendingDestination = null;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        { /* Keep the click destination for a later successful local render. */ }
    }

    private static string BoundedNotificationText(string value, int maximum)
    {
        var plain = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return plain.Length <= maximum ? plain : plain[..(maximum - 1)] + "…";
    }

    private sealed record NativeNotification(string Title, string Message, bool Warning, string Category,
        Guid? ProfileId, Guid? ConnectionId, DesktopNotificationDestination? Destination);

    private void Run()
    {
        try
        {
            TrySetLoadState(GuiLoadState.WindowStarting);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var placement = localPreferences?.LoadWindowPlacement();
            using var window = new ChromeForm(startInTray, placement)
            {
                Text = displayName,
                StartPosition = startInTray ? FormStartPosition.Manual : FormStartPosition.CenterScreen,
                Size = new Size(1180, 820),
                MinimumSize = new Size(380, 560),
                BackColor = WindowBorderColor,
                FormBorderStyle = FormBorderStyle.None,
                Padding = new Padding(1),
                ShowInTaskbar = !startInTray
            };
            if (!startInTray)
            {
                var primary = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
                var requested = placement ?? new DesktopWindowPlacement(primary.Left + Math.Max(0, (primary.Width - 1180) / 2),
                    primary.Top + Math.Max(0, (primary.Height - 820) / 2), 1180, 820);
                var bounds = QolLocalPreferences.ClampWindowBounds(requested, Screen.AllScreens.Select(screen => screen.WorkingArea));
                window.MinimumSize = new Size(Math.Min(380, bounds.Width), Math.Min(560, bounds.Height));
                window.StartPosition = FormStartPosition.Manual;
                window.Bounds = bounds;
            }
            if (startInTray) window.Location = OutsideVirtualDesktop(window.Size);
            form = window;
            var content = BuildChrome(window);
            using var trayIconImage = CreateTrayIcon(isStaging);
            window.Icon = trayIconImage;
            using var trayMenu = new ContextMenuStrip();
            using var tray = new NotifyIcon
            {
                Icon = trayIconImage,
                Text = displayName,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon = tray;
            trayRunSummary = trayMenu.Items.Add("Hosting: checking…");
            trayRunSummary.Enabled = false;
            trayFriendSummary = trayMenu.Items.Add("Join: checking…");
            trayFriendSummary.Enabled = false;
            trayMenu.Items.Add(new ToolStripSeparator());
            UpdateTraySummary();
            trayMenu.Items.Add("Open " + displayName, null, (_, _) => _ = ShowAsync());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quit " + displayName, null, (_, _) =>
            {
                if (!requestingQuit) _ = RequestQuitAsync(window);
            });
            tray.DoubleClick += (_, _) => _ = ShowAsync();
            tray.BalloonTipClicked += (_, _) =>
            {
                if (activeNotificationClicked || activeNotification?.Destination is not { } destination) return;
                activeNotificationClicked = true;
                pendingDestination = destination;
                // Incoming events never reveal the window. This path requires the owner's notification click.
                RevealWindow(window);
                PostNotificationDestination();
            };
            tray.BalloonTipClosed += (_, _) =>
            {
                activeNotification = null;
                if (!window.IsDisposed) window.BeginInvoke(new Action(ShowNextNotification));
            };
            using var placementTimer = new System.Windows.Forms.Timer { Interval = 600 };
            placementTimer.Tick += (_, _) =>
            {
                placementTimer.Stop();
                RememberWindowPlacement(window);
            };
            void SchedulePlacementSave(object? _, EventArgs __)
            {
                if (!window.Visible || window.WindowState == FormWindowState.Minimized) return;
                placementTimer.Stop();
                placementTimer.Start();
            }
            window.LocationChanged += SchedulePlacementSave;
            window.SizeChanged += SchedulePlacementSave;
            window.FormClosing += (_, eventArgs) =>
            {
                RememberWindowPlacement(window);
                if (closing || eventArgs.CloseReason == CloseReason.WindowsShutDown) return;
                eventArgs.Cancel = true;
                if (closeToTray) HideToTray(window, tray);
                else if (!requestingQuit) _ = RequestQuitAsync(window);
            };
            window.Shown += (_, _) =>
            {
                TrySetLoadState(GuiLoadState.WindowShown);
                shown.TrySetResult(true);
                if (!startInTray && placement?.Maximized == true) window.MaximizeWithinWorkingArea();
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
            draftFlush.CancelAll();
            trayIcon = null;
            uiBridge = null;
            visible = false;
        }
    }

    private void RememberWindowPlacement(Form window)
    {
        if (localPreferences is null || !window.Visible || window.WindowState == FormWindowState.Minimized) return;
        var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
        if (!HasUsefulVisibleArea(bounds, Screen.AllScreens.Select(screen => screen.WorkingArea))) return;
        try { localPreferences.SaveWindowPlacement(new(bounds.X, bounds.Y, bounds.Width, bounds.Height,
            window.WindowState == FormWindowState.Maximized)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Remembering a window is optional and never blocks hosting or closing. */ }
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
            Text = isStaging ? "S" : "T",
            ForeColor = AccentInkColor,
            BackColor = AccentColor,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(11, 8),
            Size = new Size(26, 26)
        };
        var title = new Label
        {
            Text = displayName,
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(47, 12)
        };
        var close = ChromeButton("×", "Close " + displayName);
        var maximize = ChromeButton("□", "Maximize " + displayName);
        var minimize = ChromeButton("—", "Minimize " + displayName);
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
                ? "Restore " + displayName : "Maximize " + displayName;
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
            Text = "Opening " + displayName + "..."
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
            uiBridge = view.CoreWebView2;
            view.CoreWebView2.WebMessageReceived += (_, eventArgs) =>
            {
                try
                {
                    if (closing || window.IsDisposed || !ReferenceEquals(uiBridge, view.CoreWebView2)) return;
                    draftFlush.TryComplete(eventArgs.Source, eventArgs.WebMessageAsJson);
                }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                { /* An unavailable or malformed bridge response never permits Quit. */ }
            };
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
                            PostNotificationDestination();
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
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        if (window is ChromeForm chrome) chrome.PrepareForReveal();
        window.ShowInTaskbar = true;
        window.Show();
        window.BringToFront();
        window.Activate();
    }

    internal static Rectangle RelativeMaximizedBounds(Rectangle monitorBounds, Rectangle workingArea) =>
        new(workingArea.Left - monitorBounds.Left, workingArea.Top - monitorBounds.Top,
            workingArea.Width, workingArea.Height);

    internal static bool HasUsefulVisibleArea(Rectangle bounds, IEnumerable<Rectangle> workingAreas)
    {
        const int minimumVisibleEdge = 64;
        return workingAreas.Any(workingArea =>
        {
            var visible = Rectangle.Intersect(bounds, workingArea);
            return visible.Width >= minimumVisibleEdge && visible.Height >= minimumVisibleEdge;
        });
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
        if (target is null || !IsApprovedExternal(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { DesktopLaunch.ShowError("Could not open the selected page.\n\n" + ex.Message); }
    }

    internal static bool IsApprovedExternal(string? target)
    {
        if (target is null) return false;
        if (new[] { "steam://install/896660", "https://www.minecraft.net/en-us/eula",
                "https://www.microsoft.com/en-us/privacy/privacystatement" }
            .Contains(target, StringComparer.OrdinalIgnoreCase)) return true;
        const string prefix = "https://github.com/daltonstates/TogetherServer/releases/tag/";
        return target.StartsWith(prefix, StringComparison.Ordinal) && target.Length <= prefix.Length + 33 &&
            AppUpdater.ValidReleaseNotesLink(target, target[prefix.Length..]);
    }

    private void HideToTray(Form window, NotifyIcon tray)
    {
        window.Hide();
        window.ShowInTaskbar = false;
        if (trayHintShown) return;
        trayHintShown = true;
        NotifyCore(displayName + " is still running", "Open it from the tray icon. Right-click the icon to quit.",
            false, "Lifecycle", null, null, new("settings", "app"));
    }

    private static Icon CreateTrayIcon(bool staging)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(AccentColor))
        using (var foreground = new SolidBrush(AccentInkColor))
        using (var font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (staging) graphics.FillRectangle(background, 2, 2, 28, 28);
            else graphics.FillEllipse(background, 1, 1, 30, 30);
            graphics.DrawString(staging ? "D" : "T", font, foreground, new RectangleF(0, 1, 32, 30), centered);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private async Task<bool> FlushDraftsForQuitAsync(Form window)
    {
        // No editor could have been used before the first successful local render.
        if (!rendered) return true;
        var bridge = uiBridge;
        if (closing || window.IsDisposed || bridge is null ||
            !draftFlush.TryBegin(out var requestId, out var completion)) return false;
        try
        {
            bridge.PostWebMessageAsJson(DesktopDraftFlushGate.RequestJson(requestId));
            return await completion.WaitAsync(DraftFlushDeadline);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or COMException)
        { return false; }
        finally { draftFlush.Cancel(requestId); }
    }

    private async Task RequestQuitAsync(Form window)
    {
        if (requestingQuit || closing || window.IsDisposed) return;
        requestingQuit = true;
        try
        {
            // Flush before calling /quit: protected draft writes need the local
            // API's mode gate, which the guarded quit endpoint also acquires.
            if (!await FlushDraftsForQuitAsync(window))
            {
                if (!closing && !window.IsDisposed)
                {
                    await ShowAsync();
                    MessageBox.Show(window,
                        "TogetherServer is still open because unfinished edits could not be confirmed as protected drafts. Review the draft message in the window, retry saving it, or deliberately discard it before quitting.",
                        "TogetherServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            if (closing || window.IsDisposed) return;
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
        private DesktopWindowPlacement? rememberedPlacement;

        public ChromeForm(bool startupHidden, DesktopWindowPlacement? placement = null)
        {
            suppressActivation = startupHidden;
            centerBeforeReveal = startupHidden;
            rememberedPlacement = startupHidden ? placement : null;
        }

        public void AllowActivation() => suppressActivation = false;

        public void PrepareForReveal()
        {
            suppressActivation = false;
            if (rememberedPlacement is { } saved)
            {
                rememberedPlacement = null;
                centerBeforeReveal = false;
                var restored = QolLocalPreferences.ClampWindowBounds(saved, Screen.AllScreens.Select(screen => screen.WorkingArea));
                MinimumSize = new Size(Math.Min(380, restored.Width), Math.Min(560, restored.Height));
                WindowState = FormWindowState.Normal;
                Bounds = restored;
                if (saved.Maximized) MaximizeWithinWorkingArea();
                return;
            }
            if (!centerBeforeReveal && HasUsefulVisibleArea(Bounds, Screen.AllScreens.Select(screen => screen.WorkingArea)))
            {
                if (WindowState == FormWindowState.Normal)
                {
                    var current = QolLocalPreferences.ClampWindowBounds(new(Left, Top, Width, Height),
                        Screen.AllScreens.Select(screen => screen.WorkingArea));
                    MinimumSize = new Size(Math.Min(380, current.Width), Math.Min(560, current.Height));
                    Bounds = current;
                }
                return;
            }
            centerBeforeReveal = false;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            var centered = QolLocalPreferences.ClampWindowBounds(new(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2), Width, Height), [workingArea]);
            MinimumSize = new Size(Math.Min(380, centered.Width), Math.Min(560, centered.Height));
            Bounds = centered;
        }

        protected override bool ShowWithoutActivation => suppressActivation;

        public void MaximizeWithinWorkingArea()
        {
            var screen = Screen.FromControl(this);
            MaximizedBounds = RelativeMaximizedBounds(screen.Bounds, screen.WorkingArea);
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
