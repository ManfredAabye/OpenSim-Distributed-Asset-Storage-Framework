using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Models;

namespace OpenSim.DataS3.Metadata.Memory
{
    /// <summary>
    /// In-memory metadata store with deterministic paging for local tests.
    /// </summary>
    public sealed class InMemoryAssetMetadataStore : IAssetMetadataStore
    {
        private readonly ConcurrentDictionary<UUID, AssetMetadataRecord> _entries = new ConcurrentDictionary<UUID, AssetMetadataRecord>();

        /// <inheritdoc />
        public Task<AssetMetadataRecord?> GetAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.TryGetValue(id, out AssetMetadataRecord? value);
            return Task.FromResult(value);
        }

        /// <inheritdoc />
        public Task StoreAsync(AssetMetadataRecord metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            _entries[metadata.AssetId] = metadata;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeleteAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.ContainsKey(id));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AssetMetadataRecord>> ListAsync(int start, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (start < 0)
                start = 0;
            if (count <= 0)
                return Task.FromResult<IReadOnlyList<AssetMetadataRecord>>(Array.Empty<AssetMetadataRecord>());

            List<AssetMetadataRecord> items = _entries
                .OrderBy(kvp => kvp.Key)
                .Skip(start)
                .Take(count)
                .Select(kvp => kvp.Value)
                .ToList();

            return Task.FromResult<IReadOnlyList<AssetMetadataRecord>>(items);
        }

        /// <inheritdoc />
        public Task<bool> HasOtherReferencesAsync(string storageKey, UUID assetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool hasOtherReferences = _entries.Values.Any(entry => entry.AssetId != assetId && entry.StorageKey == storageKey);
            return Task.FromResult(hasOtherReferences);
        }
    }
}
