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

            // 2. Reconcile primary image badge independently using image path as idempotency marker.
            var imageChanged = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(pid.Provider) && !string.IsNullOrWhiteSpace(pid.Id))
                {
                    var badge = string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.BadgeUrl)
                        ? "zimu.png"
                        : Plugin.Instance.Configuration.BadgeUrl;
                    var expectedBadgedUrl = ApiClient.GetPrimaryImageApiUrl(
                        pid.Provider, pid.Id, pid.Position ?? -1, badge);
                    var expectedUnbadgedUrl = ApiClient.GetPrimaryImageApiUrl(
                        pid.Provider, pid.Id, pid.Position ?? -1, string.Empty);
                    var currentImagePath = item.GetImageInfo(ImageType.Primary, 0)?.Path;

                    if (SubtitleMatcher.ShouldReconcilePrimaryImage(
                            currentImagePath,
                            expectedBadgedUrl,
                            expectedUnbadgedUrl,
                            hasSubtitle,
                            Plugin.Instance.Configuration.EnableBadges,
                            out var targetImageUrl))
                    {
                        SetPrimaryImage(item, targetImageUrl);
                        imageChanged = true;
                    }
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

            // 4. Persist item if either image or genres changed.
            if (!imageChanged && !genresChanged)
                continue;

            _logger.Info("Organize metadata for video: {0}", item.Name);

#if __EMBY__
            _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
#else
            await _libraryManager
                .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
#endif
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

    #endregion
}
