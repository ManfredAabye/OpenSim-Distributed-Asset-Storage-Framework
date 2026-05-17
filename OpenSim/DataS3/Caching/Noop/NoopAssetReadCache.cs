using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.Caching.Noop
{
    /// <summary>
    /// Disabled read cache implementation.
    /// </summary>
    public sealed class NoopAssetReadCache : IAssetReadCache
    {
        /// <inheritdoc />
        public Task<byte[]?> GetAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<byte[]?>(null);
        }

        /// <inheritdoc />
        public Task SetAsync(string storageKey, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
