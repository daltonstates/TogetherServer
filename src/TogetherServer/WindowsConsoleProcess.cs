using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TogetherServer;

internal static class WindowsConsoleProcess
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartUseShowWindow = 0x00000001;

    public static int Start(string executable, IReadOnlyList<string> arguments, string? steamAppId = null,
        string? workingDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console launch is required.");
        // Windows inherits the parent's Ctrl+C ignore flag into a child even
        // when the child gets a new console. Ensure the server can receive Stop.
        if (!SetConsoleCtrlHandler(IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable Ctrl+C for the server process.");
        var command = new StringBuilder(Quote(executable));
        foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = StartUseShowWindow, ShowWindow = 0 };
        var environment = IntPtr.Zero;
        try
        {
            if (steamAppId is not null)
            {
                var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
                values["SteamAppId"] = steamAppId;
                var block = new StringBuilder();
                foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
                block.Append('\0');
                environment = Marshal.StringToHGlobalUni(block.ToString());
            }
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    CreateNewConsole | (environment == IntPtr.Zero ? 0 : CreateUnicodeEnvironment),
                    environment, workingDirectory ?? Path.GetDirectoryName(executable)!, ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the selected server executable.");
            CloseHandle(created.Thread);
            CloseHandle(created.Process);
            return created.ProcessId;
        }
        finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
    }

    // Ctrl+C is sent only to the new console created for this exact managed process.
    public static void RequestCtrlC(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console stop is required.");
        var processId = process.Id;
        var previous = ConsoleMembers().FirstOrDefault(id => id != Environment.ProcessId);
        FreeConsole();
        try
        {
            if (!AttachConsole((uint)processId))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach to the managed server console.");
            try
            {
                var members = ConsoleMembers();
                if (members.Length != 2 || !members.Contains(processId) || !members.Contains(Environment.ProcessId))
                    throw new InvalidOperationException("Server console contains another process; no stop signal was sent.");
                if (!SetConsoleCtrlHandler(IntPtr.Zero, true))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the Host from Ctrl+C.");
                try
                {
                    if (!GenerateConsoleCtrlEvent(0, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not send Ctrl+C to the managed console.");
                    // Delivery is asynchronous. Keep the Host's ignore handler in place until
                    // the game exits; removing it immediately can terminate TogetherServer.
                    if (!process.WaitForExit(90000))
                        throw new TimeoutException("Server did not exit after Ctrl+C; no force stop was sent.");
                    Thread.Sleep(250);
                }
                finally { SetConsoleCtrlHandler(IntPtr.Zero, false); }
            }
            finally { FreeConsole(); }
        }
        finally
        {
            if (previous != 0) AttachConsole((uint)previous);
        }
    }

    // Minecraft servers expose a fixed `stop` console action. Send only that
    // literal to the console containing the exact managed process.
    public static void RequestStopCommand(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console stop is required.");
        var previous = ConsoleMembers().FirstOrDefault(id => id != Environment.ProcessId);
        FreeConsole();
        try
        {
            if (!AttachConsole((uint)process.Id))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach to the managed server console.");
            try
            {
                var members = ConsoleMembers();
                if (members.Length != 2 || !members.Contains(process.Id) || !members.Contains(Environment.ProcessId))
                    throw new InvalidOperationException("Server console contains another process; no stop command was sent.");
                var input = CreateFileW("CONIN$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (input == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the managed server console input.");
                try
                {
                    var keys = "stop\r".SelectMany(character => new[]
                    {
                        new ConsoleInputRecord { EventType = 1, Key = new ConsoleKeyEvent
                            { KeyDown = true, RepeatCount = 1, VirtualKeyCode = character == '\r' ? (ushort)13 : (ushort)char.ToUpperInvariant(character), UnicodeChar = character } },
                        new ConsoleInputRecord { EventType = 1, Key = new ConsoleKeyEvent
                            { KeyDown = false, RepeatCount = 1, VirtualKeyCode = character == '\r' ? (ushort)13 : (ushort)char.ToUpperInvariant(character), UnicodeChar = character } }
                    }).ToArray();
                    if (!WriteConsoleInputW(input, keys, (uint)keys.Length, out var written) || written != keys.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not send the managed server stop command.");
                    if (!process.WaitForExit(90000))
                        throw new TimeoutException("Server did not exit after its stop command; no force stop was sent.");
                    Thread.Sleep(250);
                }
                finally { CloseHandle(input); }
            }
            finally { FreeConsole(); }
        }
        finally { if (previous != 0) AttachConsole((uint)previous); }
    }

    public static uint ExitCode(IntPtr processHandle)
    {
        if (!GetExitCodeProcess(processHandle, out var code))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the server exit code.");
        return code;
    }

    private static int[] ConsoleMembers()
    {
        var ids = new uint[64];
        var count = GetConsoleProcessList(ids, (uint)ids.Length);
        return count > ids.Length ? [] : ids.Take((int)count).Select(id => (int)id).ToArray();
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct ConsoleInputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public ConsoleKeyEvent Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConsoleKeyEvent
    {
        [MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string workingDirectory, ref StartupInfo startup, out ProcessInformation created);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string name, uint access, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInputW(IntPtr input, ConsoleInputRecord[] records, uint length, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processIds, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
