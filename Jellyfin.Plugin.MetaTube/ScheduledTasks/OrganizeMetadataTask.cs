using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using MediaBrowser.Controller.Sorting;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class OrganizeMetadataTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public OrganizeMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<OrganizeMetadataTask>();
        _libraryManager = libraryManager;
    }
#else
    public OrganizeMetadataTask(ILogger<OrganizeMetadataTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.ProviderName}OrganizeMetadata";

    public string Name => "Organize Metadata";

    public string Description => $"Organizes video metadata provided by {Plugin.ProviderName} in library.";

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if __EMBY__
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        await Task.Yield();

        progress?.Report(0);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.ProviderId },
            IncludeItemTypes = new[] { nameof(Movie) },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.ProviderId, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        var stateStore = new BadgeStateStore();
        if (stateStore.LastLoadError != null)
        {
            _logger.Warn("Failed to load subtitle badge state store: {0}", stateStore.LastLoadError.Message);
        }

        var thumbStateStore = new BadgeStateStore(BadgeStateStore.DefaultThumbStateFilePath);
        if (thumbStateStore.LastLoadError != null)
        {
            _logger.Warn("Failed to load thumbnail subtitle badge state store: {0}",
                thumbStateStore.LastLoadError.Message);
        }

        var validIds = new HashSet<string>(items.Select(i => i.Id.ToString()), StringComparer.OrdinalIgnoreCase);
        stateStore.Prune(validIds);
        thumbStateStore.Prune(validIds);

        try
        {
            foreach (var (idx, item) in items.WithIndex())
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)idx / items.Count * 100);

                var pid = item.GetPid(Plugin.ProviderId);
                var hasSubtitle = SubtitleMatcher.HasChineseSubtitle(item);
                var genres = item.Genres?.ToList() ?? new List<string>();

                // 1. Reconcile ChineseSubtitle genre independently (case-insensitive).
                var hasChineseGenre = genres.Contains(SubtitleMatcher.ChineseSubtitle, StringComparer.OrdinalIgnoreCase);
                if (hasSubtitle && !hasChineseGenre)
                {
                    genres.Add(SubtitleMatcher.ChineseSubtitle);
                }
                else if (!hasSubtitle && hasChineseGenre)
                {
                    genres.RemoveAll(s => s.Equals(SubtitleMatcher.ChineseSubtitle, StringComparison.OrdinalIgnoreCase));
                }

                // 2. Reconcile primary image badge state machine.
                var imageChanged = false;
                var thumbImageChanged = false;
                var itemId = item.Id.ToString();
                BadgeReconciliationResult badgeResult = null;
                BadgeReconciliationResult thumbBadgeResult = null;

                if (!string.IsNullOrWhiteSpace(pid.Id) && !string.IsNullOrWhiteSpace(pid.Provider))
                {
                    try
                    {
                        var currentImagePath = item.GetImageInfo(ImageType.Primary, 0)?.Path;
                        var existingState = stateStore.GetState(itemId);
                        var badgesEnabled = Plugin.Instance.Configuration.EnableBadges;
                        var currentHash = existingState != null || (badgesEnabled && hasSubtitle)
                            ? BadgeStateMachine.ComputeFileHashSafely(currentImagePath)
                            : null;
                        var badge = string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.BadgeUrl)
                            ? "zimu.png"
                            : Plugin.Instance.Configuration.BadgeUrl;

                        var evalCtx = new BadgeReconciliationContext
                        {
                            ExpectedBadgedUrl = ApiClient.GetPrimaryImageApiUrl(
                                pid.Provider, pid.Id, pid.Position ?? -1, badge),
                            ExpectedUnbadgedUrl = ApiClient.GetPrimaryImageApiUrl(
                                pid.Provider, pid.Id, pid.Position ?? -1, string.Empty),
                            CurrentImagePath = currentImagePath,
                            CurrentImageHash = currentHash,
                            HasSubtitle = hasSubtitle,
                            EnableBadges = badgesEnabled,
                            ExistingState = existingState
                        };

                        badgeResult = BadgeStateMachine.Evaluate(evalCtx);

                        switch (badgeResult.Action)
                        {
                            case BadgeAction.SetPrimaryImage:
                                SetPrimaryImage(item, badgeResult.TargetImageUrl);
                                imageChanged = true;
                                break;

                            case BadgeAction.UpdateStateOnly:
                                ApplyStateTransition(stateStore, itemId, badgeResult);
                                break;

                            case BadgeAction.RemoveState:
                                ApplyStateTransition(stateStore, itemId, badgeResult);
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.Error("Reconcile badge for video {0}: {1}", item.Name, e.Message);
                    }

                    try
                    {
                        var currentThumbPath = item.GetImageInfo(ImageType.Thumb, 0)?.Path;
                        var existingThumbState = thumbStateStore.GetState(itemId);
                        var badgesEnabled = Plugin.Instance.Configuration.EnableBadges;
                        var currentThumbHash = existingThumbState != null || (badgesEnabled && hasSubtitle)
                            ? BadgeStateMachine.ComputeFileHashSafely(currentThumbPath)
                            : null;
                        var badge = string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.BadgeUrl)
                            ? "zimu.png"
                            : Plugin.Instance.Configuration.BadgeUrl;

                        var thumbEvalCtx = new BadgeReconciliationContext
                        {
                            ExpectedBadgedUrl = ApiClient.GetThumbImageApiUrl(
                                pid.Provider, pid.Id, badge: badge),
                            ExpectedUnbadgedUrl = ApiClient.GetThumbImageApiUrl(
                                pid.Provider, pid.Id, badge: string.Empty),
                            CurrentImagePath = currentThumbPath,
                            CurrentImageHash = currentThumbHash,
                            HasSubtitle = hasSubtitle,
                            EnableBadges = badgesEnabled,
                            ExistingState = existingThumbState
                        };

                        thumbBadgeResult = BadgeStateMachine.Evaluate(thumbEvalCtx);

                        switch (thumbBadgeResult.Action)
                        {
                            case BadgeAction.SetPrimaryImage:
                                thumbImageChanged = await SetAndWarmThumbImageAsync(
                                    item, thumbBadgeResult.TargetImageUrl, cancellationToken).ConfigureAwait(false);

                                // ConvertImageToLocal completed the download before the metadata
                                // write, so re-evaluate against the actual local file. This avoids
                                // recording a thumbnail as applied merely because Emby/Jellyfin
                                // still had an older landscape.jpg on disk.
                                if (thumbImageChanged)
                                {
                                    var localizedThumbPath = item.GetImageInfo(ImageType.Thumb, 0)?.Path;
                                    thumbBadgeResult = BadgeStateMachine.Evaluate(new BadgeReconciliationContext
                                    {
                                        ExpectedBadgedUrl = thumbEvalCtx.ExpectedBadgedUrl,
                                        ExpectedUnbadgedUrl = thumbEvalCtx.ExpectedUnbadgedUrl,
                                        CurrentImagePath = localizedThumbPath,
                                        CurrentImageHash = BadgeStateMachine.ComputeFileHashSafely(localizedThumbPath),
                                        HasSubtitle = hasSubtitle,
                                        EnableBadges = badgesEnabled,
                                        ExistingState = thumbBadgeResult.NewState
                                    });
                                }
                                break;

                            case BadgeAction.UpdateStateOnly:
                                ApplyStateTransition(thumbStateStore, itemId, thumbBadgeResult);
                                break;

                            case BadgeAction.RemoveState:
                                ApplyStateTransition(thumbStateStore, itemId, thumbBadgeResult);
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.Error("Reconcile thumbnail badge for video {0}: {1}", item.Name, e.Message);
                    }
                }
                else
                {
                    stateStore.RemoveState(itemId);
                    thumbStateStore.RemoveState(itemId);
                }

                // 3. Reconcile Genres and ordering.
                var orderedGenres =
                    (Plugin.Instance.Configuration.EnableGenreSubstitution
                        ? Plugin.Instance.Configuration.GetGenreSubstitutionTable().Substitute(genres)
                        : genres).Distinct().OrderByString(genre => genre).ToList();

                var genresChanged = !orderedGenres.SequenceEqual(
                    item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                if (genresChanged)
                {
                    item.Genres = orderedGenres.ToArray();
                }

                // 4. Persist item once if either image or genres changed.
                if (!imageChanged && !thumbImageChanged && !genresChanged)
                    continue;

                _logger.Info("Organize metadata for video: {0}", item.Name);

#if __EMBY__
                _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
#else
                await _libraryManager
                    .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
#endif

                // A SetImage plan becomes durable only after the item update succeeds.
                // This prevents a failed metadata write from leaving a false Pending state.
                if (imageChanged)
                    ApplyStateTransition(stateStore, itemId, badgeResult);

                if (thumbImageChanged)
                    ApplyStateTransition(thumbStateStore, itemId, thumbBadgeResult);
            }
        }
        finally
        {
            try
            {
                stateStore.Save();
                if (!string.IsNullOrWhiteSpace(stateStore.LastRecoveryBackupPath))
                {
                    _logger.Warn("Recovered subtitle badge state; unreadable input was preserved at {0}",
                        stateStore.LastRecoveryBackupPath);
                }
            }
            catch (Exception e)
            {
                _logger.Error("Failed to persist subtitle badge state: {0}", e.Message);
            }

            try
            {
                thumbStateStore.Save();
                if (!string.IsNullOrWhiteSpace(thumbStateStore.LastRecoveryBackupPath))
                {
                    _logger.Warn("Recovered thumbnail subtitle badge state; unreadable input was preserved at {0}",
                        thumbStateStore.LastRecoveryBackupPath);
                }
            }
            catch (Exception e)
            {
                _logger.Error("Failed to persist thumbnail subtitle badge state: {0}", e.Message);
            }
        }

        progress?.Report(100);
    }

    #region Helper

    private static void SetPrimaryImage(BaseItem item, string imageUrl)
    {
        item.SetImage(new ItemImageInfo
        {
            Path = imageUrl,
            Type = ImageType.Primary
        }, 0);
    }

    /// <summary>
    /// Downloads a remote thumbnail into the media server's normal image location before
    /// persisting it. SetImage alone only records a URL and relies on a later UI request or
    /// metadata refresh to fetch it; that made the scheduled task falsely complete early.
    /// </summary>
    private async Task<bool> SetAndWarmThumbImageAsync(
        BaseItem item,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        var remoteImage = new ItemImageInfo
        {
            Path = imageUrl,
            Type = ImageType.Thumb
        };

#if __EMBY__
        var localizedImage = await _libraryManager
            .ConvertImageToLocal(item, remoteImage, 0, cancellationToken)
            .ConfigureAwait(false);
#else
        var localizedImage = await _libraryManager
            .ConvertImageToLocal(item, remoteImage, 0, removeOnFailure: false)
            .ConfigureAwait(false);
#endif

        if (localizedImage == null || string.IsNullOrWhiteSpace(localizedImage.Path))
        {
            _logger.Warn("Unable to warm thumbnail image for video {0}", item.Name);
            return false;
        }

        item.SetImage(localizedImage, 0);
        return true;
    }

    private static void ApplyStateTransition(
        BadgeStateStore stateStore,
        string itemId,
        BadgeReconciliationResult result)
    {
        if (result == null)
            return;

        if (result.ShouldRemoveState || result.Action == BadgeAction.RemoveState)
        {
            stateStore.RemoveState(itemId);
        }
        else if (result.NewState != null)
        {
            stateStore.SetState(itemId, result.NewState);
        }
    }

    #endregion
}
