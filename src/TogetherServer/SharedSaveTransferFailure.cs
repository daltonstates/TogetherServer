using System.ComponentModel;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed class SaveTransferPermissionChangedException() : OperationCanceledException("Save receiving permission changed.");

// Fixed diagnostics only: exception messages, endpoint identities and paths
// never become job/history/export text. Context is drawn from closed enums.
internal sealed record SharedSaveTransferFailure(string Code, string Message, string Operation,
    string Phase, string Error, bool Pending)
{
    private const string Paused = "The approved peer save transfer paused when its bounded request ended. Existing staged bytes were kept for a later authorized pass.";
    private const string Budget = "The approved peer save transfer reached this pass's work budget. Existing staged bytes were kept for the next authorized background pass.";
    private const string Timeout = "The approved peer save request timed out. Existing staged bytes were kept; a later authorized pass may retry.";
    private const string Unreachable = "The approved peer could not be reached for this save request. Existing verified saves and staged bytes were kept.";
    private const string Permission = "Save receiving permission changed during the peer transfer. The incomplete copy was kept; review current access before retrying.";
    private const string Authentication = "The peer connection could not be authenticated. Review its approved connection before retrying the save transfer.";
    private const string Busy = "The approved peer temporarily refused this save request. Existing staged bytes were kept for a later authorized pass.";
    private const string Rejected = "The approved peer rejected this save request. Review its current connection and transfer result before retrying.";
    private const string History = "The peer save's signed history did not verify. Existing saves were kept; review the protected histories before retrying.";
    private const string Invalid = "The peer save response did not match the expected data. Existing verified saves were kept; the incomplete copy is unavailable for hosting.";
    private const string Verification = "The peer save payload did not pass verification. Existing verified saves were kept; the incomplete copy is unavailable for hosting.";
    private const string StorageDenied = "The peer save could not be stored because local file access was denied. Existing verified saves were kept; review storage access before retrying.";
    private const string Storage = "The peer save could not finish local file storage or reading. Existing verified saves were kept; review available space and storage before retrying.";
    private const string FileBusy = "The peer save file is temporarily in use by another local operation. Existing staged bytes were kept for a later authorized pass.";
    private const string Space = "The peer save could not finish because local storage is full. Existing verified saves were kept; free space before retrying.";
    private const string Unexpected = "The peer save transfer stopped unexpectedly. Existing verified saves were kept; review its typed operation and phase before retrying.";

    internal static bool IsFixedMessage(string value) => value is Paused or Budget or Timeout or Unreachable or Permission or
        Authentication or Busy or Rejected or History or Invalid or Verification or StorageDenied or Storage or FileBusy or Space or Unexpected or
        "The approved peer transfer did not finish. Existing verified saves were kept; a later background check may retry.";

    internal static SharedSaveTransferFailure Classify(Exception error, CancellationToken cancellationToken,
        WorldPeerTransferOperation operation = WorldPeerTransferOperation.None,
        WorldPeerTransferPhase phase = WorldPeerTransferPhase.Pulling, bool workBudget = false)
    {
        var outcome = error switch
        {
            SaveTransferPermissionChangedException => ("SaveTransferPermissionChanged", Permission, WorldPeerTransferError.Unauthorized, false),
            OperationCanceledException when workBudget => ("SaveTransferWorkBudget", Budget, WorldPeerTransferError.None, true),
            OperationCanceledException when cancellationToken.IsCancellationRequested => ("SaveTransferPaused", Paused, WorldPeerTransferError.Canceled, true),
            TaskCanceledException { InnerException: TimeoutException } => ("SaveTransferTimedOut", Timeout, WorldPeerTransferError.Timeout, true),
            OperationCanceledException => ("SaveTransferPermissionChanged", Permission, WorldPeerTransferError.Unauthorized, false),
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                ("SaveTransferAuthFailed", Authentication, WorldPeerTransferError.Unauthorized, false),
            // A peer endpoint also returns 403 for rejected data or storage
            // errors. Only a local proven-permission subtype implies revocation.
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                ("SaveTransferHttpRejected", Rejected, WorldPeerTransferError.PeerRejected, false),
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } =>
                ("SaveTransferRemoteBusy", Busy, WorldPeerTransferError.HttpRejected, true),
            HttpRequestException { StatusCode: not null } => ("SaveTransferHttpRejected", Rejected, WorldPeerTransferError.HttpRejected, false),
            HttpRequestException => ("SaveTransferUnreachable", Unreachable, WorldPeerTransferError.Unreachable, true),
            AuthenticationException => ("SaveTransferAuthFailed", Authentication, WorldPeerTransferError.Unauthorized, false),
            CryptographicException => ("SaveTransferInvalidHistory", History, WorldPeerTransferError.InvalidHistory, false),
            InvalidDataException when phase is WorldPeerTransferPhase.VerifyingLocal or WorldPeerTransferPhase.VerifyingRemote =>
                ("SaveTransferVerificationFailed", Verification, WorldPeerTransferError.VerificationFailed, false),
            InvalidDataException or JsonException or FormatException or ArgumentException =>
                ("SaveTransferInvalidData", Invalid, WorldPeerTransferError.InvalidResponse, false),
            UnauthorizedAccessException => ("SaveTransferStorageDenied", StorageDenied, WorldPeerTransferError.SourceUnavailable, false),
            IOException when StorageError(error) is 32 or 33 => ("SaveTransferFileBusy", FileBusy, WorldPeerTransferError.SourceUnavailable, true),
            IOException when StorageError(error) == 112 => ("SaveTransferInsufficientSpace", Space, WorldPeerTransferError.Capacity, false),
            IOException => ("SaveTransferStorageFailed", Storage, WorldPeerTransferError.SourceUnavailable, false),
            _ => ("SaveTransferUnexpectedFailure", Unexpected, WorldPeerTransferError.Unexpected, false)
        };
        return new(outcome.Item1, outcome.Item2,
            Enum.IsDefined(operation) ? operation.ToString() : WorldPeerTransferOperation.None.ToString(),
            Enum.IsDefined(phase) ? phase.ToString() : WorldPeerTransferPhase.Queued.ToString(), outcome.Item3.ToString(), outcome.Item4);
    }

    private static int StorageError(Exception error) => error.InnerException is Win32Exception native
        ? native.NativeErrorCode
        : (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : 0;
}
