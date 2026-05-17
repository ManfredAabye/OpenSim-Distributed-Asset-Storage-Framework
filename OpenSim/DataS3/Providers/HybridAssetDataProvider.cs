using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Common;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Models;
using OpenSim.DataS3.Observability;
using OpenSim.DataS3.Resilience;
using OpenSim.DataS3.RateControl;

namespace OpenSim.DataS3.Providers
{
    /// <summary>
    /// Core DataS3 asset provider logic coordinating object storage, metadata, and rate control.
    /// </summary>
    public sealed class HybridAssetDataProvider : IDisposable
    {
        private readonly IObjectStore _objectStore;
        private readonly IAssetMetadataStore _metadataStore;
        private readonly IUploadRateLimiter _rateLimiter;
        private readonly IAssetReadCache _readCache;
        private readonly IAssetUploadQueue _uploadQueue;
        private readonly DataS3MetricsCollector _metrics;
        private readonly GlobalUploadLimiter? _globalUploadLimiter;
        private readonly DataS3CircuitBreaker? _circuitBreaker;
        private readonly string _storageProviderName;
        private readonly ConcurrentDictionary<string, Task<byte[]>> _inFlightReads = new ConcurrentDictionary<string, Task<byte[]>>(StringComparer.Ordinal);

        /// <summary>
        /// Creates a provider instance with explicit dependencies.
        /// </summary>
        /// <param name="objectStore">Binary object store implementation.</param>
        /// <param name="metadataStore">Asset metadata storage implementation.</param>
        /// <param name="rateLimiter">Upload rate limiter implementation.</param>
        public HybridAssetDataProvider(
            IObjectStore objectStore,
            IAssetMetadataStore metadataStore,
            IUploadRateLimiter rateLimiter,
            IAssetReadCache readCache,
            IAssetUploadQueue uploadQueue,
            GlobalUploadLimiter? globalUploadLimiter = null,
            DataS3CircuitBreaker? circuitBreaker = null)
        {
            _objectStore = objectStore;
            _metadataStore = metadataStore;
            _rateLimiter = rateLimiter;
            _readCache = readCache;
            _uploadQueue = uploadQueue;
            _metrics = new DataS3MetricsCollector();
            _globalUploadLimiter = globalUploadLimiter;
            _circuitBreaker = circuitBreaker;
            _storageProviderName = objectStore.GetType().Name;
        }

