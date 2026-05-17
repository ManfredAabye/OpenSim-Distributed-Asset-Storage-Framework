using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenSim.DataS3.Compatibility;
using OpenMetaverse;
using OpenSim.DataS3.Providers;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class HybridAssetDataCompatibilityTests : OpenSimTestCase
    {
        [Test]
        public void TestLegacyCompatibleCrudAndMetadataSetFlow()
        {
            HybridAssetData provider = new HybridAssetData();
            provider.Initialise("ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false");

            try
            {
                UUID assetId = UUID.Random();
                byte[] payload = new byte[] { 1, 3, 3, 7, 9 };
                AssetBase toStore = new AssetBase(assetId, "compat-asset", (sbyte)0, UUID.Zero.ToString())
                {
                    Description = "compatibility-check",
                    Data = payload,
                    Flags = AssetFlags.Normal
                };

                Assert.That(provider.StoreAsset(toStore), Is.True, "StoreAsset should succeed.");

                bool[] existsAfterStore = provider.AssetsExist(new[] { assetId });
                Assert.That(existsAfterStore.Length, Is.EqualTo(1));
                Assert.That(existsAfterStore[0], Is.True, "AssetsExist should report stored asset.");

                AssetBase? fetched = provider.GetAsset(assetId);
                Assert.That(fetched, Is.Not.Null, "GetAsset should return the stored asset.");
                Assert.That(fetched!.FullID, Is.EqualTo(assetId));
                Assert.That(fetched.Name, Is.EqualTo("compat-asset"));
                Assert.That(fetched.Type, Is.EqualTo((sbyte)0));
                Assert.That(fetched.Data.SequenceEqual(payload), Is.True, "Fetched payload should match stored payload.");

                var metadataSet = provider.FetchAssetMetadataSet(0, 10);
                Assert.That(metadataSet.Any(m => m.FullID == assetId), Is.True, "FetchAssetMetadataSet should include stored asset metadata.");

                Assert.That(provider.Delete(assetId.ToString()), Is.True, "Delete should succeed.");

                bool[] existsAfterDelete = provider.AssetsExist(new[] { assetId });
                Assert.That(existsAfterDelete.Length, Is.EqualTo(1));
                Assert.That(existsAfterDelete[0], Is.False, "AssetsExist should report deleted asset as missing.");
                Assert.That(provider.GetAsset(assetId), Is.Null, "GetAsset should return null after delete.");
            }
            finally
            {
                provider.Dispose();
            }
        }

        [Test]
        public void TestLegacyFallbackReadThroughMigratesAsset()
        {
            UUID assetId = UUID.Random();
            byte[] payload = new byte[] { 10, 20, 30, 40 };
            string legacyDbPath = Path.Combine(Path.GetTempPath(), $"datas3-legacy-{Guid.NewGuid():N}.db");

            try
            {
                SeedLegacyAsset(legacyDbPath, assetId, payload, "legacy-readthrough");

                string connection =
                    "ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false;" +
                    "LegacyAssetProvider=SQLite;" +
                    $"LegacyAssetConnectionString=Data Source={legacyDbPath};" +
                    "FallbackReadEnabled=true;ReadThroughMigrationEnabled=true";

                HybridAssetData provider = new HybridAssetData();
                provider.Initialise(connection);

                try
                {
                    AssetBase? fetched = provider.GetAsset(assetId);
                    Assert.That(fetched, Is.Not.Null, "Legacy fallback should return the legacy asset.");
                    Assert.That(fetched!.Data.SequenceEqual(payload), Is.True, "Returned payload should match legacy payload.");

                    bool[] exists = provider.AssetsExist(new[] { assetId });
                    Assert.That(exists.Length, Is.EqualTo(1));
                    Assert.That(exists[0], Is.True, "Read-through should migrate metadata/object into DataS3.");
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

        [Test]
        public void TestDirectMigrationImportsLegacyAssetsOnInitialize()
        {
            UUID assetA = UUID.Random();
            UUID assetB = UUID.Random();
            string legacyDbPath = Path.Combine(Path.GetTempPath(), $"datas3-directmig-{Guid.NewGuid():N}.db");

            try
            {
                SeedLegacyAsset(legacyDbPath, assetA, new byte[] { 1, 1, 2, 3 }, "legacy-a");
                SeedLegacyAsset(legacyDbPath, assetB, new byte[] { 5, 8, 13, 21 }, "legacy-b");

                string connection =
                    "ObjectStore=InMemory;MetadataProvider=InMemory;RateLimitEnabled=false;" +
                    "LegacyAssetProvider=SQLite;" +
                    $"LegacyAssetConnectionString=Data Source={legacyDbPath};" +
                    "DirectMigrationEnabled=true;DirectMigrationBatchSize=1;DirectMigrationMaxAssets=10";

                HybridAssetData provider = new HybridAssetData();
                provider.Initialise(connection);

                try
                {
                    bool[] exists = provider.AssetsExist(new[] { assetA, assetB });
                    Assert.That(exists.Length, Is.EqualTo(2));
                    Assert.That(exists.All(v => v), Is.True, "Direct migration should import all legacy assets into DataS3.");

                    Assert.That(provider.GetAsset(assetA), Is.Not.Null);
                    Assert.That(provider.GetAsset(assetB), Is.Not.Null);
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

        [Test]
        public void TestLegacyTableNamesAreNormalizedWithCompatibilityViews()
        {
            string legacyDbPath = Path.Combine(Path.GetTempPath(), $"datas3-legacy-names-{Guid.NewGuid():N}.db");

            try
            {
                using (SqliteConnection conn = new SqliteConnection($"Data Source={legacyDbPath}"))
                {
                    conn.Open();

                    using SqliteCommand createOld = conn.CreateCommand();
                    createOld.CommandText =
                        "CREATE TABLE IF NOT EXISTS AgentPrefs(PrincipalID TEXT PRIMARY KEY, AccessPrefs TEXT);" +
                        "CREATE TABLE IF NOT EXISTS usersettings(useruuid TEXT PRIMARY KEY, visible INTEGER);";
                    createOld.ExecuteNonQuery();
                }

                LegacySchemaNormalizer.Normalize("SQLite", $"Data Source={legacyDbPath}");

                using SqliteConnection verify = new SqliteConnection($"Data Source={legacyDbPath}");
                verify.Open();

                Assert.That(SqliteObjectExists(verify, "table", "AgentPreferences"), Is.True);
                Assert.That(SqliteObjectExists(verify, "view", "AgentPrefs"), Is.True);

                // SQLite identifiers are case-insensitive. For usersettings/UserSettings this means
                // both names refer to the same table object and no additional compatibility view is needed.
                Assert.That(SqliteObjectExists(verify, "table", "UserSettings"), Is.True);
                Assert.That(SqliteObjectExists(verify, "table", "usersettings"), Is.True);
            }
            finally
            {
                TryDeleteTempFile(legacyDbPath);
            }
        }

        private static void SeedLegacyAsset(string legacyDbPath, UUID assetId, byte[] data, string name)
        {
            using SqliteConnection conn = new SqliteConnection($"Data Source={legacyDbPath}");
            conn.Open();

            using SqliteCommand create = conn.CreateCommand();
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

            using SqliteCommand upsert = conn.CreateCommand();
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
            upsert.Parameters.Add("$data", SqliteType.Blob).Value = data;
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
                // Best-effort cleanup for provider file handles on Windows test runs.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for provider file handles on Windows test runs.
            }
        }

        private static bool SqliteObjectExists(SqliteConnection conn, string type, string name)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type=$type AND lower(name)=lower($name) LIMIT 1";
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$name", name);
            return cmd.ExecuteScalar() != null;
        }
    }
}