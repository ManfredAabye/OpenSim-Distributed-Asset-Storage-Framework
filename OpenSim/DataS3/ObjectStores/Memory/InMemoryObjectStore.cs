using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.ObjectStores.Memory
{
    /// <summary>
    /// In-memory object store for local development and early integration tests.
    /// </summary>
    public sealed class InMemoryObjectStore : IObjectStore
    {
        private sealed class StoredObject
        {
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public IReadOnlyDictionary<string, string>? Metadata { get; init; }
            public DateTime StoredAtUtc { get; init; }
        }

        private readonly ConcurrentDictionary<string, StoredObject> _objects = new ConcurrentDictionary<string, StoredObject>(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_objects.TryGetValue(key, out StoredObject? stored))
                throw new KeyNotFoundException($"Object key not found: {key}");

            Stream copy = new MemoryStream(stored.Data, writable: false);
            return Task.FromResult(copy);
        }

        /// <inheritdoc />
        public async Task PutAsync(
            string key,
            Stream data,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            using (MemoryStream buffer = new MemoryStream())
            {
                await data.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
                _objects[key] = new StoredObject
                {
                    Data = buffer.ToArray(),
                    Metadata = metadata,
                    StoredAtUtc = DateTime.UtcNow
                };
            }
        }

        /// <inheritdoc />
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_objects.ContainsKey(key));
        }

        /// <inheritdoc />
        public Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_objects.TryGetValue(key, out StoredObject? stored))
                throw new KeyNotFoundException($"Object key not found: {key}");

            return Task.FromResult(new ObjectStat
            {
                SizeBytes = stored.Data.LongLength,
                ETag = null,
                ContentType = stored.Metadata != null && stored.Metadata.TryGetValue("ContentType", out string? value)
                    ? value
                    : "application/octet-stream"
            });
        }
    }
}
