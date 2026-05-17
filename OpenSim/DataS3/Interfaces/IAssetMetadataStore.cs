using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.DataS3.Models;

namespace OpenSim.DataS3.Interfaces
{
    public interface IAssetMetadataStore
    {
        Task<AssetMetadataRecord?> GetAsync(UUID id, CancellationToken cancellationToken);

        Task StoreAsync(AssetMetadataRecord metadata, CancellationToken cancellationToken);

        Task DeleteAsync(UUID id, CancellationToken cancellationToken);

        Task<bool> ExistsAsync(UUID id, CancellationToken cancellationToken);

        Task<IReadOnlyList<AssetMetadataRecord>> ListAsync(int start, int count, CancellationToken cancellationToken);

        Task<bool> HasOtherReferencesAsync(string storageKey, UUID assetId, CancellationToken cancellationToken);
    }
}
