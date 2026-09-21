using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TogetherServer;

internal sealed class DesktopWindow
{
    private const string RuntimeDownload = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
    private readonly Uri address;
    private readonly string browserDataDirectory;
    private readonly Action stopApplication;
    private readonly TaskCompletionSource<bool> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form? form;
    private bool closing;
    private bool requestingQuit;
    private int started;
    private int fileDialogOpen;
    private volatile bool rendered;
    private volatile bool visible;

    public DesktopWindow(Uri address, string dataDirectory, Action stopApplication)
    {
        this.address = address;
        this.stopApplication = stopApplication;
        browserDataDirectory = Path.Combine(dataDirectory, "webview2");
    }

    public bool Visible => visible;
    public bool Rendered => rendered;
    public bool FileDialogOpen => Volatile.Read(ref fileDialogOpen) != 0;
    public bool CustomChrome => form is ChromeForm;

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
        try
        {
            target.BeginInvoke(new Action(() =>
            {
                if (target.WindowState == FormWindowState.Minimized) target.WindowState = FormWindowState.Normal;
                target.Show();
                target.BringToFront();
                target.Activate();
            }));
            return true;
        }
        catch (InvalidOperationException) { return false; }
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
                MinimumSize = new Size(800, 600),
                BackColor = Color.FromArgb(40, 51, 44),
                FormBorderStyle = FormBorderStyle.None,
                Padding = new Padding(1)
            };
            form = window;
            var content = BuildChrome(window);
            window.FormClosing += (_, eventArgs) =>
            {
                if (closing || eventArgs.CloseReason == CloseReason.WindowsShutDown) return;
                eventArgs.Cancel = true;
                if (!requestingQuit) _ = RequestQuitAsync(window);
            };
            window.Shown += (_, _) =>
            {
                visible = true;
                shown.TrySetResult(true);
                _ = LoadGuiAsync(window, content);
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
            BackColor = Color.FromArgb(16, 22, 19),
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleBar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(17, 25, 20), Margin = Padding.Empty };
        var mark = new Label
        {
            Text = "T",
            ForeColor = Color.FromArgb(19, 33, 23),
            BackColor = Color.FromArgb(172, 216, 137),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(11, 8),
            Size = new Size(26, 26)
        };
        var title = new Label
        {
            Text = "TogetherServer",
            ForeColor = Color.FromArgb(232, 237, 232),
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
        close.ForeColor = Color.FromArgb(222, 231, 222);
        close.MouseEnter += (_, _) => close.BackColor = Color.FromArgb(160, 52, 52);
        close.MouseLeave += (_, _) => close.BackColor = Color.Transparent;
        minimize.MouseEnter += ChromeHover;
        minimize.MouseLeave += ChromeLeave;
        maximize.MouseEnter += ChromeHover;
        maximize.MouseLeave += ChromeLeave;
        minimize.Click += (_, _) => window.WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => ToggleMaximize(window, maximize);
        close.Click += (_, _) => { if (!requestingQuit) _ = RequestQuitAsync(window); };

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
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 22, 19), Margin = Padding.Empty };
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
            ForeColor = Color.FromArgb(186, 200, 188),
            Font = new Font("Segoe UI Symbol", 11),
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(52, 79, 57);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 70, 48);
        return button;
    }

    private static void ChromeHover(object? sender, EventArgs _) =>
        ((Button)sender!).BackColor = Color.FromArgb(43, 70, 48);

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
            ForeColor = Color.FromArgb(216, 240, 205),
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
                        var value = await view.ExecuteScriptAsync("document.querySelector('.brand strong')?.textContent ?? ''");
                        if (JsonSerializer.Deserialize<string>(value) == "TogetherServer")
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
            BackColor = Color.FromArgb(172, 216, 137)
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
        if (!string.Equals(target, "steam://install/896660", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo("steam://install/896660") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { DesktopLaunch.ShowError("Steam could not open its installation page.\n\n" + ex.Message); }
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
                MessageBox.Show(window, message, "TogetherServer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (!closing && !window.IsDisposed)
                MessageBox.Show(window, "TogetherServer could not close yet.\n\n" + ex.Message,
                    "TogetherServer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { requestingQuit = false; }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

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
