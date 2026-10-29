using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClearFrame.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace ClearFrame.Services
{
    public class HomeItem
    {
        public string Id { get; set; }

        /// <summary>movie, episode, program or discover.</summary>
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public int? Year { get; set; }
        public string Overview { get; set; }

        /// <summary>Emby images are server-relative ("/emby/Items/..."); Seerr images are absolute TMDB URLs.</summary>
        public string PosterUrl { get; set; }
        public string LandscapeUrl { get; set; }

        public double? ProgressPercent { get; set; }
        public long? PositionTicks { get; set; }
        public long? RuntimeTicks { get; set; }

        /// <summary>Item id to send to /ClearFrame/Play (the channel id for Live TV programs).</summary>
        public string PlayItemId { get; set; }

        /// <summary>resume, play or request.</summary>
        public string DefaultAction { get; set; }

        public string ChannelName { get; set; }
        public string ChannelNumber { get; set; }
        public string StartDate { get; set; }
        public string EndDate { get; set; }
        public bool? IsAiring { get; set; }

        public int? TmdbId { get; set; }

        /// <summary>movie or tv (Seerr items).</summary>
        public string MediaType { get; set; }

        /// <summary>none, requested, processing, partial or available (Seerr items).</summary>
        public string RequestStatus { get; set; }
    }

    public class HomeRow
    {
        public string Id { get; set; }
        public string Title { get; set; }

        /// <summary>landscape or poster.</summary>
        public string Layout { get; set; }
        public List<HomeItem> Items { get; set; } = new List<HomeItem>();
    }

    public class HomeResponse
    {
        public string TvName { get; set; }
        public string UserName { get; set; }
        public string EmbyUserId { get; set; }
        public bool RequestsEnabled { get; set; }
        public string GeneratedUtc { get; set; }
        public List<HomeRow> Rows { get; set; } = new List<HomeRow>();

        /// <summary>Non-fatal problems, e.g. Seerr unreachable. The rest of the home screen still works.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>Builds the rows for a TV's home screen.</summary>
    public class HomeBuilder
    {
        private const string TmdbImageBase = "https://image.tmdb.org/t/p/";

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IDtoService _dto;
        private readonly ITVSeriesManager _tv;
        private readonly ILiveTvManager _liveTv;
        private readonly SeerrClient _seerr;
        private readonly ILogger _logger;

        public HomeBuilder(ILibraryManager library, IUserManager users, IDtoService dto, ITVSeriesManager tv, ILiveTvManager liveTv, SeerrClient seerr, ILogger logger)
        {
            _library = library;
            _users = users;
            _dto = dto;
            _tv = tv;
            _liveTv = liveTv;
            _seerr = seerr;
            _logger = logger;
        }

        public User GetUser(TvDevice tv)
        {
            var user = string.IsNullOrWhiteSpace(tv.EmbyUserId) ? null : _users.GetUserById(tv.EmbyUserId);
            if (user == null)
            {
                throw new ArgumentException("The Emby user assigned to '" + tv.Name + "' no longer exists. Pick another one in the ClearFrame settings.");
            }

            return user;
        }

        public async Task<HomeResponse> Build(TvDevice tv, CancellationToken ct)
        {
            var config = Plugin.Instance.Configuration;
            var settings = TvSettings.Resolve(tv, config);
            var user = GetUser(tv);
            var limit = settings.RowLimit;
            var response = new HomeResponse
            {
                TvName = tv.Name,
                UserName = user.Name,
                EmbyUserId = user.Id.ToString("N"),
                RequestsEnabled = config.RequestsEnabled && _seerr.IsConfigured,
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
            };

            var dtoOptions = new DtoOptions(true)
            {
                EnableImages = true,
                ImageTypeLimit = 1,
                EnableUserData = true,
            };

            // Rows are built into this map, then emitted in the TV's chosen order.
            var built = new Dictionary<string, HomeRow>();

            // Seerr calls run in parallel with the local queries.
            var seerrTasks = new List<Tuple<string, string, Task<List<SeerrMedia>>>>();
            if (_seerr.IsConfigured)
            {
                if (settings.IsVisible(TvSettings.SeerrTrending)) seerrTasks.Add(Tuple.Create(TvSettings.SeerrTrending, "trending", SafeDiscover("trending", ct)));
                if (settings.IsVisible(TvSettings.SeerrMovies)) seerrTasks.Add(Tuple.Create(TvSettings.SeerrMovies, "movies", SafeDiscover("movies", ct)));
                if (settings.IsVisible(TvSettings.SeerrShows)) seerrTasks.Add(Tuple.Create(TvSettings.SeerrShows, "tv", SafeDiscover("tv", ct)));
            }

            var resumeIds = new HashSet<string>();
            if (settings.IsVisible(TvSettings.Continue))
            {
                AddRow(response, built, TvSettings.Continue, "landscape", () =>
                {
                    var items = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        IsResumable = true,
                        Recursive = true,
                        IncludeItemTypes = new[] { "Movie", "Episode" },
                        OrderBy = new[] { ValueTuple.Create(ItemSortBy.DatePlayed, SortOrder.Descending) },
                        Limit = limit,
                        DtoOptions = dtoOptions,
                    });

                    var mapped = _dto.GetBaseItemDtos(items, dtoOptions, user).Select(d => MapLibraryItem(d, PlayAction.Resume)).ToList();
                    foreach (var m in mapped)
                    {
                        resumeIds.Add(m.Id);
                    }

                    return mapped;
                });
            }

            if (settings.IsVisible(TvSettings.NextUp))
            {
                AddRow(response, built, TvSettings.NextUp, "landscape", () =>
                {
                    var result = _tv.GetNextUp(new NextUpQuery { UserId = user.InternalId, Limit = limit + resumeIds.Count }, user, dtoOptions);
                    var items = result?.Items ?? new BaseItem[0];
                    return _dto.GetBaseItemDtos(items, dtoOptions, user)
                        .Select(d => MapLibraryItem(d, PlayAction.Play))
                        .Where(i => !resumeIds.Contains(i.Id))
                        .Take(limit)
                        .ToList();
                });
            }

            if (settings.IsVisible(TvSettings.LiveNow))
            {
                AddRow(response, built, TvSettings.LiveNow, "landscape", () =>
                {
                    var result = _liveTv.GetRecommendedPrograms(new InternalItemsQuery(user)
                    {
                        IsAiring = true,
                        Limit = limit,
                        DtoOptions = dtoOptions,
                    }, ct);

                    return (result?.Items ?? new BaseItemDto[0]).Select(MapProgram).Where(p => p.PlayItemId != null).ToList();
                });
            }

            var teams = tv.SportsTeams ?? new string[0];
            if (settings.IsVisible(TvSettings.Sports) && teams.Length > 0)
            {
                AddRow(response, built, TvSettings.Sports, "landscape", () =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var programs = GetSportsPrograms(user, now, now.AddHours(settings.SportsLookaheadHours), dtoOptions, ct);

                    return programs
                        .Where(p => MatchesTeam(p, teams))
                        .Select(MapProgram)
                        .Where(p => p.PlayItemId != null)
                        // Same game is often on several channels; keep the first listing of each.
                        .GroupBy(p => (p.Title ?? string.Empty) + "|" + (p.Subtitle ?? string.Empty) + "|" + p.StartDate)
                        .Select(g => g.First())
                        .OrderByDescending(p => p.IsAiring == true)
                        .ThenBy(p => p.StartDate, StringComparer.Ordinal)
                        .Take(limit)
                        .ToList();
                });
            }

            foreach (var t in seerrTasks)
            {
                var results = await t.Item3.ConfigureAwait(false);
                if (results == null)
                {
                    if (!response.Warnings.Contains("Seerr is unreachable"))
                    {
                        response.Warnings.Add("Seerr is unreachable");
                    }

                    continue;
                }

                var row = new HomeRow { Id = t.Item1, Title = TvSettings.FindRow(t.Item1).Title, Layout = "poster" };
                row.Items.AddRange(results.Take(limit).Select(MapSeerr));
                if (row.Items.Count > 0)
                {
                    built[row.Id] = row;
                }
            }

            foreach (var id in settings.RowOrder)
            {
                if (built.TryGetValue(id, out var row))
                {
                    response.Rows.Add(row);
                }
            }

            return response;
        }

        /// <summary>Sports programmes in the guide between two times, soonest first.</summary>
        public BaseItemDto[] GetSportsPrograms(User user, DateTimeOffset from, DateTimeOffset to, DtoOptions dtoOptions, CancellationToken ct)
        {
            var result = _liveTv.GetPrograms(new InternalItemsQuery(user)
            {
                IsSports = true,
                MinEndDate = from,
                MaxStartDate = to,
                OrderBy = new[] { ValueTuple.Create(ItemSortBy.StartDate, SortOrder.Ascending) },
                Limit = 5000,
                DtoOptions = dtoOptions ?? new DtoOptions(false),
            }, ct);

            return result?.Items ?? new BaseItemDto[0];
        }

        private void AddRow(HomeResponse response, Dictionary<string, HomeRow> built, string id, string layout, Func<List<HomeItem>> build)
        {
            var title = TvSettings.FindRow(id).Title;
            try
            {
                var items = build();
                if (items.Count > 0)
                {
                    built[id] = new HomeRow { Id = id, Title = title, Layout = layout, Items = items };
                }
            }
            catch (Exception ex)
            {
                // One broken row (e.g. Live TV not set up) must not take down the home screen.
                _logger.ErrorException("failed to build row '{0}'", ex, id);
                response.Warnings.Add("Could not load " + title);
            }
        }

        private async Task<List<SeerrMedia>> SafeDiscover(string kind, CancellationToken ct)
        {
            try
            {
                return await _seerr.Discover(kind, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn("Seerr discover '{0}' failed: {1}", kind, ex.Message);
                return null;
            }
        }

        private static bool MatchesTeam(BaseItemDto p, string[] teams)
        {
            var haystack = string.Join(" ", p.Name, p.EpisodeTitle, p.Overview);
            return teams.Any(t => haystack.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static HomeItem MapLibraryItem(BaseItemDto d, string defaultAction)
        {
            var item = new HomeItem
            {
                Id = d.Id,
                Kind = d.Type == "Episode" ? "episode" : "movie",
                Title = d.Type == "Episode" ? (d.SeriesName ?? d.Name) : d.Name,
                Year = d.ProductionYear,
                Overview = d.Overview,
                RuntimeTicks = d.RunTimeTicks,
                PlayItemId = d.Id,
                DefaultAction = defaultAction,
            };

            if (d.Type == "Episode")
            {
                var number = d.ParentIndexNumber.HasValue && d.IndexNumber.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "S{0}:E{1} · ", d.ParentIndexNumber, d.IndexNumber)
                    : string.Empty;
                item.Subtitle = number + d.Name;
                item.LandscapeUrl = Image(d.Id, ImageType.Primary, Tag(d, ImageType.Primary))
                                    ?? Image(d.ParentThumbItemId, ImageType.Thumb, d.ParentThumbImageTag)
                                    ?? Image(d.ParentBackdropItemId, ImageType.Backdrop, d.ParentBackdropImageTags?.FirstOrDefault());
                item.PosterUrl = Image(d.SeriesId, ImageType.Primary, d.SeriesPrimaryImageTag) ?? item.LandscapeUrl;
            }
            else
            {
                item.Subtitle = d.ProductionYear?.ToString(CultureInfo.InvariantCulture);
                item.PosterUrl = Image(d.Id, ImageType.Primary, Tag(d, ImageType.Primary));
                item.LandscapeUrl = Image(d.Id, ImageType.Thumb, Tag(d, ImageType.Thumb))
                                    ?? Image(d.Id, ImageType.Backdrop, d.BackdropImageTags?.FirstOrDefault())
                                    ?? item.PosterUrl;
            }

            var ud = d.UserData;
            if (ud != null && ud.PlaybackPositionTicks > 0)
            {
                item.PositionTicks = ud.PlaybackPositionTicks;
                item.ProgressPercent = ud.PlayedPercentage.HasValue
                    ? Math.Round(ud.PlayedPercentage.Value, 1)
                    : (d.RunTimeTicks > 0 ? Math.Round(100.0 * ud.PlaybackPositionTicks / d.RunTimeTicks.Value, 1) : (double?)null);
            }

            return item;
        }

        private static HomeItem MapProgram(BaseItemDto p)
        {
            var now = DateTimeOffset.UtcNow;
            var airing = p.StartDate <= now && p.EndDate > now;
            var item = new HomeItem
            {
                Id = p.Id,
                Kind = "program",
                Title = p.Name,
                Subtitle = p.EpisodeTitle,
                Overview = p.Overview,
                ChannelName = p.ChannelName,
                ChannelNumber = p.ChannelNumber,
                StartDate = p.StartDate?.UtcDateTime.ToString("o"),
                EndDate = p.EndDate?.UtcDateTime.ToString("o"),
                IsAiring = airing,
                PlayItemId = p.ChannelId,
                DefaultAction = PlayAction.Play,
            };

            if (airing && p.StartDate.HasValue && p.EndDate.HasValue)
            {
                var total = (p.EndDate.Value - p.StartDate.Value).TotalSeconds;
                if (total > 0)
                {
                    item.ProgressPercent = Math.Round(100.0 * (now - p.StartDate.Value).TotalSeconds / total, 1);
                }
            }

            var channelLogo = Image(p.ChannelId, ImageType.Primary, p.ChannelPrimaryImageTag);
            item.LandscapeUrl = Image(p.Id, ImageType.Thumb, Tag(p, ImageType.Thumb))
                                ?? Image(p.Id, ImageType.Primary, Tag(p, ImageType.Primary))
                                ?? channelLogo;
            item.PosterUrl = Image(p.Id, ImageType.Primary, Tag(p, ImageType.Primary)) ?? channelLogo;
            return item;
        }

        private static HomeItem MapSeerr(SeerrMedia m)
        {
            var date = m.MediaType == "tv" ? m.FirstAirDate : m.ReleaseDate;
            int? year = null;
            if (!string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out var y))
            {
                year = y;
            }

            return new HomeItem
            {
                Id = m.MediaType + ":" + m.Id,
                Kind = "discover",
                Title = m.MediaType == "tv" ? (m.Name ?? m.Title) : (m.Title ?? m.Name),
                Subtitle = year?.ToString(CultureInfo.InvariantCulture),
                Year = year,
                Overview = m.Overview,
                PosterUrl = string.IsNullOrWhiteSpace(m.PosterPath) ? null : TmdbImageBase + "w342" + m.PosterPath,
                LandscapeUrl = string.IsNullOrWhiteSpace(m.BackdropPath) ? null : TmdbImageBase + "w780" + m.BackdropPath,
                TmdbId = m.Id,
                MediaType = m.MediaType,
                RequestStatus = MapStatus(m.MediaInfo?.Status ?? 0),
                DefaultAction = "request",
            };
        }

        /// <summary>Seerr MediaStatus: 1 unknown, 2 pending, 3 processing, 4 partially available, 5 available.</summary>
        private static string MapStatus(int status)
        {
            switch (status)
            {
                case 2: return "requested";
                case 3: return "processing";
                case 4: return "partial";
                case 5: return "available";
                default: return "none";
            }
        }

        private static string Tag(BaseItemDto d, ImageType type)
        {
            return d.ImageTags != null && d.ImageTags.TryGetValue(type, out var tag) ? tag : null;
        }

        private static string Image(string itemId, ImageType type, string tag)
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            return "/emby/Items/" + itemId + "/Images/" + type + "?tag=" + Uri.EscapeDataString(tag);
        }
    }
}
