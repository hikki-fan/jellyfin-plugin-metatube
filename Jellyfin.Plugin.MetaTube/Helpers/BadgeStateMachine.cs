using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Helpers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BadgeStateStatus
{
    None = 0,
    PendingDownload = 1,
    Applied = 2
}

public sealed class ItemBadgeState
{
    [JsonPropertyName("expectedBadgedUrl")]
    public string ExpectedBadgedUrl { get; set; }

    [JsonPropertyName("originalLocalHash")]
    public string OriginalLocalHash { get; set; }

    [JsonPropertyName("appliedLocalHash")]
    public string AppliedLocalHash { get; set; }

    [JsonPropertyName("status")]
    public BadgeStateStatus Status { get; set; }
}

public sealed class BadgeReconciliationContext
{
    public string ExpectedBadgedUrl { get; set; }

    public string ExpectedUnbadgedUrl { get; set; }

    public string CurrentImagePath { get; set; }

    public string CurrentImageHash { get; set; }

    public bool HasSubtitle { get; set; }

    public bool EnableBadges { get; set; }

    public ItemBadgeState ExistingState { get; set; }
}

public enum BadgeAction
{
    None = 0,
    SetPrimaryImage = 1,
    UpdateStateOnly = 2,
    RemoveState = 3
}

public sealed class BadgeReconciliationResult
{
    public BadgeAction Action { get; init; }

    public string TargetImageUrl { get; init; }

    public ItemBadgeState NewState { get; init; }

    public bool ShouldRemoveState { get; init; }

    public string Reason { get; init; }
}

/// <summary>
/// Plans subtitle-badge changes without mutating an Emby/Jellyfin item.
/// </summary>
public static class BadgeStateMachine
{
    public static BadgeReconciliationResult Evaluate(BadgeReconciliationContext context)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.ExpectedBadgedUrl) ||
            string.IsNullOrWhiteSpace(context.ExpectedUnbadgedUrl))
        {
            return NoAction("Missing expected image URL");
        }

        var expectedBadgedUrl = context.ExpectedBadgedUrl;
        var expectedUnbadgedUrl = context.ExpectedUnbadgedUrl;
        var currentPath = context.CurrentImagePath;
        var currentHash = context.CurrentImageHash;
        var state = context.ExistingState;
        var isLocal = !string.IsNullOrWhiteSpace(currentPath) && !IsRemoteUrl(currentPath);
        var stateMatchesConfiguration = state != null && string.Equals(
            state.ExpectedBadgedUrl, expectedBadgedUrl, StringComparison.Ordinal);

        if (context.EnableBadges && context.HasSubtitle)
        {
            if (stateMatchesConfiguration && state.Status == BadgeStateStatus.PendingDownload)
            {
                if (string.Equals(currentPath, expectedBadgedUrl, StringComparison.Ordinal))
                {
                    return NoAction("Waiting for the media server to localize the badged image");
                }

                if (isLocal && !string.IsNullOrWhiteSpace(currentHash))
                {
                    // The path changing from the persisted remote URL back to a local file is
                    // the durable signal that Emby/Jellyfin localized it. The bytes can be
                    // identical during migration from an older plugin build, so hash inequality
                    // must not be required here.
                    return UpdateState(AppliedState(expectedBadgedUrl, currentHash),
                        "The media server localized the badged image");
                }
            }

            if (stateMatchesConfiguration && state.Status == BadgeStateStatus.Applied)
            {
                if (isLocal && FingerprintsEqual(currentHash, state.AppliedLocalHash))
                {
                    return NoAction("The localized badged image is unchanged");
                }

                if (string.Equals(currentPath, expectedBadgedUrl, StringComparison.Ordinal))
                {
                    return UpdateState(PendingState(expectedBadgedUrl, state.AppliedLocalHash),
                        "A refreshed remote badged image is waiting to be localized");
                }
            }

            if (string.Equals(currentPath, expectedBadgedUrl, StringComparison.Ordinal))
            {
                return UpdateState(PendingState(expectedBadgedUrl, null),
                    "Record an existing remote badged image");
            }

            return new BadgeReconciliationResult
            {
                Action = BadgeAction.SetPrimaryImage,
                TargetImageUrl = expectedBadgedUrl,
                NewState = PendingState(expectedBadgedUrl, isLocal ? currentHash : null),
                Reason = "Apply the badged primary image"
            };
        }

        if (SubtitleMatcher.IsBadgedVersionOf(currentPath, expectedUnbadgedUrl))
        {
            return RemoveBadge(expectedUnbadgedUrl, "Remove the owned remote badge");
        }

        if (state != null)
        {
            if (isLocal && !string.IsNullOrWhiteSpace(currentHash))
            {
                var isAppliedImage = state.Status == BadgeStateStatus.Applied &&
                                     FingerprintsEqual(currentHash, state.AppliedLocalHash);
                var isLocalizedPendingImage = state.Status == BadgeStateStatus.PendingDownload &&
                                              (string.IsNullOrWhiteSpace(state.OriginalLocalHash) ||
                                               !FingerprintsEqual(currentHash, state.OriginalLocalHash));

                if (isAppliedImage || isLocalizedPendingImage)
                {
                    return RemoveBadge(expectedUnbadgedUrl, "Remove the localized plugin badge");
                }
            }

            return new BadgeReconciliationResult
            {
                Action = BadgeAction.RemoveState,
                ShouldRemoveState = true,
                Reason = "Forget stale badge state while preserving the current image"
            };
        }

        return NoAction("No badge reconciliation is needed");
    }

    public static string ComputeFileHashSafely(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: false);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ItemBadgeState PendingState(string expectedBadgedUrl, string originalLocalHash)
    {
        return new ItemBadgeState
        {
            ExpectedBadgedUrl = expectedBadgedUrl,
            OriginalLocalHash = originalLocalHash,
            Status = BadgeStateStatus.PendingDownload
        };
    }

    private static ItemBadgeState AppliedState(string expectedBadgedUrl, string appliedLocalHash)
    {
        return new ItemBadgeState
        {
            ExpectedBadgedUrl = expectedBadgedUrl,
            AppliedLocalHash = appliedLocalHash,
            Status = BadgeStateStatus.Applied
        };
    }

    private static BadgeReconciliationResult UpdateState(ItemBadgeState state, string reason)
    {
        return new BadgeReconciliationResult
        {
            Action = BadgeAction.UpdateStateOnly,
            NewState = state,
            Reason = reason
        };
    }

    private static BadgeReconciliationResult RemoveBadge(string expectedUnbadgedUrl, string reason)
    {
        return new BadgeReconciliationResult
        {
            Action = BadgeAction.SetPrimaryImage,
            TargetImageUrl = expectedUnbadgedUrl,
            ShouldRemoveState = true,
            Reason = reason
        };
    }

    private static BadgeReconciliationResult NoAction(string reason)
    {
        return new BadgeReconciliationResult
        {
            Action = BadgeAction.None,
            Reason = reason
        };
    }

    private static bool FingerprintsEqual(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteUrl(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
