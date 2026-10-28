namespace TogetherServer;

// Quit intent blocks launch before the request can wait behind lifecycle work.
// Each request releases only its own pending intent. Accepted shutdown remains
// permanent, including after all HTTP requests have finished.
internal sealed class AppShutdownIntent(object launchGate)
{
    private int pendingRequests;
    private bool stopping;

    // Read the lease count first: Accept publishes stopping before the lease
    // is released, so that handoff cannot transiently look available.
    internal bool IsPending => Volatile.Read(ref pendingRequests) != 0 || Volatile.Read(ref stopping);

    internal bool IsStopping => Volatile.Read(ref stopping);

    internal QuitRequest BeginQuit()
    {
        // Do not retain this gate while waiting for modeGate or the manager.
        // Managed launch already holds this same shared mutation gate.
        lock (launchGate) Volatile.Write(ref pendingRequests, pendingRequests + 1);
        return new(this);
    }

    internal void MarkStopping()
    {
        lock (launchGate) Volatile.Write(ref stopping, true);
    }

    private void Release()
    {
        lock (launchGate) Volatile.Write(ref pendingRequests, pendingRequests - 1);
    }

    internal sealed class QuitRequest(AppShutdownIntent owner) : IDisposable
    {
        private AppShutdownIntent? retained = owner;

        internal void Accept()
        {
            var current = Volatile.Read(ref retained) ?? throw new ObjectDisposedException(nameof(QuitRequest));
            current.MarkStopping();
        }

        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }
}
