using System.Linq;
using NUnit.Framework;
using OpenSim.DataS3.Observability;
using OpenSim.Tests.Common;

namespace OpenSim.DataS3.Tests
{
    [TestFixture]
    public class DataS3AlertEvaluatorTests : OpenSimTestCase
    {
        [Test]
        public void TestEvaluatorRaisesSloAndAvailabilityAlerts()
        {
            DataS3AlertEvaluator evaluator = new DataS3AlertEvaluator();
            DataS3AlertThresholds thresholds = new DataS3AlertThresholds
            {
                MaxErrorRate = 0.05,
                MaxUpload429Rate = 0.10,
                MinObjectStoreAvailability = 0.99,
                MaxFallbackReadsPerInterval = 50,
                MaxUploadFailuresPerInterval = 20
            };

            DataS3OperationalMetricsSnapshot current = new DataS3OperationalMetricsSnapshot
            {
                ErrorRate = 0.15,
                Upload429Rate = 0.25,
                ObjectStoreAvailability = 0.90,
                UploadFailureCount = 2
            };

            var alerts = evaluator.Evaluate(current, null, fallbackReadsSinceLastWindow: 0, thresholds);

            Assert.That(alerts.Any(a => a.Code == "SLO_ERROR_RATE"), Is.True);
            Assert.That(alerts.Any(a => a.Code == "UPLOAD_429_RATE"), Is.True);
            Assert.That(alerts.Any(a => a.Code == "OBJECTSTORE_AVAILABILITY"), Is.True);
        }

        [Test]
        public void TestEvaluatorRaisesFallbackAndQueueAlertsOnWindowSpike()
        {
            DataS3AlertEvaluator evaluator = new DataS3AlertEvaluator();
            DataS3AlertThresholds thresholds = new DataS3AlertThresholds
            {
                MaxErrorRate = 0.50,
                MaxUpload429Rate = 0.50,
                MinObjectStoreAvailability = 0.50,
                MaxFallbackReadsPerInterval = 5,
                MaxUploadFailuresPerInterval = 2
            };

            DataS3OperationalMetricsSnapshot previous = new DataS3OperationalMetricsSnapshot
            {
                UploadFailureCount = 1,
                ErrorRate = 0.01,
                Upload429Rate = 0.01,
                ObjectStoreAvailability = 1.0
            };

            DataS3OperationalMetricsSnapshot current = new DataS3OperationalMetricsSnapshot
            {
                UploadFailureCount = 10,
                ErrorRate = 0.02,
                Upload429Rate = 0.02,
                ObjectStoreAvailability = 1.0
            };

            var alerts = evaluator.Evaluate(current, previous, fallbackReadsSinceLastWindow: 12, thresholds);

            Assert.That(alerts.Any(a => a.Code == "FALLBACK_READ_SPIKE"), Is.True);
            Assert.That(alerts.Any(a => a.Code == "QUEUE_STALL_OR_BACKPRESSURE"), Is.True);
        }

        [Test]
        public void TestEvaluatorReturnsNoAlertsWithinThresholds()
        {
            DataS3AlertEvaluator evaluator = new DataS3AlertEvaluator();
            DataS3AlertThresholds thresholds = new DataS3AlertThresholds
            {
                MaxErrorRate = 0.20,
                MaxUpload429Rate = 0.20,
                MinObjectStoreAvailability = 0.90,
                MaxFallbackReadsPerInterval = 50,
                MaxUploadFailuresPerInterval = 20
            };

            DataS3OperationalMetricsSnapshot previous = new DataS3OperationalMetricsSnapshot
            {
                UploadFailureCount = 10,
                ErrorRate = 0.05,
                Upload429Rate = 0.05,
                ObjectStoreAvailability = 0.95
            };

            DataS3OperationalMetricsSnapshot current = new DataS3OperationalMetricsSnapshot
            {
                UploadFailureCount = 12,
                ErrorRate = 0.08,
                Upload429Rate = 0.07,
                ObjectStoreAvailability = 0.94
            };

            var alerts = evaluator.Evaluate(current, previous, fallbackReadsSinceLastWindow: 3, thresholds);

            Assert.That(alerts.Count, Is.EqualTo(0));
        }
    }
}
