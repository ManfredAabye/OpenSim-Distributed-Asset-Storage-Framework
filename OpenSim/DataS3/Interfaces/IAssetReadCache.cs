using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.Interfaces
{
    /// <summary>
    /// Read cache for object payloads keyed by storage key.
    /// </summary>
    public interface IAssetReadCache
    {
        /// <summary>
        /// Attempts to read cached payload bytes.
        /// </summary>
        Task<byte[]?> GetAsync(string storageKey, CancellationToken cancellationToken);

        /// <summary>
        /// Stores payload bytes in the cache.
        /// </summary>
        Task SetAsync(string storageKey, byte[] payload, CancellationToken cancellationToken);

        /// <summary>
        /// Removes one cached payload.
        /// </summary>
        Task RemoveAsync(string storageKey, CancellationToken cancellationToken);
    }
}
