using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenSim.DataS3.ObjectStores.MinIO;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class DataS3MinioIntegrationTest
    {
        [Test]
        public async Task MinioObjectStore_PutGetDelete()
        {
            string connection = "MinioEndpoint=http://127.0.0.1:9000;MinioBucket=assets-test;MinioAccessKey=minioadmin;MinioSecretKey=minioadmin;MinioRegion=us-east-1;MinioAutoCreateBucket=true";
            var store = new MinioObjectStore(connection);
            string testKey = "test-object.txt";
            byte[] testData = System.Text.Encoding.UTF8.GetBytes("Hello DataS3!");
            using (var ms = new MemoryStream(testData))
            {
                await store.PutAsync(testKey, ms, null, CancellationToken.None);
            }
            Assert.IsTrue(await store.ExistsAsync(testKey, CancellationToken.None));
            using (var result = await store.GetAsync(testKey, CancellationToken.None))
            using (var reader = new StreamReader(result))
            {
                string content = await reader.ReadToEndAsync();
                Assert.AreEqual("Hello DataS3!", content);
            }
            await store.DeleteAsync(testKey, CancellationToken.None);
            Assert.IsFalse(await store.ExistsAsync(testKey, CancellationToken.None));
        }
    }
}
