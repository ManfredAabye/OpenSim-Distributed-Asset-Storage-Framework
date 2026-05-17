using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Caching.Noop;
using OpenSim.DataS3.Common;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Metadata.Memory;
using OpenSim.DataS3.Models;
using OpenSim.DataS3.ObjectStores.Memory;
using OpenSim.DataS3.Providers;
using OpenSim.DataS3.RateControl;
using OpenSim.DataS3.UploadQueue;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class HybridAssetDataProviderFailureTests : OpenSimTestCase
    {
        [Test]
        public void TestPutFailsWhenObjectStoreIsUnavailable()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FailingObjectStore(failPut: true),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            using MemoryStream payload = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false);

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload,
                    payload.Length,
                    "failure-put",
                    "simulated put failure",
                    string.Empty,
                    0,
                    CancellationToken.None));
        }

        [Test]
        public void TestPutCanBeCancelledOnSlowMetadataStore()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                new DelayedMetadataStore(TimeSpan.FromSeconds(2)),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            using MemoryStream payload = new MemoryStream(new byte[] { 9, 8, 7, 6 }, writable: false);
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload,
                    payload.Length,
                    "slow-db",
                    "simulated slow metadata",
                    string.Empty,
                    0,
                    cts.Token));
        }

        [Test]
        public void TestGetFailsOnChecksumMismatch()
        {
            UUID assetId = UUID.Random();
            string storageKey = "type-0/aa/bb/corrupted";
            byte[] payload = new byte[] { 1, 2, 3, 4 };

            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FixedPayloadObjectStore(payload),
                new StaticMetadataStore(new AssetMetadataRecord
                {
                    AssetId = assetId,
                    StorageKey = storageKey,
                    Checksum = "deadbeef",
                    ContentHash = "deadbeef",
                    AssetType = 0,
                    Name = "checksum-mismatch",
                    Description = string.Empty,
                    CreatorId = string.Empty,
                    ContentType = "application/octet-stream",
                    StorageProvider = "test",
                    StorageBucket = "assets"
                }),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await provider.GetAsync(assetId, CancellationToken.None));
        }

        [Test]
        public void TestGetSucceedsWhenChecksumMatches()
        {
            UUID assetId = UUID.Random();
            string storageKey = "type-0/cc/dd/valid";
            byte[] payload = new byte[] { 7, 8, 9, 10 };
            string hash = AssetObjectKeyBuilder.ComputeSha256Hex(payload);

            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FixedPayloadObjectStore(payload),
                new StaticMetadataStore(new AssetMetadataRecord
                {
                    AssetId = assetId,
                    StorageKey = storageKey,
                    Checksum = hash,
                    ContentHash = hash,
                    AssetType = 0,
                    Name = "checksum-ok",
                    Description = string.Empty,
                    CreatorId = string.Empty,
                    ContentType = "application/octet-stream",
                    StorageProvider = "test",
                    StorageBucket = "assets"
                }),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            var result = provider.GetAsync(assetId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(result, Is.Not.Null);
        }

        private sealed class FailingObjectStore : IObjectStore
        {
            private readonly bool _failPut;

            public FailingObjectStore(bool failPut)
            {
                _failPut = failPut;
            }

            public Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated object store get failure.");
            }

            public Task PutAsync(string key, Stream data, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
            {
                if (_failPut)
                    throw new InvalidOperationException("Simulated object store put failure.");

                return Task.CompletedTask;
            }

            public Task DeleteAsync(string key, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }

            public Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ObjectStat());
            }
        }

        private sealed class DelayedMetadataStore : IAssetMetadataStore
        {
            private readonly TimeSpan _delay;
            private readonly InMemoryAssetMetadataStore _inner = new InMemoryAssetMetadataStore();

            public DelayedMetadataStore(TimeSpan delay)
            {
                _delay = delay;
            }

            public Task<OpenSim.DataS3.Models.AssetMetadataRecord?> GetAsync(UUID id, CancellationToken cancellationToken)
            {
                return _inner.GetAsync(id, cancellationToken);
            }

            public async Task StoreAsync(OpenSim.DataS3.Models.AssetMetadataRecord metadata, CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                await _inner.StoreAsync(metadata, cancellationToken).ConfigureAwait(false);
            }

            public Task DeleteAsync(UUID id, CancellationToken cancellationToken)
            {
                return _inner.DeleteAsync(id, cancellationToken);
            }

            public Task<bool> ExistsAsync(UUID id, CancellationToken cancellationToken)
            {
                return _inner.ExistsAsync(id, cancellationToken);
            }

            public Task<IReadOnlyList<OpenSim.DataS3.Models.AssetMetadataRecord>> ListAsync(int start, int count, CancellationToken cancellationToken)
            {
                return _inner.ListAsync(start, count, cancellationToken);
            }

            public Task<bool> HasOtherReferencesAsync(string storageKey, UUID assetId, CancellationToken cancellationToken)
            {
                return _inner.HasOtherReferencesAsync(storageKey, assetId, cancellationToken);
            }
        }

        private sealed class StaticMetadataStore : IAssetMetadataStore
        {
            private readonly AssetMetadataRecord _record;

            public StaticMetadataStore(AssetMetadataRecord record)
            {
                _record = record;
            }

            public Task<AssetMetadataRecord?> GetAsync(UUID id, CancellationToken cancellationToken)
            {
                return Task.FromResult<AssetMetadataRecord?>(id == _record.AssetId ? _record : null);
            }

            public Task StoreAsync(AssetMetadataRecord metadata, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(UUID id, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(UUID id, CancellationToken cancellationToken)
            {
                return Task.FromResult(id == _record.AssetId);
            }

            public Task<IReadOnlyList<AssetMetadataRecord>> ListAsync(int start, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult<IReadOnlyList<AssetMetadataRecord>>(new[] { _record });
            }

            public Task<bool> HasOtherReferencesAsync(string storageKey, UUID assetId, CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }
        }

        private sealed class FixedPayloadObjectStore : IObjectStore
        {
            private readonly byte[] _payload;

            public FixedPayloadObjectStore(byte[] payload)
            {
                _payload = payload;
            }

            public Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));
            }

            public Task PutAsync(string key, Stream data, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string key, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ObjectStat { SizeBytes = _payload.LongLength, ContentType = "application/octet-stream" });
            }
        }
    }
}
