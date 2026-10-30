using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lobby.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace Lobby.Services
{
    public static class PlayAction
    {
        public const string Play = "play";
        public const string Resume = "resume";
        public const string Details = "details";
    }

    public class PlaybackOrder
    {
        public string TvId { get; set; }
        public string TvName { get; set; }
        public string EmbyUserId { get; set; }
        public string EmbyDeviceId { get; set; }
        public string Duid { get; set; }
        public long ItemId { get; set; }
        public string ItemType { get; set; }
        public string ItemName { get; set; }
        public string Action { get; set; }
        public long? StartPositionTicks { get; set; }

        /// <summary>Wait after the Emby app becomes controllable before sending (this TV's setting).</summary>
        public int DelayMs { get; set; }

        /// <summary>How long to keep the order queued (this TV's setting).</summary>
        public int TimeoutSeconds { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public int Attempts;
    }

    public class DispatchResult
    {
        /// <summary>sent, queued or failed.</summary>
        public string Status { get; set; }
        public string Message { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Delivers play/resume/details commands to the Emby app on a TV.
    /// Lobby calls the server, then launches Emby; this class waits for the Emby app's session to
    /// become controllable (or uses it straight away if it is already running) and sends the command.
    /// </summary>
    public class PlaybackDispatcher : IDisposable
    {
        private const int MaxAttempts = 4;

        private readonly ISessionManager _sessions;
        private readonly IUserManager _users;
        private readonly ILogger _logger;

        // One pending order per TV: a newer press replaces an older one.
        private readonly ConcurrentDictionary<string, PlaybackOrder> _pending = new ConcurrentDictionary<string, PlaybackOrder>();
        private readonly Timer _sweeper;

        public PlaybackDispatcher(ISessionManager sessions, IUserManager users, ILogger logger)
        {
            _sessions = sessions;
            _users = users;
            _logger = logger;

            _sessions.SessionStarted += OnSessionEvent;
            _sessions.CapabilitiesChanged += OnSessionEvent;
            _sessions.SessionActivity += OnSessionEvent;

            // Catches sessions that become controllable without raising an event we listen to,
            // and expires stale orders.
            _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public int PendingCount => _pending.Count;

        public async Task<DispatchResult> Dispatch(PlaybackOrder order)
        {
            var timeout = Math.Max(5, order.TimeoutSeconds);
            order.ExpiresUtc = DateTime.UtcNow.AddSeconds(timeout);

            var session = FindControllableSession(order);
            if (session != null)
            {
                // Emby is already running (often the case: the Emby app supports prelaunch),
                // so there is nothing to wait for.
                try
                {
                    await Send(order, session).ConfigureAwait(false);
                    return new DispatchResult { Status = "sent", SessionId = session.Id };
                }
                catch (Exception ex)
                {
                    _logger.Warn("immediate send to '{0}' failed, queueing instead: {1}", order.TvName, ex.Message);
                }
            }

            _pending[order.TvId] = order;
            _logger.Info("queued {0} of item {1} for '{2}' until its Emby app connects", order.Action, order.ItemId, order.TvName);
            return new DispatchResult
            {
                Status = "queued",
                Message = "Waiting up to " + timeout + "s for the Emby app on this TV to connect.",
            };
        }

        /// <summary>Sessions that could belong to this TV's Emby app, newest first.</summary>
        public IEnumerable<SessionInfo> FindSessions(string embyDeviceId, string duid)
        {
            return _sessions.Sessions
                .Where(s => s.DeviceId != null &&
                            ((embyDeviceId != null && string.Equals(s.DeviceId, embyDeviceId, StringComparison.OrdinalIgnoreCase)) ||
                             (duid != null && string.Equals(s.DeviceId, duid, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(s => s.LastActivityDate);
        }

        public void Dispose()
        {
            _sessions.SessionStarted -= OnSessionEvent;
            _sessions.CapabilitiesChanged -= OnSessionEvent;
            _sessions.SessionActivity -= OnSessionEvent;
            _sweeper.Dispose();
        }

        private SessionInfo FindControllableSession(PlaybackOrder order)
        {
            return FindSessions(order.EmbyDeviceId, order.Duid).FirstOrDefault(IsControllable);
        }

        private static bool IsControllable(SessionInfo s)
        {
            return s.IsActive && s.SupportsRemoteControl;
        }

        private void OnSessionEvent(object sender, SessionEventArgs e)
        {
            var session = e?.SessionInfo;
            if (session?.DeviceId == null || _pending.IsEmpty || !IsControllable(session))
            {
                return;
            }

            foreach (var order in _pending.Values)
            {
                var matches = string.Equals(session.DeviceId, order.EmbyDeviceId, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(session.DeviceId, order.Duid, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    TryDeliver(order, session);
                }
            }
        }

        private void Sweep()
        {
            if (_pending.IsEmpty)
            {
                return;
            }

            foreach (var order in _pending.Values)
            {
                if (DateTime.UtcNow > order.ExpiresUtc)
                {
                    if (_pending.TryRemove(order.TvId, out _))
                    {
                        _logger.Warn("gave up on {0} of item {1} for '{2}': the Emby app never became controllable", order.Action, order.ItemId, order.TvName);
                    }

                    continue;
                }

                var session = FindControllableSession(order);
                if (session != null)
                {
                    TryDeliver(order, session);
                }
            }
        }

        private void TryDeliver(PlaybackOrder order, SessionInfo session)
        {
            // Claim the order so concurrent events don't send it twice.
            if (!_pending.TryGetValue(order.TvId, out var current) || !ReferenceEquals(current, order) ||
                !((ICollection<KeyValuePair<string, PlaybackOrder>>)_pending).Remove(new KeyValuePair<string, PlaybackOrder>(order.TvId, order)))
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    // Give the freshly started app a moment to finish loading its home screen,
                    // otherwise it can swallow the command.
                    var delay = Math.Max(0, order.DelayMs);
                    if (delay > 0)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }

                    await Send(order, session).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    order.Attempts++;
                    if (order.Attempts < MaxAttempts && DateTime.UtcNow < order.ExpiresUtc)
                    {
                        _logger.Warn("send to '{0}' failed (attempt {1}), will retry: {2}", order.TvName, order.Attempts, ex.Message);
                        _pending.TryAdd(order.TvId, order);
                    }
                    else
                    {
                        _logger.ErrorException("could not send {0} to '{1}'", ex, order.Action, order.TvName);
                    }
                }
            });
        }

        private async Task Send(PlaybackOrder order, SessionInfo session)
        {
            var controllingUserId = order.EmbyUserId;

            if (order.Action == PlayAction.Details)
            {
                var command = new GeneralCommand
                {
                    Name = "DisplayContent",
                    ControllingUserId = controllingUserId,
                    Arguments = new Dictionary<string, string>
                    {
                        { "ItemId", order.ItemId.ToString() },
                        { "ItemType", order.ItemType ?? string.Empty },
                        { "ItemName", order.ItemName ?? string.Empty },
                    },
                };

                await _sessions.SendGeneralCommand(null, session.Id, command, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var request = new PlayRequest
                {
                    ItemIds = new[] { order.ItemId },
                    PlayCommand = PlayCommand.PlayNow,
                    ControllingUserId = controllingUserId,
                    StartPositionTicks = order.StartPositionTicks > 0 ? order.StartPositionTicks : null,
                };

                await _sessions.SendPlayCommand(null, session.Id, request, CancellationToken.None).ConfigureAwait(false);
            }

            _logger.Info("sent {0} of item {1} to '{2}' (session {3})", order.Action, order.ItemId, order.TvName, session.Id);
        }
    }
}
