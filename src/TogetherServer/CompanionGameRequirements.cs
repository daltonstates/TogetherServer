namespace TogetherServer;

// The assigned server remains the public contract identity while the reviewed
// requirements come from its exact currently managed local continuation.
internal static class CompanionGameRequirements
{
    internal static async Task<GameRequirementsResult> ReadAsync(Guid assignedProfileId,
        Func<CancellationToken, Task<Guid>> resolveTarget,
        Func<Guid, CancellationToken, Task<GameRequirementsResult>> read,
        CancellationToken cancellationToken)
    {
        var targetId = await resolveTarget(cancellationToken);
        var result = await read(targetId, cancellationToken);
        if (await resolveTarget(cancellationToken) != targetId)
            return GameCompatibility.Failure("ProfileChanged", "The current local Host changed while reading requirements. Refresh server status and try again.");
        if (result.Requirements is not { } requirements) return result;
        if (requirements.ProfileId != targetId)
            return GameCompatibility.Failure("InvalidRequirements", "The game requirements did not match the selected local server.");
        return result with { Requirements = requirements with { ProfileId = assignedProfileId } };
    }
}
