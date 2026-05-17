using System.IO;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Caching.Noop;
using OpenSim.DataS3.Common;
using OpenSim.DataS3.Integrity;
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
    public class BackgroundAssetIntegrityVerifierTests : OpenSimTestCase
    {
        [Test]
        public void TestRunOnceReportsHealthyAssetAsVerified()
        {
            HybridAssetDataProvider provider = CreateProvider();

            byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
            using (MemoryStream stream = new MemoryStream(payload, writable: false))
            {
                bool stored = provider.PutAsync(
                    UUID.Random(),
                    UUID.Random(),
                    0,
                    stream,
                    payload.Length,
                    "ok",
                    string.Empty,
                    string.Empty,
                    0,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.That(stored, Is.True);
            }

            using BackgroundAssetIntegrityVerifier verifier = new BackgroundAssetIntegrityVerifier(provider, 100, System.TimeSpan.FromMinutes(30));
            AssetIntegrityScanReport report = verifier.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(report.TotalMetadataRows, Is.EqualTo(1));
            Assert.That(report.VerifiedOk, Is.EqualTo(1));
            Assert.That(report.MissingOrUnreadableObjects, Is.EqualTo(0));
            Assert.That(report.ChecksumMismatches, Is.EqualTo(0));
            Assert.That(report.OtherFailures, Is.EqualTo(0));
            Assert.That(report.Succeeded, Is.True);
        }

        [Test]
        public void TestRunOnceReportsMissingObjectAsFailure()
        {
            InMemoryAssetMetadataStore metadata = new InMemoryAssetMetadataStore();
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                metadata,
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            UUID id = UUID.Random();
            string fakeHash = AssetObjectKeyBuilder.ComputeSha256Hex(new byte[] { 9, 9, 9 });
            metadata.StoreAsync(new AssetMetadataRecord
            {
                AssetId = id,
                ContentHash = fakeHash,
                Checksum = fakeHash,
                AssetType = 0,
                Name = "missing",
                Description = string.Empty,
                CreatorId = string.Empty,
                Flags = 0,
                ContentType = "application/octet-stream",
                SizeBytes = 3,
                StorageProvider = "InMemoryObjectStore",
                StorageBucket = "assets",
                StorageKey = "type-0/ff/ff/nonexistent"
            }, CancellationToken.None).GetAwaiter().GetResult();

            using BackgroundAssetIntegrityVerifier verifier = new BackgroundAssetIntegrityVerifier(provider, 100, System.TimeSpan.FromMinutes(30));
            AssetIntegrityScanReport report = verifier.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(report.TotalMetadataRows, Is.EqualTo(1));
            Assert.That(report.VerifiedOk, Is.EqualTo(0));
            Assert.That(report.MissingOrUnreadableObjects, Is.EqualTo(1));
            Assert.That(report.Succeeded, Is.False);
        }

        [Test]
        public void TestRunRepairOnceReindexesChecksumMismatch()
        {
            InMemoryObjectStore objectStore = new InMemoryObjectStore();
            InMemoryAssetMetadataStore metadata = new InMemoryAssetMetadataStore();
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                objectStore,
                metadata,
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            UUID id = UUID.Random();
            byte[] payload = new byte[] { 10, 20, 30, 40 };
            string actualHash = AssetObjectKeyBuilder.ComputeSha256Hex(payload);
            string storageKey = AssetObjectKeyBuilder.BuildStorageKey(0, actualHash);

            using (MemoryStream data = new MemoryStream(payload, writable: false))
                objectStore.PutAsync(storageKey, data, null, CancellationToken.None).GetAwaiter().GetResult();

            metadata.StoreAsync(new AssetMetadataRecord
            {
                AssetId = id,
                ContentHash = "deadbeef",
                Checksum = "deadbeef",
                AssetType = 0,
                Name = "checksum-mismatch",
                Description = string.Empty,
                CreatorId = string.Empty,
                Flags = 0,
                ContentType = "application/octet-stream",
                SizeBytes = payload.Length,
                StorageProvider = "InMemoryObjectStore",
                StorageBucket = "assets",
                StorageKey = storageKey
            }, CancellationToken.None).GetAwaiter().GetResult();

            using BackgroundAssetIntegrityVerifier verifier = new BackgroundAssetIntegrityVerifier(provider, 100, System.TimeSpan.FromMinutes(30), repairEnabled: true);
            AssetIntegrityScanReport report = verifier.RunRepairOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(report.ChecksumMismatches, Is.EqualTo(1));
            Assert.That(report.ReindexedChecksums, Is.EqualTo(1));
            Assert.That(report.RepairsSucceeded, Is.EqualTo(1));
            Assert.That(report.UnresolvedFailures, Is.EqualTo(0));
            Assert.That(report.Succeeded, Is.True);

            var getResult = provider.GetAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(getResult.HasValue, Is.True);
            getResult!.Value.Data.Dispose();
        }

        [Test]
        public void TestRunRepairOnceMarksMissingObjectAsInconsistent()
        {
            InMemoryAssetMetadataStore metadata = new InMemoryAssetMetadataStore();
            HybridAssetDataProvider provider = new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                metadata,
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());

            UUID id = UUID.Random();
            string fakeHash = AssetObjectKeyBuilder.ComputeSha256Hex(new byte[] { 9, 9, 9 });
            metadata.StoreAsync(new AssetMetadataRecord
            {
                AssetId = id,
                ContentHash = fakeHash,
                Checksum = fakeHash,
                AssetType = 0,
                Name = "missing",
                Description = string.Empty,
                CreatorId = string.Empty,
                Flags = 0,
                ContentType = "application/octet-stream",
                SizeBytes = 3,
                StorageProvider = "InMemoryObjectStore",
                StorageBucket = "assets",
                StorageKey = "type-0/ff/ff/nonexistent"
            }, CancellationToken.None).GetAwaiter().GetResult();

            using BackgroundAssetIntegrityVerifier verifier = new BackgroundAssetIntegrityVerifier(provider, 100, System.TimeSpan.FromMinutes(30), repairEnabled: true);
            AssetIntegrityScanReport report = verifier.RunRepairOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(report.MissingOrUnreadableObjects, Is.EqualTo(1));
            Assert.That(report.MarkedInconsistentEntries, Is.EqualTo(1));
            Assert.That(report.RepairsSucceeded, Is.EqualTo(1));
            Assert.That(report.UnresolvedFailures, Is.EqualTo(0));
            Assert.That(report.Succeeded, Is.True);

            AssetMetadataRecord? row = metadata.GetAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(row, Is.Not.Null);
            Assert.That((row!.Flags & AssetIntegrityFlags.InconsistentEntry) != 0, Is.True);
            Assert.That(row.Description.Contains("DATAS3-INTEGRITY"), Is.True);
        }

        private static HybridAssetDataProvider CreateProvider()
        {
            return new HybridAssetDataProvider(
                new InMemoryObjectStore(),
                new InMemoryAssetMetadataStore(),
                new NoopUploadRateLimiter(),
                new NoopAssetReadCache(),
                new InlineAssetUploadQueue());
        }
    }
}