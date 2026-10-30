using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lobby.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Lobby.Services
{
    public class SyncReport
    {
        public int SeerrUsers { get; set; }
        public int AutoLinked { get; set; }
        public int Suggested { get; set; }
        public int ManualKept { get; set; }
        public UserLink[] Links { get; set; }
    }

    /// <summary>Links Emby users to Seerr users so requests from a TV are made as the right person.</summary>
    public class LinkService
    {
        private static readonly TimeSpan UserCacheLifetime = TimeSpan.FromMinutes(5);

        private readonly SeerrClient _seerr;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private List<SeerrUser> _cachedUsers;
        private DateTime _cachedAt;

        public LinkService(SeerrClient seerr, IUserManager userManager, ILogger logger)
        {
            _seerr = seerr;
            _userManager = userManager;
            _logger = logger;
        }

        public async Task<List<SeerrUser>> GetSeerrUsers(CancellationToken ct, bool forceRefresh = false)
        {
            await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (forceRefresh || _cachedUsers == null || DateTime.UtcNow - _cachedAt > UserCacheLifetime)
                {
                    _cachedUsers = await _seerr.GetUsers(ct).ConfigureAwait(false);
                    _cachedAt = DateTime.UtcNow;
                }

                return _cachedUsers;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public void ClearCache()
        {
            _cachedUsers = null;
        }

        /// <summary>Seerr user ids an Emby user may request as (auto and manual links only).</summary>
        public int[] GetLinkedSeerrUserIds(string embyUserId)
        {
            var id = TvRegistry.NormalizeId(embyUserId);
            return Plugin.Instance.Configuration.UserLinks
                .Where(l => l.EmbyUserId == id && l.Source != LinkSource.Suggested)
                .Select(l => l.SeerrUserId)
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// Rebuilds automatic links from Seerr's stored Emby ids and suggests username/email matches.
        /// Manual links are never touched.
        /// </summary>
        public async Task<SyncReport> Sync(CancellationToken ct)
        {
            var seerrUsers = await GetSeerrUsers(ct, forceRefresh: true).ConfigureAwait(false);
            var embyUsers = _userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery());
            var report = new SyncReport { SeerrUsers = seerrUsers.Count };

            UserLink[] result = null;
            Plugin.Instance.Mutate(c =>
            {
                var manual = c.UserLinks.Where(l => l.Source == LinkSource.Manual).ToList();
                var links = new List<UserLink>(manual);
                report.ManualKept = manual.Count;

                // 1. Exact: Seerr already knows this account's Emby user id.
                foreach (var su in seerrUsers.Where(s => !string.IsNullOrWhiteSpace(s.JellyfinUserId)))
                {
                    var sid = TvRegistry.NormalizeId(su.JellyfinUserId);
                    var eu = embyUsers.FirstOrDefault(u => TvRegistry.NormalizeId(u.Id.ToString("N")) == sid || u.InternalId.ToString() == sid);
                    if (eu == null)
                    {
                        continue;
                    }

                    var eid = eu.Id.ToString("N");
                    if (!links.Any(l => l.EmbyUserId == eid && l.SeerrUserId == su.Id))
                    {
                        links.Add(new UserLink { EmbyUserId = eid, SeerrUserId = su.Id, Source = LinkSource.Auto, SeerrDisplayName = su.BestName });
                        report.AutoLinked++;
                    }
                }

                // 2. Suggestions: Emby users still without any link, matched on name or email.
                // A Seerr account may already be linked to another (shared) Emby login; it can still be
                // suggested for its owner's own Emby account. Seerr accounts tied to a different Emby
                // user via jellyfinUserId are skipped.
                foreach (var eu in embyUsers)
                {
                    var eid = eu.Id.ToString("N");
                    if (links.Any(l => l.EmbyUserId == eid))
                    {
                        continue;
                    }

                    var name = (eu.Name ?? string.Empty).Trim();
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    foreach (var su in seerrUsers.Where(s => string.IsNullOrWhiteSpace(s.JellyfinUserId)))
                    {
                        if (NameMatches(name, su))
                        {
                            links.Add(new UserLink { EmbyUserId = eid, SeerrUserId = su.Id, Source = LinkSource.Suggested, SeerrDisplayName = su.BestName });
                            report.Suggested++;
                        }
                    }
                }

                // Refresh cached display names.
                foreach (var l in links)
                {
                    var su = seerrUsers.FirstOrDefault(s => s.Id == l.SeerrUserId);
                    if (su != null)
                    {
                        l.SeerrDisplayName = su.BestName;
                    }
                }

                c.UserLinks = links.ToArray();
                result = c.UserLinks;
            });

            report.Links = result;
            _logger.Info("Seerr link sync found {0} Seerr users, {1} auto links, {2} suggestions, kept {3} manual links",
                report.SeerrUsers, report.AutoLinked, report.Suggested, report.ManualKept);
            return report;
        }

        /// <summary>Replaces the whole link list with what the admin page submitted.</summary>
        public UserLink[] Save(IEnumerable<UserLink> submitted)
        {
            var embyIds = new HashSet<string>(_userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery()).Select(u => u.Id.ToString("N")));
            var clean = (submitted ?? Enumerable.Empty<UserLink>())
                .Where(l => l != null && l.SeerrUserId > 0)
                .Select(l => new UserLink
                {
                    EmbyUserId = TvRegistry.NormalizeId(l.EmbyUserId),
                    SeerrUserId = l.SeerrUserId,
                    Source = l.Source == LinkSource.Auto || l.Source == LinkSource.Suggested ? l.Source : LinkSource.Manual,
                    SeerrDisplayName = l.SeerrDisplayName,
                })
                .Where(l => l.EmbyUserId != null && embyIds.Contains(l.EmbyUserId))
                .GroupBy(l => l.EmbyUserId + ":" + l.SeerrUserId)
                .Select(g => g.First())
                .ToArray();

            Plugin.Instance.Mutate(c => c.UserLinks = clean);
            return clean;
        }

        private static bool NameMatches(string embyName, SeerrUser su)
        {
            bool Eq(string s) => !string.IsNullOrWhiteSpace(s) && string.Equals(s.Trim(), embyName, StringComparison.OrdinalIgnoreCase);

            if (Eq(su.Username) || Eq(su.DisplayName) || Eq(su.JellyfinUsername) || Eq(su.PlexUsername))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(su.Email))
            {
                var at = su.Email.IndexOf('@');
                if (Eq(su.Email) || (at > 0 && Eq(su.Email.Substring(0, at))))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
