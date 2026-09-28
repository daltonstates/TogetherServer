using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
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

    public static int StartMinecraftCaptured(ManagedRun run, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console launch is required.");
        if (run.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(run.LogPath))
            throw new InvalidOperationException("Minecraft capture requires an owned managed-run log.");
        var logDirectory = Path.GetDirectoryName(Path.GetFullPath(run.LogPath))!;
        var requestPath = Path.Combine(logDirectory, run.OperationId.ToString("N") + ".capture.json");
        var pipeName = "TogetherServer.MinecraftCapture." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.None);
        MinecraftConsoleCapture.WriteRequest(requestPath,
            MinecraftConsoleCapture.CreateRequest(run, arguments));
        try
        {
            var (hostExecutable, hostArguments) = CaptureHostCommand();
            hostArguments.Add(MinecraftConsoleCapture.Command);
            hostArguments.Add(requestPath);
            hostArguments.Add(pipeName);
            var captureId = Start(hostExecutable, hostArguments,
                workingDirectory: Path.GetDirectoryName(hostExecutable));
            run.ConsoleCaptureProcessId = captureId;
            using var capture = Process.GetProcessById(captureId);
            var (captureStartTimeUtcTicks, captureExecutablePath) =
                ReadNewProcessIdentity(capture, hostExecutable);
            run.ConsoleCaptureStartTimeUtcTicks = captureStartTimeUtcTicks;
            run.ConsoleCaptureExecutablePath = captureExecutablePath;

            var connection = Task.Run(pipe.WaitForConnection);
            if (Task.WhenAny(connection, Task.Delay(TimeSpan.FromSeconds(15)))
                    .GetAwaiter().GetResult() != connection)
            {
                pipe.Dispose();
                _ = connection.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new TimeoutException("Minecraft capture host did not connect in time.");
            }
            connection.GetAwaiter().GetResult();
            var readHandshake = Task.Run(() => ReadCaptureHandshake(pipe));
            if (Task.WhenAny(readHandshake, Task.Delay(TimeSpan.FromSeconds(15)))
                    .GetAwaiter().GetResult() != readHandshake)
            {
                pipe.Dispose();
                _ = readHandshake.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new TimeoutException("Minecraft capture host did not acknowledge the game process in time.");
            }
            var handshakePayload = readHandshake.GetAwaiter().GetResult();
            var handshakeText = Encoding.UTF8.GetString(handshakePayload);
            if (handshakeText.StartsWith(MinecraftConsoleCapture.NoGameFailurePrefix, StringComparison.Ordinal))
            {
                TryClearExitedMinecraftCapture(run, capture, captureStartTimeUtcTicks,
                    captureExecutablePath);
                throw new InvalidDataException("Minecraft capture host rejected the fixed launch before starting a game process.");
            }
            if (handshakeText.StartsWith("ERROR:", StringComparison.Ordinal))
                throw new InvalidDataException("Minecraft capture host rejected the fixed launch.");
            var handshake = MinecraftConsoleCapture.ParseHandshake(handshakePayload);
            run.ProcessId = handshake.ProcessId;
            using var process = Process.GetProcessById(handshake.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != handshake.StartTimeUtcTicks ||
                !Path.GetFullPath(process.MainModule!.FileName)
                    .Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(handshake.ExecutablePath)
                    .Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Minecraft capture returned an unexpected managed process identity.");
            return handshake.ProcessId;
        }
        finally
        {
            try { if (File.Exists(requestPath)) File.Delete(requestPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (long StartTimeUtcTicks, string ExecutablePath) ReadNewProcessIdentity(
        Process process, string expectedExecutablePath)
    {
        // CreateProcess has returned the exact PID, but Windows can briefly
        // expose a null MainModule while the image is still initializing.
        // Retry only that PID and require its expected executable; never fall
        // back to a process name or a different process.
        var expected = Path.GetFullPath(expectedExecutablePath);
        var wait = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                    throw new InvalidDataException(
                        "Minecraft capture host exited before its exact identity could be recorded.");
                var started = process.StartTime.ToUniversalTime().Ticks;
                var module = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(module))
                {
                    var executable = Path.GetFullPath(module);
                    if (!executable.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Minecraft capture host identity could not be verified.");
                    return (started, executable);
                }
            }
            catch (Exception ex) when (ex is not InvalidDataException &&
                                       ex is InvalidOperationException or Win32Exception or
                                           NotSupportedException or IOException)
            {
                lastFailure = ex;
            }

            if (wait.Elapsed >= TimeSpan.FromSeconds(2))
                throw new InvalidDataException(
                    "Minecraft capture host identity was not available in time.", lastFailure);
            Thread.Sleep(50);
        }
    }

    internal static bool TryClearExitedMinecraftCapture(ManagedRun run, Process capture,
        long observedStartTimeUtcTicks, string observedExecutablePath, int waitMilliseconds = 5000)
    {
        if (run.ProcessId is not null || run.ConsoleCaptureProcessId != capture.Id ||
            run.ConsoleCaptureStartTimeUtcTicks is null ||
            string.IsNullOrWhiteSpace(run.ConsoleCaptureExecutablePath)) return false;
        try
        {
            if (observedStartTimeUtcTicks != run.ConsoleCaptureStartTimeUtcTicks ||
                !Path.GetFullPath(observedExecutablePath).Equals(
                    Path.GetFullPath(run.ConsoleCaptureExecutablePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !capture.WaitForExit(waitMilliseconds)) return false;
            run.ConsoleCaptureProcessId = null;
            run.ConsoleCaptureStartTimeUtcTicks = null;
            run.ConsoleCaptureExecutablePath = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or
                                   NotSupportedException or IOException)
        {
            return false;
        }
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
            AttachManagedConsole(process, processId);
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
    public static void RequestStopCommand(Process process, ManagedRun run)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console stop is required.");
        var previous = ConsoleMembers().FirstOrDefault(id => id != Environment.ProcessId);
        FreeConsole();
        try
        {
            AttachManagedConsole(process, process.Id);
            try
            {
                var members = ConsoleMembers();
                var allowed = new HashSet<int> { process.Id, Environment.ProcessId };
                if (CaptureIdentityMatches(run)) allowed.Add(run.ConsoleCaptureProcessId!.Value);
                if (members.Length != allowed.Count || members.Any(member => !allowed.Contains(member)))
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

    private static bool CaptureIdentityMatches(ManagedRun run)
    {
        if (run.ConsoleCaptureProcessId is null || run.ConsoleCaptureStartTimeUtcTicks is null ||
            string.IsNullOrWhiteSpace(run.ConsoleCaptureExecutablePath)) return false;
        try
        {
            using var capture = Process.GetProcessById(run.ConsoleCaptureProcessId.Value);
            return !capture.HasExited &&
                capture.StartTime.ToUniversalTime().Ticks == run.ConsoleCaptureStartTimeUtcTicks &&
                Path.GetFullPath(capture.MainModule!.FileName).Equals(
                    Path.GetFullPath(run.ConsoleCaptureExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or
                                   NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static byte[] ReadCaptureHandshake(Stream pipe)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = pipe.Read(buffer, 0, buffer.Length);
            if (read == 0) return payload.ToArray();
            if (payload.Length + read > 4096)
                throw new InvalidDataException("Minecraft capture handshake is too large.");
            payload.Write(buffer, 0, read);
        }
    }

    private static (string Executable, List<string> Arguments) CaptureHostCommand()
    {
        var command = Environment.GetCommandLineArgs()[0];
        if (Path.GetExtension(command).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var host = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("The .NET host path is unavailable for Minecraft capture.");
            return Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                ? (Path.GetFullPath(host), [Path.GetFullPath(command)])
                : (Path.GetFullPath(host), []);
        }
        var executable = File.Exists(command) ? command : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("The TogetherServer executable path is unavailable for Minecraft capture.");
        return (Path.GetFullPath(executable), []);
    }

    private static void AttachManagedConsole(Process process, int processId)
    {
        // CREATE_NEW_CONSOLE returns once the process exists, but Windows may
        // need a brief moment before another process can attach to that new
        // console. This is common when Stop follows Start immediately. Retry
        // only the exact recorded process; never fall back to a named process.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var error = 0;
        while (true)
        {
            if (AttachConsole((uint)processId)) return;
            error = Marshal.GetLastWin32Error();
            process.Refresh();
            if (process.HasExited || DateTime.UtcNow >= deadline)
                throw new Win32Exception(error, "Could not attach to the managed server console.");
            Thread.Sleep(50);
        }
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
