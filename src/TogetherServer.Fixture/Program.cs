using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

// A disposable stand-in for a game server. It never reads or writes a world.
var pipeIndex = Array.IndexOf(args, "--stop-pipe");
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeIndex + 1]))
{
    Console.Error.WriteLine("Fixture requires --stop-pipe.");
    return 2;
}

using var pipe = new NamedPipeServerStream(args[pipeIndex + 1], PipeDirection.In, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
Guid ArgumentId(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && Guid.TryParse(args[index + 1], out var id) ? id : Guid.Empty;
}
var profileId = ArgumentId("--profile-id");
var operationId = ArgumentId("--operation-id");
using var stopping = new CancellationTokenSource();
var saveLoop = profileId != Guid.Empty && operationId != Guid.Empty ? ServeSaveAsync(stopping.Token) : Task.CompletedTask;
await pipe.WaitForConnectionAsync();
using var reader = new StreamReader(pipe);
var exitCode = await reader.ReadLineAsync() == "stop" ? 0 : 3;
stopping.Cancel();
await saveLoop;
return exitCode;

async Task ServeSaveAsync(CancellationToken token)
{
    using var process = Process.GetCurrentProcess();
    var started = process.StartTime.ToUniversalTime().Ticks;
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var save = new NamedPipeServerStream(args[pipeIndex + 1] + "-live", PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await save.WaitForConnectionAsync(token);
            using var input = new StreamReader(save, leaveOpen: true);
            var line = "";
            var character = new char[1];
            while (line.Length < 64 && await input.ReadAsync(character.AsMemory(), token) == 1 && character[0] != '\n')
                line += character[0];
            if (!line.StartsWith("save ", StringComparison.Ordinal) || !Guid.TryParseExact(line[5..], "N", out var nonce)) continue;
            // A synthetic completion only: no game/world is ever read or written.
            // The bounded delay also permits cancellation/Stop overlap checks.
            await Task.Delay(150, token);
            using var output = new StreamWriter(save, leaveOpen: true) { AutoFlush = true };
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                profileId,
                operationId,
                nonce,
                processId = Environment.ProcessId,
                startTimeUtcTicks = started,
                complete = true
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        catch (IOException) { }
    }
}
