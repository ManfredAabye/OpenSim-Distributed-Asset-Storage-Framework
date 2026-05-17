using System;
using System.IO;
using NUnit.Framework;
using OpenSim.DataS3.Helpers;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class MinioLauncherSmokeTest
    {
        [Test]
        public void MinioLauncher_StartsAndStopsMinio()
        {
            // Passe die Pfade ggf. an deine Testumgebung an!
            string binaryPath = "bin/minio/minio.exe";
            string dataPath = "bin/minio/testdata";
            int port = 9900;

            if (!File.Exists(binaryPath))
                Assert.Ignore($"MinIO-Binary nicht gefunden: {binaryPath}");

            using (var launcher = new MinioLauncher(binaryPath, dataPath, port))
            {
                Assert.DoesNotThrow(() => launcher.Start());
                Assert.IsTrue(Directory.Exists(dataPath));
            }
        }
    }
}
