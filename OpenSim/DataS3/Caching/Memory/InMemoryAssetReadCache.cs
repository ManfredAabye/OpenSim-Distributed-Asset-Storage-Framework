using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.Caching.Memory
{
    /// <summary>
    /// Simple in-memory read cache for object payloads.
    /// </summary>
    public sealed class InMemoryAssetReadCache : IAssetReadCache
    {
        private sealed class CacheEntry
        {
            public byte[] Payload { get; init; } = Array.Empty<byte>();

            public DateTime ExpiresAtUtc { get; init; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _entries = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly TimeSpan _ttl;

        /// <summary>
        /// Creates a cache with the given entry TTL.
        /// </summary>
        public InMemoryAssetReadCache(TimeSpan ttl)
        {
            _ttl = ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : ttl;
        }

        /// <inheritdoc />
        public Task<byte[]?> GetAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_entries.TryGetValue(storageKey, out CacheEntry? entry))
                return Task.FromResult<byte[]?>(null);

            if (entry.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _entries.TryRemove(storageKey, out _);
                return Task.FromResult<byte[]?>(null);
            }

            byte[] copy = new byte[entry.Payload.Length];
            Buffer.BlockCopy(entry.Payload, 0, copy, 0, copy.Length);
            return Task.FromResult<byte[]?>(copy);
        }

        /// <inheritdoc />
        public Task SetAsync(string storageKey, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            byte[] copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, copy.Length);

            _entries[storageKey] = new CacheEntry
            {
                Payload = copy,
                ExpiresAtUtc = DateTime.UtcNow.Add(_ttl)
            };

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.TryRemove(storageKey, out _);
            return Task.CompletedTask;
        }
    }
}
