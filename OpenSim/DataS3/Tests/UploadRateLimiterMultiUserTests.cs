using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.DataS3.RateControl;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class UploadRateLimiterMultiUserTests : OpenSimTestCase
    {
        [Test]
        public void TestUserQuotasAreIsolated()
        {
            UploadRateLimiter limiter = new UploadRateLimiter(
                maxUploadPerWindowBytes: 100,
                maxConcurrentUploads: 3,
                maxBytesPerSecond: 1024 * 1024,
                quotaWindowSeconds: 3600);

            UUID userA = TestHelpers.ParseTail(0xA1);
            UUID userB = TestHelpers.ParseTail(0xB1);

            Assert.That(limiter.CanUploadAsync(userA, 80, CancellationToken.None).GetAwaiter().GetResult(), Is.True);
            limiter.RecordUploadAsync(userA, 80, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(limiter.CanUploadAsync(userA, 30, CancellationToken.None).GetAwaiter().GetResult(), Is.False);
            Assert.That(limiter.CanUploadAsync(userB, 30, CancellationToken.None).GetAwaiter().GetResult(), Is.True);
        }

        [Test]
        public void TestConcurrentLimitIsPerUser()
        {
            UploadRateLimiter limiter = new UploadRateLimiter(
                maxUploadPerWindowBytes: 1024 * 1024,
                maxConcurrentUploads: 1,
                maxBytesPerSecond: 1024 * 1024,
                quotaWindowSeconds: 3600);

            UUID userA = TestHelpers.ParseTail(0xA2);
            UUID userB = TestHelpers.ParseTail(0xB2);

            Assert.That(limiter.CanUploadAsync(userA, 10, CancellationToken.None).GetAwaiter().GetResult(), Is.True);
            Assert.That(limiter.CanUploadAsync(userA, 10, CancellationToken.None).GetAwaiter().GetResult(), Is.False);
            Assert.That(limiter.CanUploadAsync(userB, 10, CancellationToken.None).GetAwaiter().GetResult(), Is.True);

            limiter.RecordUploadAsync(userA, 10, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(limiter.CanUploadAsync(userA, 10, CancellationToken.None).GetAwaiter().GetResult(), Is.True);
        }
    }
}
