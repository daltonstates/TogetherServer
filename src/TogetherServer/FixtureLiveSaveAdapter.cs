using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// Only the synthetic staging fixture implements completion. A real adapter must
// supply its own immutable game snapshot binding; the mutable-tree scanner will
// continue rejecting every real game, even if a command or log line matches.
internal interface IManagedLiveSaveAdapter
{
    string Game { get; }
    Task<LiveSaveCompletionEvidence> CompleteAsync(ManagedRun run, CancellationToken cancellationToken);
}

internal sealed class FixtureLiveSaveAdapter : IManagedLiveSaveAdapter
{
    public string Game => GameKinds.Fixture;
    private sealed record Reply(Guid ProfileId, Guid OperationId, Guid Nonce, int ProcessId, long StartTimeUtcTicks, bool Complete);
    public async Task<LiveSaveCompletionEvidence> CompleteAsync(ManagedRun run, CancellationToken cancellationToken)
    {
        if (run.Kind != Game || run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
            run.OperationId == Guid.Empty || run.ProfileId == Guid.Empty || run.StopRequestedUtc is not null)
            throw new InvalidDataException("An exact fixture run is required.");
        using var process = Process.GetProcessById(run.ProcessId.Value);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
            !Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The exact fixture process changed.");
        using var pipe = new NamedPipeClientStream(".", run.StopPipeName + "-live", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != run.ProcessId)
            throw new InvalidDataException("The save pipe belongs to another process.");
        var nonce = Guid.NewGuid();
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(("save " + nonce.ToString("N")).AsMemory(), cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var text = ""; var character = new char[1];
        while (text.Length < 1024)
        {
            if (await reader.ReadAsync(character.AsMemory(), cancellationToken) != 1) throw new InvalidDataException("Fixture completion missing.");
            if (character[0] == '\n') break;
            text += character[0];
        }
        if (text.Length >= 1024) throw new InvalidDataException("Fixture completion oversized.");
        var reply = JsonSerializer.Deserialize<Reply>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (reply is null || !reply.Complete || reply.ProfileId != run.ProfileId || reply.OperationId != run.OperationId ||
            reply.Nonce != nonce || reply.ProcessId != run.ProcessId || reply.StartTimeUtcTicks != run.StartTimeUtcTicks || process.HasExited)
            throw new InvalidDataException("Fixture completion belongs to another request or run.");
        return new(run.ProfileId, run.OperationId, run.ProcessId.Value, run.StartTimeUtcTicks.Value,
            LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
