using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

public sealed class BadgeReconciliationTests
{
    private const string UnbadgedUrl =
        "http://metatube:8080/v1/images/primary/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&quality=90";

    private const string BadgedUrl =
        "http://metatube:8080/v1/images/primary/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&badge=zimu.png&quality=90";

    private const string ThumbUnbadgedUrl =
        "http://metatube:8080/v1/images/thumb/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&quality=90";

    private const string ThumbBadgedUrl =
        "http://metatube:8080/v1/images/thumb/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&badge=zimu.png&quality=90";

    [Fact]
    public void ThreeRunLifecycle_BecomesIdempotentAfterLocalization()
    {
        var first = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "original", state: null));

        Assert.Equal(BadgeAction.SetPrimaryImage, first.Action);
        Assert.Equal(BadgedUrl, first.TargetImageUrl);
        Assert.Equal(BadgeStateStatus.PendingDownload, first.NewState.Status);
        Assert.Equal("original", first.NewState.OriginalLocalHash);

        var second = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "badged", state: first.NewState));

        Assert.Equal(BadgeAction.UpdateStateOnly, second.Action);
        Assert.Equal(BadgeStateStatus.Applied, second.NewState.Status);
        Assert.Equal("badged", second.NewState.AppliedLocalHash);

        var third = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "badged", state: second.NewState));

        Assert.Equal(BadgeAction.None, third.Action);
        Assert.Null(third.NewState);
    }

    [Fact]
    public void ThumbnailLifecycle_AppliesLocalizesAndRemovesOwnedBadge()
    {
        var first = BadgeStateMachine.Evaluate(new BadgeReconciliationContext
        {
            ExpectedBadgedUrl = ThumbBadgedUrl,
            ExpectedUnbadgedUrl = ThumbUnbadgedUrl,
            CurrentImagePath = "/metadata/thumb.jpg",
            CurrentImageHash = "original-thumb",
            HasSubtitle = true,
            EnableBadges = true
        });

        Assert.Equal(BadgeAction.SetPrimaryImage, first.Action);
        Assert.Equal(ThumbBadgedUrl, first.TargetImageUrl);

        var localized = BadgeStateMachine.Evaluate(new BadgeReconciliationContext
        {
            ExpectedBadgedUrl = ThumbBadgedUrl,
            ExpectedUnbadgedUrl = ThumbUnbadgedUrl,
            CurrentImagePath = "/metadata/thumb.jpg",
            CurrentImageHash = "badged-thumb",
            HasSubtitle = true,
            EnableBadges = true,
            ExistingState = first.NewState
        });

        Assert.Equal(BadgeAction.UpdateStateOnly, localized.Action);
        Assert.Equal(BadgeStateStatus.Applied, localized.NewState.Status);

        var removed = BadgeStateMachine.Evaluate(new BadgeReconciliationContext
        {
            ExpectedBadgedUrl = ThumbBadgedUrl,
            ExpectedUnbadgedUrl = ThumbUnbadgedUrl,
            CurrentImagePath = "/metadata/thumb.jpg",
            CurrentImageHash = "badged-thumb",
            HasSubtitle = false,
            EnableBadges = true,
            ExistingState = localized.NewState
        });

        Assert.Equal(BadgeAction.SetPrimaryImage, removed.Action);
        Assert.Equal(ThumbUnbadgedUrl, removed.TargetImageUrl);
        Assert.True(removed.ShouldRemoveState);
    }

    [Fact]
    public void Localization_WithIdenticalBytes_IsTreatedAsApplied()
    {
        var pending = new ItemBadgeState
        {
            ExpectedBadgedUrl = BadgedUrl,
            OriginalLocalHash = "same-hash",
            Status = BadgeStateStatus.PendingDownload
        };

        var result = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "same-hash", state: pending));

        Assert.Equal(BadgeAction.UpdateStateOnly, result.Action);
        Assert.Equal(BadgeStateStatus.Applied, result.NewState.Status);
        Assert.Equal("same-hash", result.NewState.AppliedLocalHash);
    }

    [Fact]
    public void ExistingExpectedRemoteUrl_IsRecordedOnceThenWaits()
    {
        var first = BadgeStateMachine.Evaluate(Context(BadgedUrl, null, state: null));

        Assert.Equal(BadgeAction.UpdateStateOnly, first.Action);
        Assert.Equal(BadgeStateStatus.PendingDownload, first.NewState.Status);

        var second = BadgeStateMachine.Evaluate(Context(BadgedUrl, null, state: first.NewState));

        Assert.Equal(BadgeAction.None, second.Action);
    }

    [Fact]
    public void AppliedPosterReplacement_ReappliesBadge()
    {
        var state = AppliedState("badged");

        var result = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "replacement", state: state));

        Assert.Equal(BadgeAction.SetPrimaryImage, result.Action);
        Assert.Equal("replacement", result.NewState.OriginalLocalHash);
        Assert.Equal(BadgeStateStatus.PendingDownload, result.NewState.Status);
    }

    [Fact]
    public void BadgeConfigurationChange_ReappliesNewBadge()
    {
        const string customBadgedUrl =
            "http://metatube:8080/v1/images/primary/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&badge=custom.png&quality=90";
        var context = Context("/metadata/poster.jpg", "badged", state: AppliedState("badged"));
        context.ExpectedBadgedUrl = customBadgedUrl;

        var result = BadgeStateMachine.Evaluate(context);

        Assert.Equal(BadgeAction.SetPrimaryImage, result.Action);
        Assert.Equal(customBadgedUrl, result.TargetImageUrl);
    }

    [Theory]
    [InlineData(null, "localized", BadgeAction.SetPrimaryImage)]
    [InlineData("original", "localized", BadgeAction.SetPrimaryImage)]
    [InlineData("original", "original", BadgeAction.RemoveState)]
    public void SubtitleRemoval_HandlesPendingLocalImageSafely(
        string originalHash,
        string currentHash,
        BadgeAction expectedAction)
    {
        var pending = new ItemBadgeState
        {
            ExpectedBadgedUrl = BadgedUrl,
            OriginalLocalHash = originalHash,
            Status = BadgeStateStatus.PendingDownload
        };

        var result = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", currentHash, hasSubtitle: false, state: pending));

        Assert.Equal(expectedAction, result.Action);
        Assert.True(result.ShouldRemoveState);
        if (expectedAction == BadgeAction.SetPrimaryImage)
            Assert.Equal(UnbadgedUrl, result.TargetImageUrl);
    }

    [Fact]
    public void SubtitleRemoval_RevertsAppliedImageButPreservesReplacement()
    {
        var state = AppliedState("badged");

        var owned = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "badged", hasSubtitle: false, state: state));
        var replacement = BadgeStateMachine.Evaluate(Context(
            "/metadata/poster.jpg", "custom", hasSubtitle: false, state: state));

        Assert.Equal(BadgeAction.SetPrimaryImage, owned.Action);
        Assert.Equal(UnbadgedUrl, owned.TargetImageUrl);
        Assert.Equal(BadgeAction.RemoveState, replacement.Action);
    }

    [Fact]
    public void BadgeDisabled_RevertsOwnedRemoteUrlWithoutTouchingUnrelatedUrl()
    {
        var owned = BadgeStateMachine.Evaluate(Context(
            BadgedUrl, null, enableBadges: false, state: null));
        var unrelated = BadgeStateMachine.Evaluate(Context(
            "https://example.com/v1/images/primary/JavDB/DE6Xa?badge=zimu.png",
            null,
            enableBadges: false,
            state: AppliedState("badged")));

        Assert.Equal(BadgeAction.SetPrimaryImage, owned.Action);
        Assert.Equal(UnbadgedUrl, owned.TargetImageUrl);
        Assert.Equal(BadgeAction.RemoveState, unrelated.Action);
    }

    [Fact]
    public void StateStore_SavesLoadsPrunesAndPreservesCorruptInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"metatube-badge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "state.json");

        try
        {
            var store = new BadgeStateStore(statePath);
            store.SetState("item-1", AppliedState("hash-1"));
            store.SetState("item-2", AppliedState("hash-2"));
            store.Save();

            var loaded = new BadgeStateStore(statePath);
            Assert.Null(loaded.LastLoadError);
            Assert.Equal("hash-1", loaded.GetState("ITEM-1").AppliedLocalHash);

            loaded.Prune(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "item-1" });
            loaded.Save();
            Assert.Null(new BadgeStateStore(statePath).GetState("item-2"));

            const string corruptJson = "{not-json";
            File.WriteAllText(statePath, corruptJson);
            var recovered = new BadgeStateStore(statePath);
            Assert.NotNull(recovered.LastLoadError);
            recovered.SetState("recovered", AppliedState("new-hash"));
            recovered.Save();

            Assert.NotNull(recovered.LastRecoveryBackupPath);
            Assert.Equal(corruptJson, File.ReadAllText(recovered.LastRecoveryBackupPath));
            Assert.Equal("new-hash", new BadgeStateStore(statePath)
                .GetState("recovered").AppliedLocalHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrimaryAndThumbnailStateStores_AreIndependent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"metatube-badge-stores-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primaryPath = Path.Combine(directory, "primary.json");
        var thumbPath = Path.Combine(directory, "thumb.json");

        try
        {
            var primaryStore = new BadgeStateStore(primaryPath);
            var thumbStore = new BadgeStateStore(thumbPath);
            primaryStore.SetState("item-1", AppliedState("primary-hash"));
            thumbStore.SetState("item-1", new ItemBadgeState
            {
                ExpectedBadgedUrl = ThumbBadgedUrl,
                AppliedLocalHash = "thumb-hash",
                Status = BadgeStateStatus.Applied
            });
            primaryStore.Save();
            thumbStore.Save();

            var loadedPrimary = new BadgeStateStore(primaryPath).GetState("item-1");
            var loadedThumb = new BadgeStateStore(thumbPath).GetState("item-1");

            Assert.Equal(BadgedUrl, loadedPrimary.ExpectedBadgedUrl);
            Assert.Equal("primary-hash", loadedPrimary.AppliedLocalHash);
            Assert.Equal(ThumbBadgedUrl, loadedThumb.ExpectedBadgedUrl);
            Assert.Equal("thumb-hash", loadedThumb.AppliedLocalHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ThumbnailStatePath_UsesV2StoreWithoutChangingPrimaryStore()
    {
        Assert.EndsWith("subtitle-badge-state.json", BadgeStateStore.DefaultStateFilePath,
            StringComparison.Ordinal);
        Assert.EndsWith("subtitle-thumb-badge-state-v2.json", BadgeStateStore.DefaultThumbStateFilePath,
            StringComparison.Ordinal);
        Assert.False(string.Equals(BadgeStateStore.DefaultStateFilePath, BadgeStateStore.DefaultThumbStateFilePath,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FileFingerprint_IsStableWhileSharedForReadWriteDelete()
    {
        var file = Path.Combine(Path.GetTempPath(), $"metatube-hash-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
            var expected = BadgeStateMachine.ComputeFileHashSafely(file);

            using var shared = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var actual = BadgeStateMachine.ComputeFileHashSafely(file);

            Assert.NotNull(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static BadgeReconciliationContext Context(
        string currentPath,
        string currentHash,
        bool hasSubtitle = true,
        bool enableBadges = true,
        ItemBadgeState state = null)
    {
        return new BadgeReconciliationContext
        {
            ExpectedBadgedUrl = BadgedUrl,
            ExpectedUnbadgedUrl = UnbadgedUrl,
            CurrentImagePath = currentPath,
            CurrentImageHash = currentHash,
            HasSubtitle = hasSubtitle,
            EnableBadges = enableBadges,
            ExistingState = state
        };
    }

    private static ItemBadgeState AppliedState(string hash)
    {
        return new ItemBadgeState
        {
            ExpectedBadgedUrl = BadgedUrl,
            AppliedLocalHash = hash,
            Status = BadgeStateStatus.Applied
        };
    }
}
