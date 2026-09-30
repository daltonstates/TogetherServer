using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

public sealed record HostingPowerView(bool SettingEnabled, bool ManagedServerRunning,
    bool RequestActive, string State, string Message);

internal interface IHostingPowerGuard : IDisposable
{
    bool IsActive { get; }
    string? LastError { get; }
    void SetRequired(bool required);
}

internal sealed class NullHostingPowerGuard : IHostingPowerGuard
{
    public bool IsActive => false;
    public string? LastError => null;
    public void SetRequired(bool required) { }
    public void Dispose() { }
}

internal sealed class WindowsHostingPowerGuard : IHostingPowerGuard
{
    private const uint SimpleReasonString = 0x00000001;
    private const int PowerRequestSystemRequired = 0;
    private SafeFileHandle? handle;

    public bool IsActive { get; private set; }
    public string? LastError { get; private set; }

    public void SetRequired(bool required)
    {
        if (!required)
        {
            LastError = null;
            if (IsActive || handle is not null) ClearRequest();
            return;
        }
        if (IsActive || LastError is not null) return;

        if (!OperatingSystem.IsWindows())
        {
            LastError = "Scoped Windows power requests are unavailable on this operating system.";
            return;
        }

        try
        {
            var reason = Marshal.StringToHGlobalUni("TogetherServer is hosting a managed game server.");
            try
            {
                var context = new ReasonContext { Version = 0, Flags = SimpleReasonString, Reason = reason };
                handle = PowerCreateRequest(ref context);
            }
            finally { Marshal.FreeHGlobal(reason); }

            if (handle is null || handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a scoped power request.");
            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not activate the scoped power request.");
            IsActive = true;
            LastError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            handle?.Dispose();
            handle = null;
            IsActive = false;
            LastError = ex.Message;
        }
    }

    public void Dispose()
    {
        ClearRequest();
        GC.SuppressFinalize(this);
    }

    private void ClearRequest()
    {
        if (handle is null) { IsActive = false; return; }
        if (IsActive && !PowerClearRequest(handle, PowerRequestSystemRequired))
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        handle.Dispose();
        handle = null;
        IsActive = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint Reason;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle powerRequest, int requestType);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle powerRequest, int requestType);
}
