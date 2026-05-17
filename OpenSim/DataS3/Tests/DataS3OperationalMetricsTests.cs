using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Caching.Noop;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Metadata.Memory;
using OpenSim.DataS3.ObjectStores.Memory;
using OpenSim.DataS3.Observability;
using OpenSim.DataS3.Providers;
using OpenSim.DataS3.RateControl;
using OpenSim.DataS3.UploadQueue;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class DataS3OperationalMetricsTests : OpenSimTestCase
    {
        [Test]
        public void TestMetricsCaptureSuccessfulPutAndGet()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            UUID assetId = UUID.Random();
            byte[] payload = new byte[] { 1, 3, 5, 7, 9 };
            using (MemoryStream stream = new MemoryStream(payload, writable: false))
            {
                bool stored = provider.PutAsync(
                    assetId,
                    UUID.Random(),
                    0,
                    stream,
                    payload.Length,
                    "metric-ok",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None).GetAwaiter().GetResult();

                Assert.That(stored, Is.True);
            }

            var getResult = provider.GetAsync(assetId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(getResult, Is.Not.Null);
            getResult!.Value.Data.Dispose();

            DataS3OperationalMetricsSnapshot snapshot = provider.GetOperationalMetricsSnapshot();
            Assert.That(snapshot.UploadSuccessCount, Is.EqualTo(1));
            Assert.That(snapshot.ReadSuccessCount, Is.EqualTo(1));
            Assert.That(snapshot.UploadedBytes, Is.EqualTo(payload.Length));
            Assert.That(snapshot.PutLatencyP95Ms, Is.GreaterThanOrEqualTo(0));
            Assert.That(snapshot.PutLatencyP99Ms, Is.GreaterThanOrEqualTo(snapshot.PutLatencyP95Ms));
            Assert.That(snapshot.GetLatencyP95Ms, Is.GreaterThanOrEqualTo(0));
            Assert.That(snapshot.GetLatencyP99Ms, Is.GreaterThanOrEqualTo(snapshot.GetLatencyP95Ms));
            Assert.That(snapshot.Upload429Rate, Is.EqualTo(0));
            Assert.That(snapshot.ErrorRate, Is.EqualTo(0));
            Assert.That(snapshot.ObjectStoreCalls, Is.GreaterThan(0));
            Assert.That(snapshot.ObjectStoreAvailability, Is.EqualTo(1));
        }

        [Test]
        public void TestMetricsCapture429Rate()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                new InMemoryAssetMetadataStore(),
                new AlwaysDenyRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            using MemoryStream payload = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
            Assert.ThrowsAsync<UploadRateLimitExceededException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload,
                    payload.Length,
                    "429",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None));

            DataS3OperationalMetricsSnapshot snapshot = provider.GetOperationalMetricsSnapshot();
            Assert.That(snapshot.UploadSuccessCount, Is.EqualTo(0));
            Assert.That(snapshot.UploadRateLimitedCount, Is.EqualTo(1));
            Assert.That(snapshot.Upload429Rate, Is.EqualTo(1));
        }

        [Test]
        public void TestMetricsCaptureObjectStoreFailures()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FailingExistsObjectStore(),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            using MemoryStream payload = new MemoryStream(new byte[] { 4, 4, 4 }, writable: false);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload,
                    payload.Length,
                    "object-fail",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None));

            DataS3OperationalMetricsSnapshot snapshot = provider.GetOperationalMetricsSnapshot();
            Assert.That(snapshot.UploadFailureCount, Is.EqualTo(1));
            Assert.That(snapshot.ErrorRate, Is.GreaterThan(0));
            Assert.That(snapshot.ObjectStoreCalls, Is.EqualTo(1));
            Assert.That(snapshot.ObjectStoreFailures, Is.EqualTo(1));
            Assert.That(snapshot.ObjectStoreAvailability, Is.EqualTo(0));
        }

        private sealed class AlwaysDenyRateLimiter : IUploadRateLimiter
        {
            public Task<bool> CanUploadAsync(UUID userId, long bytes, CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }

            public Task RecordUploadAsync(UUID userId, long bytes, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<OpenSim.DataS3.Models.QuotaStatus> GetQuotaStatusAsync(UUID userId, CancellationToken cancellationToken)
            {
                return Task.FromResult(new OpenSim.DataS3.Models.QuotaStatus
                {
                    DenyReason = "test",
                    RetryAfterSeconds = 10
                });
            }

            public Task ResetQuotaAsync(UUID userId, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task AddQuotaBytesAsync(UUID userId, long additionalBytes, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class FailingExistsObjectStore : IObjectStore
        {
            public Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
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
                throw new InvalidOperationException("Simulated exists failure");
            }

            public Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ObjectStat());
            }
        }
    }
}
