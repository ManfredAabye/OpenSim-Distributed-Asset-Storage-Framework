using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Models;

namespace OpenSim.DataS3.RateControl
{
    /// <summary>
    /// In-memory upload rate limiter for user quota, throughput and concurrent upload limits.
    /// </summary>
    public sealed class UploadRateLimiter : IUploadRateLimiter
    {
        private const string DenyConcurrent = "concurrent_upload_limit";
        private const string DenyDailyQuota = "daily_quota_exceeded";
        private const string DenyThroughput = "throughput_limit";

        private readonly object _syncRoot = new object();
        private readonly Dictionary<UUID, UserRateState> _states = new Dictionary<UUID, UserRateState>();

        private readonly long _maxUploadPerWindowBytes;
        private readonly int _maxConcurrentUploads;
        private readonly long _maxBytesPerSecond;
        private readonly int _quotaWindowSeconds;

        /// <summary>
        /// Creates a limiter with configurable thresholds.
        /// </summary>
        /// <param name="maxUploadPerWindowBytes">Maximum uploaded bytes per quota window. Values <= 0 disable the limit.</param>
        /// <param name="maxConcurrentUploads">Maximum in-flight uploads per user. Values <= 0 disable the limit.</param>
        /// <param name="maxBytesPerSecond">Maximum per-user throughput. Values <= 0 disable the limit.</param>
        /// <param name="quotaWindowSeconds">Quota window length in seconds. Values <= 0 default to 86400.</param>
        public UploadRateLimiter(
            long maxUploadPerWindowBytes,
            int maxConcurrentUploads,
            long maxBytesPerSecond,
            int quotaWindowSeconds)
        {
            _maxUploadPerWindowBytes = maxUploadPerWindowBytes;
            _maxConcurrentUploads = maxConcurrentUploads;
            _maxBytesPerSecond = maxBytesPerSecond;
            _quotaWindowSeconds = quotaWindowSeconds > 0 ? quotaWindowSeconds : 86400;
        }

        /// <inheritdoc />
        public Task<bool> CanUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long size = sizeBytes > 0 ? sizeBytes : 0;

            lock (_syncRoot)
            {
                UserRateState state = GetOrCreateState(userId, now);
                ResetWindowIfExpired(state, now);
                TrimSecondSamples(state, now);

                if (_maxConcurrentUploads > 0 && state.InFlightUploads >= _maxConcurrentUploads)
                {
                    state.LastDenyReason = DenyConcurrent;
                    state.LastRetryAfterSeconds = 1;
                    return Task.FromResult(false);
                }

                if (_maxUploadPerWindowBytes > 0 && state.UploadedInWindowBytes + size > _maxUploadPerWindowBytes)
                {
                    state.LastDenyReason = DenyDailyQuota;
                    state.LastRetryAfterSeconds = CalculateQuotaRetryAfterSeconds(state, now);
                    return Task.FromResult(false);
                }

                if (_maxBytesPerSecond > 0 && state.BytesInLastSecond + size > _maxBytesPerSecond)
                {
                    state.LastDenyReason = DenyThroughput;
                    state.LastRetryAfterSeconds = 1;
                    return Task.FromResult(false);
                }

                state.InFlightUploads++;
                state.LastDenyReason = null;
                state.LastRetryAfterSeconds = 0;
                return Task.FromResult(true);
            }
        }

        /// <inheritdoc />
        public Task RecordUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long recordedBytes = sizeBytes > 0 ? sizeBytes : 0;

