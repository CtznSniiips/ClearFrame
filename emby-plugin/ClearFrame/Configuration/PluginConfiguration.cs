using MediaBrowser.Model.Plugins;

namespace ClearFrame.Configuration
{
    /// <summary>
    /// Persisted plugin settings. Saved by Emby as XML, so collections are plain arrays.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Base URL of the Seerr instance, e.g. http://192.168.1.10:5055.</summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>Seerr admin API key (Settings → General → API Key).</summary>
        public string SeerrApiKey { get; set; } = string.Empty;

        /// <summary>Whether TVs may submit Seerr requests.</summary>
        public bool RequestsEnabled { get; set; } = true;

        /// <summary>
        /// Seerr user to request as when the TV's Emby user has no linked Seerr account.
        /// 0 means requests are blocked for unlinked users.
        /// </summary>
        public int FallbackSeerrUserId { get; set; }

        /// <summary>Maximum items per home row.</summary>
        public int RowLimit { get; set; } = 20;

        /// <summary>How far ahead (hours) the Sports row looks for games.</summary>
        public int SportsLookaheadHours { get; set; } = 12;

        /// <summary>Delay (ms) between the Emby app becoming controllable and sending the play command.</summary>
        public int PlayDelayMs { get; set; } = 1500;

        /// <summary>How long (seconds) a queued play request waits for the Emby app to connect.</summary>
        public int PlayTimeoutSeconds { get; set; } = 45;

        /// <summary>How long (minutes) the last requester stays pre-selected on a TV.</summary>
        public int RememberRequesterMinutes { get; set; } = 30;

        public bool ShowContinueWatching { get; set; } = true;
        public bool ShowNextUp { get; set; } = true;
        public bool ShowLiveNow { get; set; } = true;
        public bool ShowSports { get; set; } = true;
        public bool ShowSeerrTrending { get; set; } = true;
        public bool ShowSeerrPopularMovies { get; set; } = true;
        public bool ShowSeerrPopularShows { get; set; } = true;

        /// <summary>Paired TVs.</summary>
        public TvDevice[] Tvs { get; set; } = new TvDevice[0];

        /// <summary>Emby user → Seerr user links. One Emby user may link to several Seerr users.</summary>
        public UserLink[] UserLinks { get; set; } = new UserLink[0];
    }

    /// <summary>A TV running ClearFrame that has been paired with this server.</summary>
    public class TvDevice
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>SHA-256 hex of the TV's ClearFrame token. The token itself is never stored.</summary>
        public string TokenHash { get; set; }

        /// <summary>Emby user (GUID, "N" format) the TV acts as.</summary>
        public string EmbyUserId { get; set; }

        /// <summary>DeviceId reported by the Emby app on this TV, used to find its session for remote control.</summary>
        public string EmbyDeviceId { get; set; }

        /// <summary>Samsung DUID reported by ClearFrame. Matched against session DeviceIds as a fallback.</summary>
        public string Duid { get; set; }

        /// <summary>Team names / keywords for the Sports row.</summary>
        public string[] SportsTeams { get; set; } = new string[0];

        public string PairedUtc { get; set; }

        public string LastSeenUtc { get; set; }
    }

    public static class LinkSource
    {
        /// <summary>Seerr already stores this Emby user id on the Seerr account.</summary>
        public const string Auto = "auto";

        /// <summary>Username/email match that an admin still needs to confirm. Not used for requests.</summary>
        public const string Suggested = "suggested";

        /// <summary>Added or confirmed by an admin.</summary>
        public const string Manual = "manual";
    }

    public class UserLink
    {
        /// <summary>Emby user id (GUID, "N" format).</summary>
        public string EmbyUserId { get; set; }

        public int SeerrUserId { get; set; }

        /// <summary>One of <see cref="LinkSource"/>.</summary>
        public string Source { get; set; }

        /// <summary>Cached Seerr display name, for the admin page.</summary>
        public string SeerrDisplayName { get; set; }
    }
}
