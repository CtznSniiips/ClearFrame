using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClearFrame.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;

namespace ClearFrame.Services
{
    /// <summary>A pairing started by a TV and waiting for an admin to approve it.</summary>
    public class PendingPairing
    {
        public string Id { get; set; }
        public string Code { get; set; }
        public string Secret { get; set; }
        public string DeviceName { get; set; }
        public string Duid { get; set; }
        public DateTime CreatedUtc { get; set; }

        /// <summary>pending, approved, rejected.</summary>
        public string Status { get; set; } = "pending";

        /// <summary>Plain token, held only until the TV collects it once.</summary>
        public string IssuedToken { get; set; }
        public string TvId { get; set; }
        public string TvName { get; set; }
    }

    /// <summary>Pairing codes, per-TV tokens and lookups of paired TVs.</summary>
    public class TvRegistry
    {
        // No 0/O, 1/I/L, so codes are easy to read off a TV across the room.
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(15);

        private readonly ConcurrentDictionary<string, PendingPairing> _pairings =
            new ConcurrentDictionary<string, PendingPairing>(StringComparer.OrdinalIgnoreCase);

        private readonly ILogger _logger;

        public TvRegistry(ILogger logger)
        {
            _logger = logger;
        }

        public PendingPairing StartPairing(string deviceName, string duid)
        {
            PurgeExpired();

            // A TV that restarts pairing gets its previous code replaced rather than piling up.
            if (!string.IsNullOrWhiteSpace(duid))
            {
                foreach (var old in _pairings.Values.Where(p => p.Status == "pending" && string.Equals(p.Duid, duid, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _pairings.TryRemove(old.Code, out _);
                }
            }

            string code;
            do
            {
                code = RandomCode(4);
            }
            while (_pairings.ContainsKey(code));

            var pairing = new PendingPairing
            {
                Id = Guid.NewGuid().ToString("N"),
                Code = code,
                Secret = RandomToken(24),
                DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Samsung TV" : deviceName.Trim(),
                Duid = string.IsNullOrWhiteSpace(duid) ? null : duid.Trim(),
                CreatedUtc = DateTime.UtcNow,
            };

            _pairings[code] = pairing;
            _logger.Info("ClearFrame pairing {0} started by '{1}'", code, pairing.DeviceName);
            return pairing;
        }

        public PendingPairing GetPairingForTv(string pairingId, string secret)
        {
            PurgeExpired();
            var pairing = _pairings.Values.FirstOrDefault(p => p.Id == pairingId);
            if (pairing == null || !FixedTimeEquals(pairing.Secret, secret))
            {
                return null;
            }

            return pairing;
        }

        /// <summary>Called after the TV has received its token, so the plain token is not kept around.</summary>
        public void CompletePairing(PendingPairing pairing)
        {
            _pairings.TryRemove(pairing.Code, out _);
        }

        public IReadOnlyList<PendingPairing> GetPendingPairings()
        {
            PurgeExpired();
            return _pairings.Values.Where(p => p.Status == "pending").OrderBy(p => p.CreatedUtc).ToList();
        }

        public TvDevice ApprovePairing(string code, string name, string embyUserId, string embyDeviceId, string[] sportsTeams)
        {
            PurgeExpired();
            if (string.IsNullOrWhiteSpace(code) || !_pairings.TryGetValue(code.Trim(), out var pairing) || pairing.Status != "pending")
            {
                throw new ArgumentException("That pairing code is unknown or has expired. Restart pairing on the TV.");
            }

            if (string.IsNullOrWhiteSpace(embyUserId))
            {
                throw new ArgumentException("Choose the Emby user this TV acts as.");
            }

            var token = RandomToken(32);
            var tv = new TvDevice
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? pairing.DeviceName : name.Trim(),
                TokenHash = Hash(token),
                EmbyUserId = NormalizeId(embyUserId),
                EmbyDeviceId = string.IsNullOrWhiteSpace(embyDeviceId) ? null : embyDeviceId.Trim(),
                Duid = pairing.Duid,
                SportsTeams = CleanTeams(sportsTeams),
                PairedUtc = DateTime.UtcNow.ToString("o"),
            };

            Plugin.Instance.Mutate(c =>
            {
                // Re-pairing the same physical TV replaces its old record.
                var others = c.Tvs.Where(t => tv.Duid == null || !string.Equals(t.Duid, tv.Duid, StringComparison.OrdinalIgnoreCase));
                c.Tvs = others.Concat(new[] { tv }).ToArray();
            });

            pairing.Status = "approved";
            pairing.IssuedToken = token;
            pairing.TvId = tv.Id;
            pairing.TvName = tv.Name;
            _logger.Info("ClearFrame pairing {0} approved as '{1}'", pairing.Code, tv.Name);
            return tv;
        }

        public void RejectPairing(string code)
        {
            if (!string.IsNullOrWhiteSpace(code) && _pairings.TryGetValue(code.Trim(), out var pairing))
            {
                pairing.Status = "rejected";
            }
        }

        /// <summary>Resolves the TV for a request token, or throws 401.</summary>
        public TvDevice Authenticate(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new SecurityException("Missing X-ClearFrame-Token header.", SecurityExceptionType.Unauthenticated);
            }

            var hash = Hash(token.Trim());
            var tv = Plugin.Instance.Configuration.Tvs.FirstOrDefault(t => FixedTimeEquals(t.TokenHash, hash));
            if (tv == null)
            {
                throw new SecurityException("This TV is not paired with ClearFrame (or was removed). Pair it again.", SecurityExceptionType.Unauthenticated);
            }

            // Only write "last seen" occasionally to avoid rewriting the config file on every request.
            DateTime last;
            if (!DateTime.TryParse(tv.LastSeenUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out last) || DateTime.UtcNow - last > TimeSpan.FromMinutes(10))
            {
                var id = tv.Id;
                var now = DateTime.UtcNow.ToString("o");
                Plugin.Instance.Mutate(c =>
                {
                    var t = c.Tvs.FirstOrDefault(x => x.Id == id);
                    if (t != null)
                    {
                        t.LastSeenUtc = now;
                    }
                });
            }

            return tv;
        }

        public static string[] CleanTeams(IEnumerable<string> teams)
        {
            return (teams ?? Enumerable.Empty<string>())
                .SelectMany(t => (t ?? string.Empty).Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>Emby user ids are compared without dashes and case-insensitively.</summary>
        public static string NormalizeId(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim().Replace("-", string.Empty).ToLowerInvariant();
        }

        private void PurgeExpired()
        {
            var cutoff = DateTime.UtcNow - PairingLifetime;
            foreach (var p in _pairings.Values.Where(p => p.CreatedUtc < cutoff).ToList())
            {
                _pairings.TryRemove(p.Code, out _);
            }
        }

        private static string RandomCode(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(length);
            foreach (var b in bytes)
            {
                sb.Append(CodeAlphabet[b % CodeAlphabet.Length]);
            }

            return sb.ToString();
        }

        private static string RandomToken(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}
