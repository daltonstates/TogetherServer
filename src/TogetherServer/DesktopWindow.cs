using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TogetherServer;

internal sealed class DesktopWindow
{
    private const string RuntimeDownload = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
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
    private bool closing;
    private bool requestingQuit;
    private bool trayHintShown;
    private volatile bool closeToTray;
    private int started;
    private int fileDialogOpen;
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
                    if (target.WindowState == FormWindowState.Minimized) target.WindowState = FormWindowState.Normal;
                    target.ShowInTaskbar = true;
                    target.Opacity = 1;
                    target.Show();
                    target.BringToFront();
                    target.Activate();
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

    private void Run()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var window = new ChromeForm
            {
                Text = "TogetherServer",
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1180, 820),
                MinimumSize = new Size(380, 560),
                BackColor = WindowBorderColor,
                FormBorderStyle = FormBorderStyle.None,
                Padding = new Padding(1),
                Opacity = startInTray ? 0 : 1,
                ShowInTaskbar = !startInTray
            };
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
                shown.TrySetResult(true);
                _ = LoadGuiAsync(window, content);
                if (startInTray)
                {
                    window.Hide();
                    window.Opacity = 1;
                }
                visible = window.Visible;
            };
            window.VisibleChanged += (_, _) => visible = window.Visible;
            Application.Run(window);
        }
        catch (Exception ex)
        {
            shown.TrySetException(ex);
            DesktopLaunch.ShowError("TogetherServer could not open its window.\n\n" + ex.Message);
            stopApplication();
        }
        finally { visible = false; }
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
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: browserDataDirectory);
            if (window.IsDisposed) return;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = content.BackColor };
            content.Controls.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            if (window.IsDisposed) return;
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
                if (!eventArgs.IsSuccess) return;
                for (var attempt = 0; attempt < 20 && !window.IsDisposed; attempt++)
                {
                    try
                    {
                        var value = await view.ExecuteScriptAsync("document.querySelector('.shell') !== null");
                        if (JsonSerializer.Deserialize<bool>(value))
                        {
                            rendered = true;
                            loading.Dispose();
                            return;
                        }
                    }
                    catch (Exception) when (window.IsDisposed) { return; }
                    catch (Exception) { /* Navigation may still be settling; retry briefly. */ }
                    await Task.Delay(100);
                }
                ShowLoadError(window, content, loading, "TogetherServer loaded its local page, but the interface did not render. Close the app and try again.", false);
            };
            view.Source = address;
            view.BringToFront();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowLoadError(window, content, loading, "Microsoft Edge WebView2 Runtime is needed to display TogetherServer. Install it from Microsoft, then reopen this app.", true);
        }
        catch (Exception ex)
        {
            ShowLoadError(window, content, loading, "TogetherServer could not display its interface.\n\n" + ex.Message, false);
        }
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
}
