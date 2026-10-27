using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    private sealed class PeerTransferObservation(WorldPeerTransferObservationStore.Attempt attempt)
    {
        internal WorldPeerTransferObservationStore.Attempt Attempt { get; } = attempt;
        internal Guid? ResponderDeviceId;
        internal string? ResponderPublicKey;

        internal bool ReceiptFromResponder(Group current, SharedWorldReceipt receipt) =>
            receipt.DeviceId == ResponderDeviceId && ResponderPublicKey is not null && (receipt.DeviceId == Guid.Empty
                ? ResponderPublicKey == current.Root.OwnerPublicKey && SharedWorldReceiptTrust.VerifyPeerOwner(receipt, current.Root)
                : SharedWorldReceiptTrust.Verify(receipt, ResponderPublicKey));
    }

    private static WorldPeerTransferOperation TransferOperation(string operation) => operation switch
    {
        WorldPeerOperations.Metadata => WorldPeerTransferOperation.Metadata,
        WorldPeerOperations.Catalog => WorldPeerTransferOperation.Catalog,
        WorldPeerOperations.Version => WorldPeerTransferOperation.Manifest,
        WorldPeerOperations.Chunk => WorldPeerTransferOperation.Chunk,
        WorldPeerOperations.Seed => WorldPeerTransferOperation.Seed,
        WorldPeerOperations.Receipt => WorldPeerTransferOperation.Receipt,
        _ => WorldPeerTransferOperation.None
    };

    private static void ObserveTransferFailure(WorldPeerTransferObservationStore.Attempt observation,
        Exception error, CancellationToken cancellationToken)
    {
        var reason = error switch
        {
            TaskCanceledException { InnerException: TimeoutException } when !cancellationToken.IsCancellationRequested => WorldPeerTransferError.Timeout,
            OperationCanceledException when !cancellationToken.IsCancellationRequested => WorldPeerTransferError.Unauthorized,
            OperationCanceledException => WorldPeerTransferError.Canceled,
            HttpRequestException { StatusCode: not null } => WorldPeerTransferError.HttpRejected,
            HttpRequestException => WorldPeerTransferError.Unreachable,
            AuthenticationException => WorldPeerTransferError.Unauthorized,
            CryptographicException => WorldPeerTransferError.InvalidHistory,
            InvalidDataException or JsonException or FormatException or ArgumentException => WorldPeerTransferError.InvalidResponse,
            IOException => WorldPeerTransferError.SourceUnavailable,
            _ => WorldPeerTransferError.Unexpected
        };
        if (error is HttpRequestException { StatusCode: { } status }) observation.Response((int)status);
        observation.Fail(reason, reason == WorldPeerTransferError.Canceled
            ? WorldPeerTransferResult.Canceled : WorldPeerTransferResult.Failed);
    }
}
