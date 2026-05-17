using System;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Observability;
using OpenSim.DataS3.Providers;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class DataS3DashboardTests : OpenSimTestCase
    {
        [Test]
        public void TestDashboardSnapshotContainsOperationalAndMigrationSections()
        {
            HybridAssetData provider = new HybridAssetData();
            provider.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false");

            try
            {
                AssetBase asset = new AssetBase(UUID.Random(), "dash-asset", (sbyte)0, UUID.Zero.ToString())
                {
                    Description = "dashboard",
                    Data = new byte[] { 1, 2, 3, 4 },
                    Flags = AssetFlags.Normal
                };

                Assert.That(provider.StoreAsset(asset), Is.True);
                Assert.That(provider.GetAsset(asset.FullID), Is.Not.Null);

                DataS3DashboardSnapshot snapshot = provider.GetDashboardSnapshot();

                Assert.That(snapshot.Operational.UploadSuccessCount, Is.EqualTo(1));
                Assert.That(snapshot.Operational.ReadSuccessCount, Is.EqualTo(1));
                Assert.That(snapshot.Migration.DirectMigrationEnabled, Is.False);
                Assert.That(snapshot.Migration.FallbackReadTotal, Is.EqualTo(0));
                Assert.That(snapshot.Integrity.IntegrityScanEnabled, Is.False);
            }
            finally
            {
                provider.Dispose();
            }
        }

        [Test]
        public void TestDashboardJsonIsValidAndContainsExpectedRootSections()
        {
            HybridAssetData provider = new HybridAssetData();
            provider.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false");

            try
            {
                string json = provider.GetDashboardJson();
                using JsonDocument doc = JsonDocument.Parse(json);

                JsonElement root = doc.RootElement;
                Assert.That(root.TryGetProperty("generatedAtUtc", out _), Is.True);
                Assert.That(root.TryGetProperty("operational", out _), Is.True);
                Assert.That(root.TryGetProperty("migration", out _), Is.True);
                Assert.That(root.TryGetProperty("integrity", out _), Is.True);
            }
            finally
            {
                provider.Dispose();
            }
        }

        [Test]
        public void TestDashboardTracksFallbackReadsDuringLegacyFallbackFlow()
        {
            UUID assetId = UUID.Random();
            string legacyDbPath = Path.Combine(Path.GetTempPath(), $"datas3-dashboard-legacy-{System.Guid.NewGuid():N}.db");

            try
            {
                SeedLegacyAsset(legacyDbPath, assetId, new byte[] { 5, 6, 7, 8 }, "dash-legacy");

                string connection =
                    "ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false;" +
                    "LegacyAssetProvider=SQLite;" +
                    $"LegacyAssetConnectionString=Data Source={legacyDbPath};" +
                    "FallbackReadEnabled=true;ReadThroughMigrationEnabled=false";

                HybridAssetData provider = new HybridAssetData();
                provider.Initialise(connection);

                try
                {
                    Assert.That(provider.GetAsset(assetId), Is.Not.Null);

                    DataS3DashboardSnapshot snapshot = provider.GetDashboardSnapshot();
                    Assert.That(snapshot.Migration.FallbackReadTotal, Is.EqualTo(1));
                }
                finally
                {
                    provider.Dispose();
                }
            }
            finally
            {
                TryDeleteTempFile(legacyDbPath);
            }
        }

        private static void SeedLegacyAsset(string legacyDbPath, UUID assetId, byte[] data, string name)
        {
            using Microsoft.Data.Sqlite.SqliteConnection conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDbPath}");
            conn.Open();

            using Microsoft.Data.Sqlite.SqliteCommand create = conn.CreateCommand();
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS assets (" +
                "id TEXT PRIMARY KEY, " +
                "name TEXT NOT NULL, " +
                "description TEXT NOT NULL, " +
                "assetType INTEGER NOT NULL, " +
                "data BLOB NOT NULL, " +
                "asset_flags INTEGER NOT NULL, " +
                "CreatorID TEXT NOT NULL)";
            create.ExecuteNonQuery();

            using Microsoft.Data.Sqlite.SqliteCommand upsert = conn.CreateCommand();
            upsert.CommandText =
                "INSERT INTO assets(id, name, description, assetType, data, asset_flags, CreatorID) " +
                "VALUES($id, $name, $description, $assetType, $data, $asset_flags, $creator) " +
                "ON CONFLICT(id) DO UPDATE SET " +
                "name=excluded.name, description=excluded.description, assetType=excluded.assetType, data=excluded.data, " +
                "asset_flags=excluded.asset_flags, CreatorID=excluded.CreatorID";
            upsert.Parameters.AddWithValue("$id", assetId.ToString());
            upsert.Parameters.AddWithValue("$name", name);
            upsert.Parameters.AddWithValue("$description", "legacy-seeded");
            upsert.Parameters.AddWithValue("$assetType", 0);
            upsert.Parameters.Add("$data", Microsoft.Data.Sqlite.SqliteType.Blob).Value = data;
            upsert.Parameters.AddWithValue("$asset_flags", 0);
            upsert.Parameters.AddWithValue("$creator", UUID.Zero.ToString());
            upsert.ExecuteNonQuery();
        }

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
