namespace OpenSim.DataS3.Observability
{
    /// <summary>
    /// Point-in-time operational metrics for DataS3 data-plane behavior.
    /// </summary>
    public sealed class DataS3OperationalMetricsSnapshot
    {
        public long UploadSuccessCount { get; init; }

        public long UploadRateLimitedCount { get; init; }

        public long UploadFailureCount { get; init; }

        public long ReadSuccessCount { get; init; }

        public long ReadFailureCount { get; init; }

        public long DeleteSuccessCount { get; init; }

        public long DeleteFailureCount { get; init; }

        public long UploadedBytes { get; init; }

        public double PutLatencyP95Ms { get; init; }

        public double PutLatencyP99Ms { get; init; }

        public double GetLatencyP95Ms { get; init; }

        public double GetLatencyP99Ms { get; init; }

        public long ObjectStoreCalls { get; init; }

        public long ObjectStoreFailures { get; init; }

        public double Upload429Rate { get; init; }

        public double ErrorRate { get; init; }

        public double ObjectStoreAvailability { get; init; }
    }
}