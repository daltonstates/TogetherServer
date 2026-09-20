using System.IO.Pipes;

// A disposable stand-in for a game server. It never reads or writes a world.
var pipeIndex = Array.IndexOf(args, "--stop-pipe");
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeIndex + 1]))
{
    Console.Error.WriteLine("Fixture requires --stop-pipe.");
    return 2;
}

using var pipe = new NamedPipeServerStream(args[pipeIndex + 1], PipeDirection.In, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();
using var reader = new StreamReader(pipe);
return await reader.ReadLineAsync() == "stop" ? 0 : 3;
