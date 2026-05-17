using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Models;

namespace OpenSim.DataS3.Interfaces
{
    public interface IUploadRateLimiter
    {
        Task<bool> CanUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken);

        Task RecordUploadAsync(UUID userId, long sizeBytes, CancellationToken cancellationToken);

        Task<QuotaStatus> GetQuotaStatusAsync(UUID userId, CancellationToken cancellationToken);

        Task ResetQuotaAsync(UUID userId, CancellationToken cancellationToken);

        Task AddQuotaBytesAsync(UUID userId, long additionalBytes, CancellationToken cancellationToken);
    }
}
