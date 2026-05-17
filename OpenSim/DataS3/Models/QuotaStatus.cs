namespace OpenSim.DataS3.Models
{
    public sealed class QuotaStatus
    {
        public bool Allowed { get; init; }

        public string? DenyReason { get; init; }

        public int RetryAfterSeconds { get; init; }

        public long MaxUploadPerDayBytes { get; init; }

        public long RemainingUploadPerDayBytes { get; init; }

        public int MaxConcurrentUploads { get; init; }

        public int CurrentConcurrentUploads { get; init; }

        public long MaxBytesPerSecond { get; init; }
    }
}
