using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Caching.Noop;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Metadata.Memory;
using OpenSim.DataS3.ObjectStores.Memory;
using OpenSim.DataS3.Providers;
using OpenSim.DataS3.RateControl;
using OpenSim.DataS3.Resilience;
using OpenSim.DataS3.Security;
using OpenSim.DataS3.UploadQueue;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class HybridAssetDataSecurityLimitsTests : OpenSimTestCase
    {
        [TearDown]
        public void CleanupContext()
        {
            DataS3RequestContext.Current = null;
        }

        [Test]
        public void StoreAssetRequiresAuthenticatedContextWhenEnabled()
        {
            HybridAssetData plugin = new HybridAssetData();
            plugin.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;AuthRequired=true");

            try
            {
                AssetBase asset = NewAsset(new byte[] { 1, 2, 3 });
                Assert.Throws<UnauthorizedAccessException>(() => plugin.StoreAsset(asset));
            }
            finally
            {
                plugin.Dispose();
            }
        }

        [Test]
        public void RoleBasedUploadLimitBlocksOversizedPayload()
        {
            HybridAssetData plugin = new HybridAssetData();
            plugin.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;AuthRequired=true;RoleUserMaxUploadBytes=3");

            try
            {
                DataS3RequestContext.Current = new DataS3RequestContext
                {
                    IsAuthenticated = true,
                    UserId = UUID.Random(),
                    RemoteIp = "10.0.0.5",
                    Roles = new[] { DataS3Role.User }
                };

                AssetBase asset = NewAsset(new byte[] { 1, 2, 3, 4 });
                Assert.Throws<InvalidOperationException>(() => plugin.StoreAsset(asset));
            }
            finally
            {
                plugin.Dispose();
            }
        }

        [Test]
        public void QuotaResetRequiresAdminRoleWhenConfigured()
        {
            HybridAssetData plugin = new HybridAssetData();
            plugin.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;AdminOnlyQuotaManagement=true");

            try
            {
                DataS3RequestContext.Current = new DataS3RequestContext
                {
                    IsAuthenticated = true,
                    UserId = UUID.Random(),
                    Roles = new[] { DataS3Role.User }
                };

                Assert.Throws<UnauthorizedAccessException>(() => plugin.ResetQuota(UUID.Random()));
            }
            finally
            {
                plugin.Dispose();
            }
        }

        [Test]
        public void IpRateLimitTriggersTemporaryBan()
        {
            HybridAssetData plugin = new HybridAssetData();
            plugin.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;AuthRequired=true;IpRateLimitEnabled=true;IpMaxRequestsPerMinute=1;IpBanSeconds=60");

            try
            {
                DataS3RequestContext.Current = new DataS3RequestContext
                {
                    IsAuthenticated = true,
                    UserId = UUID.Random(),
                    RemoteIp = "192.168.1.10",
                    Roles = new[] { DataS3Role.User }
                };

                plugin.AssetsExist(Array.Empty<UUID>());
                Assert.Throws<InvalidOperationException>(() => plugin.AssetsExist(Array.Empty<UUID>()));
            }
            finally
            {
                plugin.Dispose();
            }
        }

        [Test]
        public void ProviderAppliesGlobalLimiter()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FailingExistsObjectStore(),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue(),
                new GlobalUploadLimiter(maxConcurrentUploads: 1, maxBytesPerSecond: 3),
            null);

            using MemoryStream bigPayload = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    bigPayload,
                    bigPayload.Length,
                    "too-big",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None));

        }

        [Test]
        public void ProviderAppliesCircuitBreakerAfterObjectStoreFailures()
        {
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new FailingExistsObjectStore(),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue(),
                null,
                new DataS3CircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(5)));

            using MemoryStream payload = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload,
                    payload.Length,
                    "first-fail",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None));

            using MemoryStream payload2 = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
            InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    payload2,
                    payload2.Length,
                    "circuit-open",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None));

            Assert.That(ex!.Message.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase), Is.True);
        }

        private static AssetBase NewAsset(byte[] payload)
        {
            return new AssetBase(UUID.Random(), "secure-asset", (sbyte)0, UUID.Random().ToString())
            {
                Description = string.Empty,
                Data = payload,
                Flags = AssetFlags.Normal
            };
        }

        private sealed class FailingExistsObjectStore : IObjectStore
        {
            public Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
            }

            public Task PutAsync(string key, Stream data, System.Collections.Generic.IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string key, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated object store failure");
            }

            public Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ObjectStat());
            }
        }
    }
}
