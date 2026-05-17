using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.Migrations;
using OpenSim.DataS3.ObjectStores.MinIO;
using OpenSim.DataS3.Providers;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class DataS3SmokeTests : OpenSimTestCase
    {
        [Test]
        [Category("Smoke")]
        public void MigrationRunnerStartsAndAppliesEmbeddedMigration()
        {
            using SqliteConnection conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();

            MigrationRunner runner = new MigrationRunner(conn, Assembly.GetExecutingAssembly(), "smoke");
            runner.Update();

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM smoke_migration";
            object? scalar = cmd.ExecuteScalar();
            long count = Convert.ToInt64(scalar ?? 0L);

            Assert.That(count, Is.EqualTo(1L));
            Assert.That(runner.Version, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        [Category("Smoke")]
        public void MigrationRunnerUsesDataS3EmbeddedMigrationsReproducibly()
        {
            using SqliteConnection conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();

            MigrationRunner runner = new MigrationRunner(conn, typeof(HybridAssetData).Assembly, "datas3schema");
            runner.Update();
            int versionAfterFirstRun = runner.Version;
            runner.Update();
            int versionAfterSecondRun = runner.Version;

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM datas3_schema_probe";
            long count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);

            Assert.That(versionAfterFirstRun, Is.EqualTo(2));
            Assert.That(versionAfterSecondRun, Is.EqualTo(2));
            Assert.That(count, Is.EqualTo(1L));
        }

        [Test]
        [Category("Smoke")]
        public void MinioObjectStorePutGetDeleteAgainstRunningMinio()
        {
            string enabled = Environment.GetEnvironmentVariable("DATAS3_SMOKE_MINIO_ENABLED") ?? "false";
            if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("MinIO smoke disabled. Set DATAS3_SMOKE_MINIO_ENABLED=true to run this test.");

            string endpoint = Environment.GetEnvironmentVariable("DATAS3_MINIO_ENDPOINT") ?? "http://127.0.0.1:9000";
            string bucket = Environment.GetEnvironmentVariable("DATAS3_MINIO_BUCKET") ?? "assets-smoke";
            string accessKey = Environment.GetEnvironmentVariable("DATAS3_MINIO_ACCESS_KEY") ?? "minioadmin";
            string secretKey = Environment.GetEnvironmentVariable("DATAS3_MINIO_SECRET_KEY") ?? "minioadmin";

            string connection =
                $"MinioEndpoint={endpoint};MinioBucket={bucket};MinioAccessKey={accessKey};MinioSecretKey={secretKey};MinioRegion=us-east-1;MinioAutoCreateBucket=true";

            MinioObjectStore store = new MinioObjectStore(connection);
            string key = $"smoke/{Guid.NewGuid():N}";
            byte[] payload = { 2, 4, 6, 8 };

            using MemoryStream putStream = new MemoryStream(payload, writable: false);
            store.PutAsync(key, putStream, null, CancellationToken.None).GetAwaiter().GetResult();

            bool exists = store.ExistsAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(exists, Is.True);

            using Stream getStream = store.GetAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            using MemoryStream copy = new MemoryStream();
            getStream.CopyTo(copy);
            Assert.That(copy.ToArray().SequenceEqual(payload), Is.True);

            store.DeleteAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            bool existsAfterDelete = store.ExistsAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(existsAfterDelete, Is.False);
        }

        [Test]
        [Category("Smoke")]
        public void RestoreFromMetadataBackupKeepsObjectStoreReadable()
        {
            string enabled = Environment.GetEnvironmentVariable("DATAS3_SMOKE_MINIO_ENABLED") ?? "false";
            if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("MinIO smoke disabled. Set DATAS3_SMOKE_MINIO_ENABLED=true to run this test.");

            string endpoint = Environment.GetEnvironmentVariable("DATAS3_MINIO_ENDPOINT") ?? "http://127.0.0.1:9000";
            string bucket = Environment.GetEnvironmentVariable("DATAS3_MINIO_BUCKET") ?? "assets-smoke";
            string accessKey = Environment.GetEnvironmentVariable("DATAS3_MINIO_ACCESS_KEY") ?? "minioadmin";
            string secretKey = Environment.GetEnvironmentVariable("DATAS3_MINIO_SECRET_KEY") ?? "minioadmin";

            string dbOriginal = Path.Combine(Path.GetTempPath(), $"datas3-metadata-{Guid.NewGuid():N}.db");
            string dbBackup = Path.Combine(Path.GetTempPath(), $"datas3-metadata-backup-{Guid.NewGuid():N}.db");
            UUID assetId = UUID.Random();
            byte[] payload = { 11, 22, 33, 44, 55 };

            try
            {
                string minioConnection =
                    $"MinioEndpoint={endpoint};MinioBucket={bucket};MinioAccessKey={accessKey};MinioSecretKey={secretKey};MinioRegion=us-east-1;MinioAutoCreateBucket=true";

                string connectOriginal =
                    "ObjectStore=MinIO;MetadataProvider=SQLite;RateLimitEnabled=false;" +
                    $"MetadataConnectionString=Data Source={dbOriginal};" +
                    minioConnection;

                HybridAssetData writer = new HybridAssetData();
                writer.Initialise(connectOriginal);
                try
                {
                    AssetBase asset = new AssetBase(assetId, "restore-smoke", (sbyte)0, UUID.Zero.ToString())
                    {
                        Description = "backup-restore-smoke",
                        Data = payload,
                        Flags = AssetFlags.Normal
                    };

                    Assert.That(writer.StoreAsset(asset), Is.True);
                }
                finally
                {
                    writer.Dispose();
                }

                File.Copy(dbOriginal, dbBackup, overwrite: true);

                string connectRestored =
                    "ObjectStore=MinIO;MetadataProvider=SQLite;RateLimitEnabled=false;" +
                    $"MetadataConnectionString=Data Source={dbBackup};" +
                    minioConnection;

                HybridAssetData reader = new HybridAssetData();
                reader.Initialise(connectRestored);
                try
                {
                    AssetBase? restored = reader.GetAsset(assetId);
                    Assert.That(restored, Is.Not.Null);
                    Assert.That(restored!.Data.SequenceEqual(payload), Is.True);

                    Assert.That(reader.Delete(assetId.ToString()), Is.True);
                }
                finally
                {
                    reader.Dispose();
                }
            }
            finally
            {
                TryDeleteFile(dbOriginal);
                TryDeleteFile(dbBackup);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup for Windows file handles.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for Windows file handles.
            }
        }
    }
}