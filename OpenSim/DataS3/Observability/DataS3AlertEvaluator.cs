using System;
using System.Collections.Generic;

namespace OpenSim.DataS3.Observability
{
    public sealed class DataS3AlertEvaluator
    {
        public IReadOnlyList<DataS3OperationalAlert> Evaluate(
            DataS3OperationalMetricsSnapshot current,
            DataS3OperationalMetricsSnapshot? previous,
            long fallbackReadsSinceLastWindow,
            DataS3AlertThresholds thresholds)
        {
            if (thresholds == null)
                throw new ArgumentNullException(nameof(thresholds));

            List<DataS3OperationalAlert> alerts = new List<DataS3OperationalAlert>();

            if (current.ErrorRate > thresholds.MaxErrorRate)
            {
                alerts.Add(new DataS3OperationalAlert
                {
                    Code = "SLO_ERROR_RATE",
                    Severity = DataS3AlertSeverity.Critical,
                    Message = $"Error rate {current.ErrorRate:P2} exceeds threshold {thresholds.MaxErrorRate:P2}."
                });
            }

            if (current.Upload429Rate > thresholds.MaxUpload429Rate)
            {
                alerts.Add(new DataS3OperationalAlert
                {
                    Code = "UPLOAD_429_RATE",
                    Severity = DataS3AlertSeverity.Warning,
                    Message = $"Upload 429 rate {current.Upload429Rate:P2} exceeds threshold {thresholds.MaxUpload429Rate:P2}."
                });
            }

            if (current.ObjectStoreAvailability < thresholds.MinObjectStoreAvailability)
            {
                alerts.Add(new DataS3OperationalAlert
                {
                    Code = "OBJECTSTORE_AVAILABILITY",
                    Severity = DataS3AlertSeverity.Critical,
                    Message = $"Object store availability {current.ObjectStoreAvailability:P2} is below threshold {thresholds.MinObjectStoreAvailability:P2}."
                });
            }

            if (fallbackReadsSinceLastWindow > thresholds.MaxFallbackReadsPerInterval)
            {
                alerts.Add(new DataS3OperationalAlert
                {
                    Code = "FALLBACK_READ_SPIKE",
                    Severity = DataS3AlertSeverity.Warning,
                    Message = $"Fallback reads in window ({fallbackReadsSinceLastWindow}) exceed threshold {thresholds.MaxFallbackReadsPerInterval}."
                });
            }

            if (previous != null)
            {
                long uploadFailureDelta = Math.Max(0, current.UploadFailureCount - previous.UploadFailureCount);
                if (uploadFailureDelta > thresholds.MaxUploadFailuresPerInterval)
                {
                    alerts.Add(new DataS3OperationalAlert
                    {
                        Code = "QUEUE_STALL_OR_BACKPRESSURE",
                        Severity = DataS3AlertSeverity.Warning,
                        Message = $"Upload failures in window ({uploadFailureDelta}) exceed threshold {thresholds.MaxUploadFailuresPerInterval}."
                    });
                }
            }

            return alerts;
        }
    }
}