        /// <summary>
        /// Reads metadata for an asset.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata record if available; otherwise null.</returns>
        public async Task<AssetMetadataRecord?> GetMetadataAsync(UUID assetId, CancellationToken cancellationToken)
        {
            return await _metadataStore.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Stores a new or updated asset payload and its metadata.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="userId">User performing the upload.</param>
        /// <param name="assetType">OpenSim asset type.</param>
        /// <param name="data">Payload stream.</param>
        /// <param name="sizeBytes">Payload size.</param>
        /// <param name="name">Asset name.</param>
        /// <param name="description">Asset description.</param>
        /// <param name="creatorId">Creator identifier.</param>
        /// <param name="flags">Asset flags.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if stored; false if blocked by rate control.</returns>
        public async Task<bool> PutAsync(
            UUID assetId,
            UUID userId,
            int assetType,
            Stream data,
            long sizeBytes,
            string name,
            string description,
            string creatorId,
            int flags,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            GlobalUploadLimiter.GlobalUploadLease? globalLease = null;

            if (!await _rateLimiter.CanUploadAsync(userId, sizeBytes, cancellationToken).ConfigureAwait(false))
            {
                QuotaStatus status = await _rateLimiter.GetQuotaStatusAsync(userId, cancellationToken).ConfigureAwait(false);
                _metrics.RecordUploadRateLimited(stopwatch.ElapsedMilliseconds);
                throw new UploadRateLimitExceededException(
                    $"Upload denied by rate control (reason={status.DenyReason ?? "unknown"}, retryAfter={status.RetryAfterSeconds}s).",
                    status);
            }

            if (_globalUploadLimiter != null)
            {
                globalLease = await _globalUploadLimiter.TryAcquireAsync(sizeBytes, cancellationToken).ConfigureAwait(false);
                if (globalLease == null)
                {
                    _metrics.RecordUploadFailure(stopwatch.ElapsedMilliseconds);
                    throw new InvalidOperationException("Global upload limit exceeded.");
                }
            }

            bool completed = false;
            try
            {
                AssetMetadataRecord? existingMetadata = await _metadataStore.GetAsync(assetId, cancellationToken).ConfigureAwait(false);

                byte[] payload;
                using (MemoryStream buffer = new MemoryStream())
                {
                    await data.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
                    payload = buffer.ToArray();
                }

                if (sizeBytes <= 0)
                    sizeBytes = payload.LongLength;

                string hash = AssetObjectKeyBuilder.ComputeSha256Hex(payload);
                string storageKey = AssetObjectKeyBuilder.BuildStorageKey(assetType, hash);

                bool persisted = await _uploadQueue.EnqueueAsync(
                    queueToken => PersistAssetAsync(
                        existingMetadata,
                        assetId,
                        assetType,
                        name,
                        description,
                        creatorId,
                        flags,
                        sizeBytes,
                        hash,
                        storageKey,
                        payload,
                        queueToken),
                    cancellationToken).ConfigureAwait(false);

                if (!persisted)
                {
                    _metrics.RecordUploadFailure(stopwatch.ElapsedMilliseconds);
                    return false;
                }

                completed = true;
                _metrics.RecordUploadSuccess(sizeBytes, stopwatch.ElapsedMilliseconds);
                return true;
            }
            catch
            {
                _metrics.RecordUploadFailure(stopwatch.ElapsedMilliseconds);
                throw;
            }
            finally
            {
                globalLease?.Dispose();
                long recordedBytes = completed ? Math.Max(0, sizeBytes) : 0;
                await _rateLimiter.RecordUploadAsync(userId, recordedBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads an asset metadata row and opens a stream for its object payload.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Tuple of metadata and data stream, or null if asset is unknown.</returns>
        public async Task<(AssetMetadataRecord Metadata, Stream Data)?> GetAsync(UUID assetId, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                var raw = await GetRawPayloadAsync(assetId, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                {
                    _metrics.RecordReadSuccess(stopwatch.ElapsedMilliseconds);
                    return null;
                }

                AssetMetadataRecord metadata = raw.Value.Metadata;
                byte[] payload = raw.Value.Payload;

                ValidatePayloadChecksum(metadata, payload);

                Stream data = new MemoryStream(payload, writable: false);
                _metrics.RecordReadSuccess(stopwatch.ElapsedMilliseconds);
                return (metadata, data);
            }
            catch
            {
                _metrics.RecordReadFailure(stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Reads asset metadata and payload without checksum validation.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Tuple of metadata and payload bytes, or null if asset metadata is unknown.</returns>
        public async Task<(AssetMetadataRecord Metadata, byte[] Payload)?> GetRawPayloadAsync(UUID assetId, CancellationToken cancellationToken)
        {
            AssetMetadataRecord? metadata = await _metadataStore.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (metadata == null)
                return null;

            byte[]? cachedPayload = await _readCache.GetAsync(metadata.StorageKey, cancellationToken).ConfigureAwait(false);
            byte[] payload = cachedPayload ?? await GetObjectBytesCoalescedAsync(metadata.StorageKey, cancellationToken).ConfigureAwait(false);

            if (cachedPayload == null)
                await _readCache.SetAsync(metadata.StorageKey, payload, cancellationToken).ConfigureAwait(false);

            return (metadata, payload);
        }

        /// <summary>
        /// Persists an updated metadata record.
        /// </summary>
        /// <param name="metadata">Metadata row to upsert.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task StoreMetadataAsync(AssetMetadataRecord metadata, CancellationToken cancellationToken)
        {
            return _metadataStore.StoreAsync(metadata, cancellationToken);
        }

        /// <summary>
        /// Checks if an asset exists in metadata storage.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if metadata exists.</returns>
        public Task<bool> ExistsAsync(UUID assetId, CancellationToken cancellationToken)
        {
            return _metadataStore.ExistsAsync(assetId, cancellationToken);
        }

        /// <summary>
        /// Lists metadata rows in deterministic paging order.
        /// </summary>
        /// <param name="start">Zero-based offset.</param>
        /// <param name="count">Maximum number of rows.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Paged metadata list.</returns>
        public Task<IReadOnlyList<AssetMetadataRecord>> ListMetadataAsync(int start, int count, CancellationToken cancellationToken)
        {
            return _metadataStore.ListAsync(start, count, cancellationToken);
        }

        /// <summary>
        /// Deletes asset payload and metadata.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if an existing row was deleted.</returns>
        public async Task<bool> DeleteAsync(UUID assetId, CancellationToken cancellationToken)
        {
            try
            {
                AssetMetadataRecord? metadata = await _metadataStore.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
                if (metadata == null)
                {
                    _metrics.RecordDeleteSuccess();
                    return false;
                }

                bool hasOtherReferences = await _metadataStore.HasOtherReferencesAsync(metadata.StorageKey, assetId, cancellationToken).ConfigureAwait(false);
                if (!hasOtherReferences)
                {
                    await _readCache.RemoveAsync(metadata.StorageKey, cancellationToken).ConfigureAwait(false);
                    await TrackObjectStoreCallAsync(() => _objectStore.DeleteAsync(metadata.StorageKey, cancellationToken)).ConfigureAwait(false);
                }

                await _metadataStore.DeleteAsync(assetId, cancellationToken).ConfigureAwait(false);
                _metrics.RecordDeleteSuccess();
                return true;
            }
            catch
            {
                _metrics.RecordDeleteFailure();
                throw;
            }
        }

        /// <summary>
        /// Returns a point-in-time snapshot of runtime operational metrics.
        /// </summary>
        public DataS3OperationalMetricsSnapshot GetOperationalMetricsSnapshot()
        {
            return _metrics.GetSnapshot();
        }

        private async Task<byte[]> GetObjectBytesCoalescedAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<byte[]> loadTask = _inFlightReads.GetOrAdd(storageKey, key => LoadObjectBytesAsync(key));

            try
            {
                byte[] payload = await loadTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return payload;
            }
            finally
            {
                if (loadTask.IsCompleted)
                    _inFlightReads.TryRemove(new KeyValuePair<string, Task<byte[]>>(storageKey, loadTask));
            }
        }

        private async Task<byte[]> LoadObjectBytesAsync(string storageKey)
        {
            using Stream source = await TrackObjectStoreCallAsync(() => _objectStore.GetAsync(storageKey, CancellationToken.None)).ConfigureAwait(false);
            using MemoryStream copy = new MemoryStream();
            await source.CopyToAsync(copy).ConfigureAwait(false);
            return copy.ToArray();
        }

        private async Task<bool> PersistAssetAsync(
            AssetMetadataRecord? existingMetadata,
            UUID assetId,
            int assetType,
            string name,
            string description,
            string creatorId,
            int flags,
            long sizeBytes,
            string hash,
            string storageKey,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            bool objectExists = await TrackObjectStoreCallAsync(() => _objectStore.ExistsAsync(storageKey, cancellationToken)).ConfigureAwait(false);
            if (!objectExists)
            {
                using (MemoryStream putStream = new MemoryStream(payload, writable: false))
                {
                    await TrackObjectStoreCallAsync(() => _objectStore.PutAsync(storageKey, putStream, null, cancellationToken)).ConfigureAwait(false);
                }
            }

            await _metadataStore.StoreAsync(
                new AssetMetadataRecord
                {
                    AssetId = assetId,
                    AssetType = assetType,
                    Name = name,
                    Description = description,
                    CreatorId = creatorId,
                    Flags = flags,
                    SizeBytes = sizeBytes,
                    ContentHash = hash,
                    Checksum = hash,
                    StorageProvider = _storageProviderName,
                    StorageBucket = "assets",
                    StorageKey = storageKey
                },
                cancellationToken).ConfigureAwait(false);

            await _readCache.SetAsync(storageKey, payload, cancellationToken).ConfigureAwait(false);

            if (existingMetadata != null
                && !existingMetadata.StorageKey.Equals(storageKey, StringComparison.Ordinal)
                && !await _metadataStore.HasOtherReferencesAsync(existingMetadata.StorageKey, assetId, cancellationToken).ConfigureAwait(false))
            {
                await _readCache.RemoveAsync(existingMetadata.StorageKey, cancellationToken).ConfigureAwait(false);
                await TrackObjectStoreCallAsync(() => _objectStore.DeleteAsync(existingMetadata.StorageKey, cancellationToken)).ConfigureAwait(false);
            }

            return true;
        }

        private async Task<T> TrackObjectStoreCallAsync<T>(Func<Task<T>> operation)
        {
            _circuitBreaker?.ThrowIfOpen();

            try
            {
                T result = await operation().ConfigureAwait(false);
                _metrics.RecordObjectStoreCall(success: true);
                _circuitBreaker?.RecordSuccess();
                return result;
            }
            catch
            {
                _metrics.RecordObjectStoreCall(success: false);
                _circuitBreaker?.RecordFailure();
                throw;
            }
        }

        private async Task TrackObjectStoreCallAsync(Func<Task> operation)
        {
            _circuitBreaker?.ThrowIfOpen();

            try
            {
                await operation().ConfigureAwait(false);
                _metrics.RecordObjectStoreCall(success: true);
                _circuitBreaker?.RecordSuccess();
            }
            catch
            {
                _metrics.RecordObjectStoreCall(success: false);
                _circuitBreaker?.RecordFailure();
                throw;
            }
        }

        private static void ValidatePayloadChecksum(AssetMetadataRecord metadata, byte[] payload)
        {
            string expected = string.IsNullOrWhiteSpace(metadata.Checksum)
                ? metadata.ContentHash
                : metadata.Checksum;

            if (string.IsNullOrWhiteSpace(expected))
                return;

            string actual = AssetObjectKeyBuilder.ComputeSha256Hex(payload);
            if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Checksum mismatch for asset {metadata.AssetId}: expected {expected}, actual {actual}.");
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _uploadQueue.Dispose();
        }
    }
}
