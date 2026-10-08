// SPDX-License-Identifier: GPL-3.0-only
// PairingService.cs: PIN pairing and token resume (docs/protocol.md §8, §15). The PIN is generated here and shown on
// the rigPlay page; the user types it on the tablet. Rules: 6 digits from a CSPRNG, valid 120 s, single use,
// 3 attempts, one pending PIN per tablet, at most 5 Start requests per minute. A successful pairing issues a token of
// 32 random bytes (base64url, no padding); only its SHA-256 is stored, keyed by tabletId, and compared in constant
// time.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Pairing
{
    /// <summary>A PIN shown on the page, waiting for the tablet to submit it.</summary>
    public sealed class PendingPairing
    {
        public string TabletId { get; internal set; }
        public string TabletName { get; internal set; }
        public string Pin { get; internal set; }
        public long ExpiresAtMs { get; internal set; }
        public int AttemptsLeft { get; internal set; }

        /// <summary>The session that asked for it; 0 when not tied to one (tests).</summary>
        public int SessionId { get; internal set; }

        public PendingPairing Copy()
        {
            return (PendingPairing)MemberwiseClone();
        }
    }

    public static class PairingTokens
    {
        /// <summary>A new token: 32 bytes from a CSPRNG, base64url without padding (43 characters).</summary>
        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        public static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>SHA-256 of the token's UTF-8 bytes, lower-case hex (64 characters). What the settings store.</summary>
        public static string Hash(string token)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? ""));
                var sb = new StringBuilder(64);
                foreach (var b in digest) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Compares two strings in time that depends only on their length.</summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>A PIN uniformly distributed over 000000-999999 (rejection sampling, no modulo bias).</summary>
        public static string NewPin(RandomNumberGenerator rng)
        {
            const uint limit = 4294000000; // the largest multiple of 1 000 000 below 2^32
            var bytes = new byte[4];
            while (true)
            {
                rng.GetBytes(bytes);
                var value = BitConverter.ToUInt32(bytes, 0);
                if (value < limit) return (value % 1000000).ToString("D6");
            }
        }
    }

    public sealed class PairingService : IPairingAuthority, IDisposable
    {
        private readonly object sync = new object();
        private readonly RigPlaySettings settings;
        private readonly Action save;
        private readonly IClock clock;
        private readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        private readonly Dictionary<string, PendingPairing> pending = new Dictionary<string, PendingPairing>(StringComparer.Ordinal);
        private readonly Queue<long> starts = new Queue<long>();

        public PairingService(RigPlaySettings settings, Action save, IClock clock = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.save = save ?? (() => { });
            this.clock = clock ?? SystemClock.Instance;
        }

        /// <summary>Generates PINs; tests replace it to know the PIN.</summary>
        public Func<string> PinGenerator { get; set; }

        /// <summary>Pending PINs or paired tablets changed. Raised on the calling thread.</summary>
        public event Action Changed;

        /// <summary>The PINs waiting to be typed, oldest first; expired ones are dropped.</summary>
        public List<PendingPairing> Pending
        {
            get
            {
                bool changed;
                List<PendingPairing> list;
                lock (sync)
                {
                    changed = PruneExpired();
                    list = pending.Values.OrderBy(p => p.ExpiresAtMs).Select(p => p.Copy()).ToList();
                }
                if (changed) RaiseChanged();
                return list;
            }
        }

        /// <summary>Seconds left on a pending PIN, rounded up; 0 when expired.</summary>
        public int SecondsLeft(PendingPairing p)
        {
            var ms = p.ExpiresAtMs - clock.NowMs;
            return ms <= 0 ? 0 : (int)((ms + 999) / 1000);
        }

        public List<PairedTablet> PairedTablets
        {
            get { lock (sync) return settings.PairedTablets.Select(t => new PairedTablet { Id = t.Id, Name = t.Name, TokenHash = t.TokenHash, PairedAt = t.PairedAt }).ToList(); }
        }

        public bool IsPaired(string tabletId)
        {
            lock (sync) return settings.FindTablet(tabletId) != null;
        }

        // The three pairRequest forms

        public PairResultMessage HandlePairRequest(ClientSession session, PairRequestMessage request)
        {
            switch (request.Form)
            {
                case PairRequestForm.Resume: return Resume(session.TabletId, session.TabletName, request.Token);
                case PairRequestForm.SubmitPin: return SubmitPin(session.TabletId, session.TabletName, request.Pin);
                default: return Start(session.TabletId, session.TabletName, session.Id);
            }
        }

        /// <summary>Start: a new PIN for this tablet, replacing any pending one, unless Starts are rate-limited.</summary>
        public PairResultMessage Start(string tabletId, string tabletName, int sessionId = 0)
        {
            PendingPairing p;
            lock (sync)
            {
                var now = clock.NowMs;
                while (starts.Count > 0 && now - starts.Peek() >= 60000) starts.Dequeue();
                if (starts.Count >= ProtocolDefaults.PairStartsPerMinute)
                {
                    PluginLog.Warn("Pairing request from " + tabletName + " refused: more than " + ProtocolDefaults.PairStartsPerMinute + " per minute");
                    return PairResultMessage.Failure(PairReasons.Denied);
                }
                starts.Enqueue(now);
                p = new PendingPairing
                {
                    TabletId = tabletId,
                    TabletName = string.IsNullOrWhiteSpace(tabletName) ? RigPlaySettings.DefaultTabletName : tabletName.Trim(),
                    Pin = PinGenerator != null ? PinGenerator() : PairingTokens.NewPin(rng),
                    ExpiresAtMs = now + ProtocolDefaults.PinValiditySec * 1000L,
                    AttemptsLeft = ProtocolDefaults.PinMaxAttempts,
                    SessionId = sessionId,
                };
                pending[tabletId] = p;
            }
            PluginLog.Info("Tablet " + p.TabletName + " (" + tabletId + ") wants to pair; PIN shown on the rigPlay page");
            RaiseChanged();
            return new PairResultMessage { Ok = false, Reason = PairReasons.PinRequired, PinExpiresInSec = ProtocolDefaults.PinValiditySec };
        }

        /// <summary>Submit PIN: ok with a new token, wrongPin, tooManyAttempts or pinExpired.</summary>
        public PairResultMessage SubmitPin(string tabletId, string tabletName, string pin)
        {
            string token = null;
            PairResultMessage result;
            lock (sync)
            {
                PruneExpired();
                PendingPairing p;
                if (!pending.TryGetValue(tabletId, out p))
                {
                    result = PairResultMessage.Failure(PairReasons.PinExpired);
                }
                else if (PairingTokens.FixedTimeEquals(p.Pin, pin))
                {
                    pending.Remove(tabletId);
                    token = PairingTokens.NewToken();
                    var name = string.IsNullOrWhiteSpace(tabletName) ? p.TabletName : tabletName.Trim();
                    settings.PairedTablets.RemoveAll(t => t != null && string.Equals(t.Id, tabletId, StringComparison.Ordinal));
                    settings.PairedTablets.Add(new PairedTablet
                    {
                        Id = tabletId,
                        Name = name,
                        TokenHash = PairingTokens.Hash(token),
                        PairedAt = clock.UtcNow,
                    });
                    result = PairResultMessage.Success(token);
                }
                else
                {
                    p.AttemptsLeft--;
                    if (p.AttemptsLeft <= 0)
                    {
                        pending.Remove(tabletId);
                        result = PairResultMessage.Failure(PairReasons.TooManyAttempts);
                    }
                    else
                    {
                        result = new PairResultMessage { Ok = false, Reason = PairReasons.WrongPin, AttemptsLeft = p.AttemptsLeft };
                    }
                }
            }
            if (token != null)
            {
                PluginLog.Info("Tablet " + tabletName + " (" + tabletId + ") paired; token " + PluginLog.Redact(token));
                Save();
            }
            else
            {
                PluginLog.Info("PIN from " + tabletName + " rejected: " + result.Reason + (result.AttemptsLeft.HasValue ? ", " + result.AttemptsLeft + " attempt(s) left" : ""));
            }
            RaiseChanged();
            return result;
        }

        /// <summary>Resume: ok when the token's hash is the one stored for this tabletId, otherwise tokenInvalid.</summary>
        public PairResultMessage Resume(string tabletId, string tabletName, string token)
        {
            var renamed = false;
            bool ok;
            lock (sync)
            {
                var tablet = settings.FindTablet(tabletId);
                ok = tablet != null && PairingTokens.FixedTimeEquals(tablet.TokenHash, PairingTokens.Hash(token));
                if (ok && !string.IsNullOrWhiteSpace(tabletName) && tablet.Name != tabletName.Trim())
                {
                    tablet.Name = tabletName.Trim();
                    renamed = true;
                }
            }
            if (renamed) Save();
            if (!ok)
            {
                PluginLog.Info("Tablet " + tabletName + " (" + tabletId + ") presented an unknown token " + PluginLog.Redact(token));
                return PairResultMessage.Failure(PairReasons.TokenInvalid);
            }
            if (renamed) RaiseChanged();
            return PairResultMessage.Success(token);
        }

        /// <summary>Deny on the page: discards the pending PIN. True when there was one (the caller sends `denied`).</summary>
        public bool Deny(string tabletId)
        {
            bool removed;
            lock (sync) removed = pending.Remove(tabletId);
            if (removed)
            {
                PluginLog.Info("Pairing of " + tabletId + " denied on the PC");
                RaiseChanged();
            }
            return removed;
        }

        /// <summary>Forget on the page: deletes the stored token hash. True when the tablet was paired.</summary>
        public bool Forget(string tabletId)
        {
            int removed;
            lock (sync)
            {
                removed = settings.PairedTablets.RemoveAll(t => t != null && string.Equals(t.Id, tabletId, StringComparison.Ordinal));
                pending.Remove(tabletId);
            }
            if (removed == 0) return false;
            PluginLog.Info("Tablet " + tabletId + " forgotten");
            Save();
            RaiseChanged();
            return true;
        }

        public void SessionClosed(ClientSession session)
        {
            if (session.TabletId == null) return;
            bool removed = false;
            lock (sync)
            {
                PendingPairing p;
                if (pending.TryGetValue(session.TabletId, out p) && p.SessionId == session.Id) removed = pending.Remove(session.TabletId);
            }
            if (removed) RaiseChanged();
        }

        public void Dispose()
        {
            rng.Dispose();
        }

        private bool PruneExpired()
        {
            var now = clock.NowMs;
            var expired = pending.Values.Where(p => p.ExpiresAtMs <= now).Select(p => p.TabletId).ToList();
            foreach (var id in expired) pending.Remove(id);
            return expired.Count > 0;
        }

        private void Save()
        {
            try { save(); } catch (Exception ex) { PluginLog.Error("Saving the paired tablets failed", ex); }
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { PluginLog.Error("A pairing change handler failed", ex); }
        }
    }
}