            lock (_syncRoot)
            {
                UserRateState state = GetOrCreateState(userId, now);
                ResetWindowIfExpired(state, now);
                TrimSecondSamples(state, now);

                if (state.InFlightUploads > 0)
                    state.InFlightUploads--;

                if (recordedBytes > 0)
                {
                    state.UploadedInWindowBytes += recordedBytes;
                    state.SecondSamples.Enqueue((now, recordedBytes));
                    state.BytesInLastSecond += recordedBytes;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<QuotaStatus> GetQuotaStatusAsync(UUID userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_syncRoot)
            {
                UserRateState state = GetOrCreateState(userId, now);
                ResetWindowIfExpired(state, now);
                TrimSecondSamples(state, now);

                long remaining = _maxUploadPerWindowBytes > 0
                    ? Math.Max(0, _maxUploadPerWindowBytes - state.UploadedInWindowBytes)
                    : long.MaxValue;

                return Task.FromResult(new QuotaStatus
                {
                    Allowed = string.IsNullOrEmpty(state.LastDenyReason),
                    DenyReason = state.LastDenyReason,
                    RetryAfterSeconds = state.LastRetryAfterSeconds,
                    MaxUploadPerDayBytes = _maxUploadPerWindowBytes > 0 ? _maxUploadPerWindowBytes : long.MaxValue,
                    RemainingUploadPerDayBytes = remaining,
                    MaxConcurrentUploads = _maxConcurrentUploads > 0 ? _maxConcurrentUploads : int.MaxValue,
                    CurrentConcurrentUploads = state.InFlightUploads,
                    MaxBytesPerSecond = _maxBytesPerSecond > 0 ? _maxBytesPerSecond : long.MaxValue
                });
            }
        }

        /// <inheritdoc />
        public Task ResetQuotaAsync(UUID userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_syncRoot)
            {
                UserRateState state = GetOrCreateState(userId, now);
                state.WindowStartUnixSeconds = now;
                state.UploadedInWindowBytes = 0;
                state.LastDenyReason = null;
                state.LastRetryAfterSeconds = 0;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddQuotaBytesAsync(UUID userId, long additionalBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (additionalBytes <= 0)
                return Task.CompletedTask;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_syncRoot)
            {
                UserRateState state = GetOrCreateState(userId, now);
                ResetWindowIfExpired(state, now);
                state.UploadedInWindowBytes = Math.Max(0, state.UploadedInWindowBytes - additionalBytes);
                state.LastDenyReason = null;
                state.LastRetryAfterSeconds = 0;
            }

            return Task.CompletedTask;
        }

        private UserRateState GetOrCreateState(UUID userId, long now)
        {
            if (_states.TryGetValue(userId, out UserRateState? state))
                return state;

            state = new UserRateState
            {
                WindowStartUnixSeconds = now
            };

            _states[userId] = state;
            return state;
        }

        private void ResetWindowIfExpired(UserRateState state, long now)
        {
            if (now - state.WindowStartUnixSeconds < _quotaWindowSeconds)
                return;

            state.WindowStartUnixSeconds = now;
            state.UploadedInWindowBytes = 0;
        }

        private static void TrimSecondSamples(UserRateState state, long now)
        {
            long threshold = now - 1;
            while (state.SecondSamples.Count > 0 && state.SecondSamples.Peek().TimestampUnixSeconds <= threshold)
            {
                (long _, long bytes) = state.SecondSamples.Dequeue();
                state.BytesInLastSecond -= bytes;
                if (state.BytesInLastSecond < 0)
                    state.BytesInLastSecond = 0;
            }
        }

        private int CalculateQuotaRetryAfterSeconds(UserRateState state, long now)
        {
            long elapsed = now - state.WindowStartUnixSeconds;
            long remaining = _quotaWindowSeconds - elapsed;
            if (remaining <= 0)
                return 1;

            return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
        }

        private sealed class UserRateState
        {
            public long WindowStartUnixSeconds;

            public long UploadedInWindowBytes;

            public int InFlightUploads;

            public long BytesInLastSecond;

            public string? LastDenyReason;

            public int LastRetryAfterSeconds;

            public Queue<(long TimestampUnixSeconds, long Bytes)> SecondSamples { get; } = new Queue<(long TimestampUnixSeconds, long Bytes)>();
        }
    }
}
