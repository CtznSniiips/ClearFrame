using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClearFrame.Configuration;
using ClearFrame.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace ClearFrame.Api
{
    // ---- Requests -------------------------------------------------------------------------------
    // TV endpoints skip Emby authentication ([Unauthenticated]; Emby 4.10 requires auth by default).
    // A TV proves itself with the X-ClearFrame-Token header it received when it was paired;
    // pairing itself needs no token.

    [Route("/ClearFrame/Pair", "POST", Summary = "Starts pairing a TV. Show the returned code on screen and poll /ClearFrame/Pair/Status.")]
    [Unauthenticated]
    public class StartPairing : IReturn<StartPairingResponse>
    {
        public string DeviceName { get; set; }

        /// <summary>Samsung DUID (webapis.productinfo.getDuid()).</summary>
        public string Duid { get; set; }
    }

    [Route("/ClearFrame/Pair/Status", "GET", Summary = "Polls a pairing. Returns the TV token once an admin has approved it.")]
    [Unauthenticated]
    public class GetPairingStatus : IReturn<PairingStatusResponse>
    {
        public string PairingId { get; set; }
        public string Secret { get; set; }
    }

    [Route("/ClearFrame/Ping", "GET", Summary = "Checks the TV token.")]
    [Unauthenticated]
    public class Ping : IReturn<PingResponse>
    {
    }

    [Route("/ClearFrame/Home", "GET", Summary = "Home screen rows for this TV.")]
    [Unauthenticated]
    public class GetHome : IReturn<HomeResponse>
    {
    }

    [Route("/ClearFrame/Requesters", "GET", Summary = "Seerr users this TV can request as.")]
    [Unauthenticated]
    public class GetRequesters : IReturn<RequestersResponse>
    {
    }

    [Route("/ClearFrame/Request", "POST", Summary = "Requests a movie or show in Seerr.")]
    [Unauthenticated]
    public class SubmitRequest : IReturn<SubmitRequestResponse>
    {
        public int TmdbId { get; set; }

        /// <summary>movie or tv.</summary>
        public string MediaType { get; set; }

        /// <summary>Seerr user to request as. Optional when only one requester is available.</summary>
        public int SeerrUserId { get; set; }
    }

    [Route("/ClearFrame/Play", "POST", Summary = "Plays, resumes or shows an item in the Emby app on this TV. Call this, then launch Emby.")]
    [Unauthenticated]
    public class Play : IReturn<DispatchResult>
    {
        public string ItemId { get; set; }

        /// <summary>play, resume or details. Defaults to play.</summary>
        public string Action { get; set; }

        /// <summary>Optional explicit start position. For "resume" the saved position is used when omitted.</summary>
        public long? StartPositionTicks { get; set; }
    }

    // ---- Responses ------------------------------------------------------------------------------

    public class StartPairingResponse
    {
        public string PairingId { get; set; }
        public string Code { get; set; }
        public string Secret { get; set; }
        public int ExpiresInSeconds { get; set; }
    }

    public class PairingStatusResponse
    {
        /// <summary>pending, approved, rejected or expired.</summary>
        public string Status { get; set; }

        /// <summary>Only present once, on the first poll after approval. Store it on the TV.</summary>
        public string Token { get; set; }
        public string TvName { get; set; }
    }

    public class PingResponse
    {
        public string TvName { get; set; }
        public string UserName { get; set; }
    }

    public class Requester
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
    }

    public class RequestersResponse
    {
        public bool RequestsEnabled { get; set; }
        public List<Requester> Requesters { get; set; } = new List<Requester>();

        /// <summary>Who last requested from this TV recently; pre-select them in the picker.</summary>
        public int? LastRequesterId { get; set; }

        /// <summary>True when the TV's user has no linked Seerr account and the fallback account is used.</summary>
        public bool UsingFallback { get; set; }
    }

    public class SubmitRequestResponse
    {
        /// <summary>requested, approved or failed.</summary>
        public string Status { get; set; }
        public string Message { get; set; }
        public int? RequestId { get; set; }
    }

    // ---- Service --------------------------------------------------------------------------------

    public class TvApi : IService, IRequiresRequest
    {
        public const string TokenHeader = "X-ClearFrame-Token";

        // TV id -> (Seerr user id, when)
        private static readonly ConcurrentDictionary<string, Tuple<int, DateTime>> LastRequester =
            new ConcurrentDictionary<string, Tuple<int, DateTime>>();

        public IRequest Request { get; set; }

        private static ClearFrameRuntime Runtime =>
            ClearFrameRuntime.Current ?? throw new InvalidOperationException("ClearFrame is still starting up.");

        public object Post(StartPairing request)
        {
            var p = Runtime.Tvs.StartPairing(request.DeviceName, request.Duid);
            return new StartPairingResponse
            {
                PairingId = p.Id,
                Code = p.Code,
                Secret = p.Secret,
                ExpiresInSeconds = 15 * 60,
            };
        }

        public object Get(GetPairingStatus request)
        {
            var p = Runtime.Tvs.GetPairingForTv(request.PairingId, request.Secret);
            if (p == null)
            {
                return new PairingStatusResponse { Status = "expired" };
            }

            var response = new PairingStatusResponse { Status = p.Status, TvName = p.TvName };
            if (p.Status == "approved")
            {
                response.Token = p.IssuedToken;
                Runtime.Tvs.CompletePairing(p);
            }

            return response;
        }

        public object Get(Ping request)
        {
            var tv = Authenticate();
            var user = Runtime.Home.GetUser(tv);
            return new PingResponse { TvName = tv.Name, UserName = user.Name };
        }

        public async Task<object> Get(GetHome request)
        {
            var tv = Authenticate();
            return await Runtime.Home.Build(tv, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<object> Get(GetRequesters request)
        {
            var tv = Authenticate();
            var config = Plugin.Instance.Configuration;
            var response = new RequestersResponse { RequestsEnabled = config.RequestsEnabled && Runtime.Seerr.IsConfigured };
            if (!response.RequestsEnabled)
            {
                return response;
            }

            var ids = AllowedRequesterIds(tv, out var usingFallback);
            response.UsingFallback = usingFallback;
            if (ids.Length == 0)
            {
                return response;
            }

            List<SeerrUser> seerrUsers;
            try
            {
                seerrUsers = await Runtime.Links.GetSeerrUsers(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SeerrException)
            {
                seerrUsers = new List<SeerrUser>();
            }

            foreach (var id in ids)
            {
                var su = seerrUsers.FirstOrDefault(s => s.Id == id);
                var cachedName = config.UserLinks.FirstOrDefault(l => l.SeerrUserId == id)?.SeerrDisplayName;
                response.Requesters.Add(new Requester
                {
                    Id = id,
                    Name = su?.BestName ?? cachedName ?? ("Seerr user " + id),
                    AvatarUrl = Runtime.Seerr.Absolute(su?.Avatar),
                });
            }

            if (LastRequester.TryGetValue(tv.Id, out var last) &&
                DateTime.UtcNow - last.Item2 < TimeSpan.FromMinutes(Math.Max(0, config.RememberRequesterMinutes)) &&
                ids.Contains(last.Item1))
            {
                response.LastRequesterId = last.Item1;
            }

            return response;
        }

        public async Task<object> Post(SubmitRequest request)
        {
            var tv = Authenticate();
            var config = Plugin.Instance.Configuration;
            if (!config.RequestsEnabled || !Runtime.Seerr.IsConfigured)
            {
                return new SubmitRequestResponse { Status = "failed", Message = "Requests are turned off on this server." };
            }

            var mediaType = (request.MediaType ?? string.Empty).Trim().ToLowerInvariant();
            if ((mediaType != "movie" && mediaType != "tv") || request.TmdbId <= 0)
            {
                throw new ArgumentException("MediaType must be 'movie' or 'tv' and TmdbId must be set.");
            }

            var allowed = AllowedRequesterIds(tv, out _);
            var seerrUserId = request.SeerrUserId;
            if (seerrUserId <= 0 && allowed.Length == 1)
            {
                seerrUserId = allowed[0];
            }

            if (!allowed.Contains(seerrUserId))
            {
                return new SubmitRequestResponse
                {
                    Status = "failed",
                    Message = allowed.Length == 0
                        ? "This TV's Emby user isn't linked to a Seerr account."
                        : seerrUserId <= 0
                            ? "Choose who is requesting."
                            : "That person can't request from this TV.",
                };
            }

            try
            {
                var result = await Runtime.Seerr.Request(mediaType, request.TmdbId, seerrUserId, CancellationToken.None).ConfigureAwait(false);
                LastRequester[tv.Id] = Tuple.Create(seerrUserId, DateTime.UtcNow);
                Runtime.Logger.Info("'{0}' requested {1} {2} as Seerr user {3}", tv.Name, mediaType, request.TmdbId, seerrUserId);
                return new SubmitRequestResponse
                {
                    Status = result?.Status == 2 ? "approved" : "requested",
                    RequestId = result?.Id,
                    Message = result?.Status == 2 ? "Requested and approved." : "Requested. Waiting for approval.",
                };
            }
            catch (SeerrException ex)
            {
                return new SubmitRequestResponse { Status = "failed", Message = ex.Message };
            }
        }

        public async Task<object> Post(Play request)
        {
            var tv = Authenticate();
            var user = Runtime.Home.GetUser(tv);

            if (string.IsNullOrWhiteSpace(request.ItemId) || !long.TryParse(request.ItemId.Trim(), out var itemId))
            {
                throw new ArgumentException("ItemId must be an Emby item id.");
            }

            var item = Runtime.LibraryManager.GetItemById(itemId);
            if (item == null || !item.IsVisibleStandalone(user))
            {
                throw new ResourceNotFoundException("Item " + request.ItemId + " was not found.");
            }

            var action = (request.Action ?? PlayAction.Play).Trim().ToLowerInvariant();
            if (action != PlayAction.Play && action != PlayAction.Resume && action != PlayAction.Details)
            {
                throw new ArgumentException("Action must be play, resume or details.");
            }

            long? start = request.StartPositionTicks;
            if (action == PlayAction.Resume && !start.HasValue)
            {
                var data = Runtime.UserDataManager.GetUserData(user, item);
                start = data?.PlaybackPositionTicks > 0 ? data.PlaybackPositionTicks : (long?)null;
            }

            if (string.IsNullOrWhiteSpace(tv.EmbyDeviceId) && string.IsNullOrWhiteSpace(tv.Duid))
            {
                return new DispatchResult { Status = "failed", Message = "This TV has no Emby app device assigned in the ClearFrame settings." };
            }

            return await Runtime.Playback.Dispatch(new PlaybackOrder
            {
                TvId = tv.Id,
                TvName = tv.Name,
                EmbyUserId = user.Id.ToString("N"),
                EmbyDeviceId = tv.EmbyDeviceId,
                Duid = tv.Duid,
                ItemId = item.InternalId,
                ItemType = item.GetClientTypeName(),
                ItemName = item.Name,
                Action = action == PlayAction.Details ? PlayAction.Details : PlayAction.Play,
                StartPositionTicks = action == PlayAction.Details ? null : start,
            }).ConfigureAwait(false);
        }

        private int[] AllowedRequesterIds(TvDevice tv, out bool usingFallback)
        {
            var ids = Runtime.Links.GetLinkedSeerrUserIds(tv.EmbyUserId);
            usingFallback = false;
            var fallback = Plugin.Instance.Configuration.FallbackSeerrUserId;
            if (ids.Length == 0 && fallback > 0)
            {
                usingFallback = true;
                return new[] { fallback };
            }

            return ids;
        }

        private TvDevice Authenticate()
        {
            return Runtime.Tvs.Authenticate(Request?.Headers?.Get(TokenHeader));
        }
    }
}
