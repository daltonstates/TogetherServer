using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TogetherServer;

internal sealed record MinecraftCaptureRequest(int Version, Guid OperationId,
    string ExecutablePath, string WorkingDirectory, string LogPath,
    IReadOnlyList<string> Arguments);

internal sealed record MinecraftCaptureHandshake(int Version, int ProcessId,
    long StartTimeUtcTicks, string ExecutablePath);

internal sealed record MinecraftCapturedLine(DateTimeOffset CapturedUtc, string Stream,
    string Message, bool Truncated);

internal static class MinecraftCapturedLogFrame
{
    private const string Prefix = "@TS-MINECRAFT-1";

    public static byte[] Encode(MinecraftCapturedLine line)
    {
        var stream = line.Stream switch
        {
            "Stdout" => "O",
            "Stderr" => "E",
            _ => "C"
        };
        var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(line.Message));
        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}\t{line.CapturedUtc.UtcTicks}\t{stream}\t{(line.Truncated ? 1 : 0)}\t{message}\n"));
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out MinecraftCapturedLine? line)
    {
        line = null;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        if (bytes.Length == 0 || bytes.Length > MinecraftConsoleCapture.MaximumFrameBytes) return false;
        var text = Encoding.ASCII.GetString(bytes);
        var fields = text.Split('\t', 5);
        if (fields.Length != 5 || fields[0] != Prefix ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks ||
            fields[2] is not ("O" or "E" or "C") || fields[3] is not ("0" or "1"))
            return false;
        try
        {
            var payload = Convert.FromBase64String(fields[4]);
            if (payload.Length > MinecraftConsoleCapture.MaximumCapturedLineBytes) return false;
            line = new(new DateTimeOffset(ticks, TimeSpan.Zero), fields[2] switch
            {
                "O" => "Stdout",
                "E" => "Stderr",
                _ => "Capture"
            }, Encoding.UTF8.GetString(payload), fields[3] == "1");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// This internal command is hosted by the same executable as TogetherServer. It
// owns the redirected stdout/stderr pipes for one fixed Minecraft/Terraria process so capture
// continues if the main Host process restarts. Standard input remains attached
// to the new Windows console and the existing fixed `stop` command path.
internal static class MinecraftConsoleCapture
{
    internal const string Command = "--internal-minecraft-console-capture";
    internal const string NoGameFailurePrefix = "ERROR:NO_GAME:";
    private const int MaximumCapturedLineCharacters = 4 * 1024;
    internal const int MaximumCapturedLineBytes = 16 * 1024;
    internal const int MaximumFrameBytes = 32 * 1024;
    private const int CaptureQueueCapacity = 512;
    private const long MaximumLogBytes = 8L * 1024 * 1024;
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static bool IsCommand(string[] args) =>
        args.Length == 3 && string.Equals(args[0], Command, StringComparison.Ordinal);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsCommand(args)) return 2;
        using var handshake = new NamedPipeClientStream(".", args[2], PipeDirection.Out,
            PipeOptions.None);
        try
        {
            handshake.Connect(10_000);
        }
        catch
        {
            return 4;
        }

        MinecraftCaptureRequest request;
        try
        {
            request = ReadAndValidateRequest(args[1]);
        }
        catch (Exception ex)
        {
            TryDelete(args[1]);
            TryWriteNoGameFailure(handshake, ex);
            return 3;
        }
        TryDelete(args[1]);

        var queue = Channel.CreateBounded<MinecraftCapturedLine>(new BoundedChannelOptions(CaptureQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var drops = new CaptureDropCounter();
        var sink = DrainLogAsync(request.LogPath, queue.Reader, drops);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(request.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = request.WorkingDirectory,
                CreateNoWindow = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("Minecraft process did not start.");
            var standardOutput = DrainStreamAsync(process.StandardOutput.BaseStream, "Stdout", queue.Writer, drops);
            var standardError = DrainStreamAsync(process.StandardError.BaseStream, "Stderr", queue.Writer, drops);

            var started = new MinecraftCaptureHandshake(1, process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                request.ExecutablePath);
            var handshakeBytes = JsonSerializer.SerializeToUtf8Bytes(started, Json);
            try
            {
                handshake.Write(handshakeBytes);
                handshake.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Keep draining the exact game process even if the launch
                // acknowledgement cannot reach the main Host process.
            }
            finally { handshake.Dispose(); }

            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            queue.Writer.TryComplete();
            await sink;
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (process is null) TryWriteNoGameFailure(handshake, ex);
            TryQueue(queue.Writer, drops, new(DateTimeOffset.UtcNow, "Capture",
                "Console capture became unavailable; lifecycle authority is unchanged (" +
                ex.GetType().Name + ").", false));
            queue.Writer.TryComplete();
            try { await sink; }
            catch { }
            return 5;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static MinecraftCaptureRequest CreateRequest(ManagedRun run,
        IReadOnlyList<string> arguments) => new(1, run.OperationId, run.ExecutablePath,
        run.WorldDirectory, run.LogPath, arguments);

    internal static void WriteRequest(string path, MinecraftCaptureRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (payload.Length > MaximumRequestBytes)
            throw new InvalidOperationException("Minecraft capture request is too large.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(true);
    }

    internal static MinecraftCaptureHandshake ParseHandshake(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 4096)
            throw new InvalidDataException("Minecraft capture handshake is invalid.");
        var result = JsonSerializer.Deserialize<MinecraftCaptureHandshake>(payload, Json);
        if (result is null || result.Version != 1 || result.ProcessId <= 0 ||
            result.StartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(result.ExecutablePath))
            throw new InvalidDataException("Minecraft capture handshake is invalid.");
        return result;
    }

    private static MinecraftCaptureRequest ReadAndValidateRequest(string requestPath)
    {
        var fullRequestPath = Path.GetFullPath(requestPath);
        var info = new FileInfo(fullRequestPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumRequestBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Minecraft capture request is unavailable.");
        var directory = info.Directory ?? throw new InvalidDataException("Minecraft capture directory is missing.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Minecraft capture directory cannot be a filesystem link.");
        var request = JsonSerializer.Deserialize<MinecraftCaptureRequest>(File.ReadAllBytes(fullRequestPath), Json)
            ?? throw new InvalidDataException("Minecraft capture request is invalid.");
        if (request.Version != 1 || request.OperationId == Guid.Empty || request.Arguments is null ||
            !info.Name.Equals(request.OperationId.ToString("N") + ".capture.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Minecraft capture request identity is invalid.");

        var logPath = Path.GetFullPath(request.LogPath);
        var expectedLog = Path.Combine(directory.FullName, request.OperationId.ToString("N") + ".log");
        if (!logPath.Equals(expectedLog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Minecraft capture log is not the owned operation log.");
        var executable = Path.GetFullPath(request.ExecutablePath);
        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!File.Exists(executable) || !Directory.Exists(workingDirectory))
            throw new InvalidDataException("Minecraft capture target is missing.");
        var name = Path.GetFileName(executable);
        if (name.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Arguments.Count != 3 || request.Arguments[0] != "-jar" ||
                request.Arguments[2] != "nogui" || !Path.IsPathFullyQualified(request.Arguments[1]) ||
                !File.Exists(request.Arguments[1]) ||
                !Path.GetDirectoryName(Path.GetFullPath(request.Arguments[1]))!
                    .Equals(workingDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Minecraft Java capture arguments are invalid.");
        }
        else if (name.Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!ValidTerrariaArguments(workingDirectory, request.Arguments) ||
                !File.Exists(request.Arguments[1]) ||
                request.Arguments.Count == 7 && !File.Exists(request.Arguments[6]))
                throw new InvalidDataException("Terraria capture arguments are invalid.");
        }
        else if (!name.Equals("bedrock_server.exe", StringComparison.OrdinalIgnoreCase) ||
                 request.Arguments.Count != 0)
        {
            throw new InvalidDataException("Console capture supports only fixed Java, Bedrock, or Terraria launches.");
        }
        return request with
        {
            ExecutablePath = executable,
            WorkingDirectory = workingDirectory,
            LogPath = logPath,
            Arguments = request.Arguments.ToArray()
        };
    }

    internal static bool ValidTerrariaArguments(string workingDirectory, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(workingDirectory) || arguments.Count is not (5 or 7) ||
            arguments[0] != "-world" || arguments[2] != "-port" || arguments[4] != "-noupnp" ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 || !Path.IsPathFullyQualified(arguments[1]) ||
            !Path.GetExtension(arguments[1]).Equals(".wld", StringComparison.OrdinalIgnoreCase) ||
            !ValheimSetup.ValidWorldId(Path.GetFileNameWithoutExtension(arguments[1])) ||
            !Path.GetDirectoryName(Path.GetFullPath(arguments[1]))!.Equals(Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase))
            return false;
        return arguments.Count == 5 || arguments[5] == "-config" &&
            Path.IsPathFullyQualified(arguments[6]) &&
            Path.GetFullPath(arguments[6]).Equals(Path.Combine(Path.GetFullPath(workingDirectory), "serverconfig.txt"), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainStreamAsync(Stream input, string stream,
        ChannelWriter<MinecraftCapturedLine> output, CaptureDropCounter drops)
    {
        var bytes = new byte[8192];
        var chars = new char[8192];
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var line = new StringBuilder(512);
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(bytes);
                if (read == 0) break;
                var consumed = 0;
                while (consumed < read)
                {
                    decoder.Convert(bytes.AsSpan(consumed, read - consumed), chars, false,
                        out var usedBytes, out var usedChars, out _);
                    consumed += usedBytes;
                    Consume(chars.AsSpan(0, usedChars), stream, line, ref truncated, output, drops);
                }
            }
            decoder.Convert(ReadOnlySpan<byte>.Empty, chars, true, out _, out var finalChars, out _);
            Consume(chars.AsSpan(0, finalChars), stream, line, ref truncated, output, drops);
            if (line.Length > 0 || truncated)
                Emit(stream, line, truncated, output, drops);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            TryQueue(output, drops, new(DateTimeOffset.UtcNow, "Capture",
                stream + " capture ended unexpectedly; lifecycle authority is unchanged.", false));
        }
    }

    private static void Consume(ReadOnlySpan<char> characters, string stream, StringBuilder line,
        ref bool truncated, ChannelWriter<MinecraftCapturedLine> output, CaptureDropCounter drops)
    {
        foreach (var character in characters)
        {
            if (character == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                Emit(stream, line, truncated, output, drops);
                line.Clear();
                truncated = false;
            }
            else if (line.Length < MaximumCapturedLineCharacters)
            {
                line.Append(character);
            }
            else
            {
                truncated = true;
            }
        }
    }

    private static void Emit(string stream, StringBuilder line, bool truncated,
        ChannelWriter<MinecraftCapturedLine> output, CaptureDropCounter drops) =>
        TryQueue(output, drops, new(DateTimeOffset.UtcNow, stream, line.ToString(), truncated));

    private static void TryQueue(ChannelWriter<MinecraftCapturedLine> output,
        CaptureDropCounter drops, MinecraftCapturedLine line)
    {
        if (!output.TryWrite(line)) Interlocked.Increment(ref drops.Count);
    }

    private static async Task DrainLogAsync(string path, ChannelReader<MinecraftCapturedLine> input,
        CaptureDropCounter drops)
    {
        FileStream? stream = null;
        var capped = false;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(0, SeekOrigin.End);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep consuming and discarding. A display-log failure must never
            // backpressure or become authority over the managed game process.
        }

        try
        {
            var pendingFlush = 0;
            await foreach (var line in input.ReadAllAsync())
            {
                var dropped = Interlocked.Exchange(ref drops.Count, 0);
                if (dropped > 0)
                {
                    await WriteFrame(new(DateTimeOffset.UtcNow, "Capture",
                        $"{dropped} console lines were dropped while the bounded capture queue was full.", true));
                    pendingFlush++;
                }
                await WriteFrame(line);
                pendingFlush++;
                if (stream is not null && (pendingFlush >= 64 || !input.TryPeek(out _)))
                {
                    await stream.FlushAsync();
                    pendingFlush = 0;
                }
            }
            var finalDropped = Interlocked.Exchange(ref drops.Count, 0);
            if (finalDropped > 0)
                await WriteFrame(new(DateTimeOffset.UtcNow, "Capture",
                    $"{finalDropped} console lines were dropped while the bounded capture queue was full.", true));
            if (stream is not null) await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // The reader tasks have already drained the process pipes into a
            // bounded channel. A failed file sink is intentionally nonfatal.
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
        }

        async Task WriteFrame(MinecraftCapturedLine line)
        {
            if (stream is null || capped) return;
            var frame = MinecraftCapturedLogFrame.Encode(line);
            var marker = MinecraftCapturedLogFrame.Encode(new(DateTimeOffset.UtcNow, "Capture",
                "Console capture reached its bounded 8 MiB limit. Later output was drained but not retained.", true));
            if (stream.Position + frame.Length + marker.Length <= MaximumLogBytes)
            {
                await stream.WriteAsync(frame);
                return;
            }
            if (stream.Position + marker.Length <= MaximumLogBytes) await stream.WriteAsync(marker);
            capped = true;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryWriteNoGameFailure(Stream handshake, Exception failure)
    {
        var payload = Encoding.UTF8.GetBytes(NoGameFailurePrefix + failure.GetType().Name);
        try
        {
            handshake.Write(payload);
            handshake.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private sealed class CaptureDropCounter
    {
        public int Count;
    }
}
