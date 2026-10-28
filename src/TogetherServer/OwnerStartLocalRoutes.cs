namespace TogetherServer;

// Owner Start validates the local role and exact request under the mode gate.
// Save discovery, transfer and verification own their existing per-world gates;
// they must leave unrelated local controls able to enter the app mode gate.
internal static class OwnerStartLocalRoutes
{
    internal static async Task<IResult> StartAsync<T>(SemaphoreSlim modeGate, CancellationToken cancellationToken,
        Func<IResult?> initialRejection, Func<bool> startAllowed,
        Func<Func<bool>, Task<T>> start, Func<Task<IResult?>>? validate = null)
    {
        await modeGate.WaitAsync(cancellationToken);
        try
        {
            if (initialRejection() is { } rejection) return rejection;
            if (validate is not null && await validate() is { } invalid) return invalid;
        }
        finally { modeGate.Release(); }

        cancellationToken.ThrowIfCancellationRequested();
        // The same explicit instruction retains its cancellation lifetime and
        // passes a live permit through to the final manager launch fences.
        return Results.Json(await start(startAllowed));
    }
}
