using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Models;

namespace OpenSim.DataS3.RateControl
{
    /// <summary>
    /// Permissive rate limiter used for early integration and local testing.
    /// </summary>
    public sealed class NoopUploadRateLimiter : IUploadRateLimiter
    {
        /// <inheritdoc />
        public Task<bool> CanUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task RecordUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<QuotaStatus> GetQuotaStatusAsync(UUID userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new QuotaStatus
            {
                Allowed = true,
                DenyReason = null,
                RetryAfterSeconds = 0,
                MaxUploadPerDayBytes = long.MaxValue,
                RemainingUploadPerDayBytes = long.MaxValue,
                MaxConcurrentUploads = int.MaxValue,
                CurrentConcurrentUploads = 0,
                MaxBytesPerSecond = long.MaxValue
            });
        }

        /// <inheritdoc />
        public Task ResetQuotaAsync(UUID userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddQuotaBytesAsync(UUID userId, long additionalBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
