using System.Diagnostics;
using System.Drawing;
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
            using var window = new Form
            {
                Text = "TogetherServer",
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1180, 820),
                MinimumSize = new Size(800, 600),
                BackColor = Color.FromArgb(16, 22, 19)
            };
            form = window;
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
                _ = LoadGuiAsync(window);
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

    private async Task LoadGuiAsync(Form window)
    {
        var loading = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(216, 240, 205),
            BackColor = window.BackColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15),
            Text = "Opening TogetherServer..."
        };
        window.Controls.Add(loading);
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: browserDataDirectory);
            if (window.IsDisposed) return;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = window.BackColor };
            window.Controls.Add(view);
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
                ShowLoadError(window, loading, "TogetherServer loaded its local page, but the interface did not render. Close the app and try again.", false);
            };
            view.Source = address;
            view.BringToFront();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowLoadError(window, loading, "Microsoft Edge WebView2 Runtime is needed to display TogetherServer. Install it from Microsoft, then reopen this app.", true);
        }
        catch (Exception ex)
        {
            ShowLoadError(window, loading, "TogetherServer could not display its interface.\n\n" + ex.Message, false);
        }
    }

    private static void ShowLoadError(Form window, Label loading, string message, bool offerRuntimeLink)
    {
        if (window.IsDisposed) return;
        window.Controls.Clear();
        loading.Text = message;
        window.Controls.Add(loading);
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
        window.Controls.Add(button);
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
}
