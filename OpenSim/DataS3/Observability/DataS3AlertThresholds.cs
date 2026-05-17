namespace OpenSim.DataS3.Observability
{
    public sealed class DataS3AlertThresholds
    {
        public double MaxErrorRate { get; init; } = 0.05;

        public double MaxUpload429Rate { get; init; } = 0.10;

        public double MinObjectStoreAvailability { get; init; } = 0.99;

        public int MaxFallbackReadsPerInterval { get; init; } = 50;

        public int MaxUploadFailuresPerInterval { get; init; } = 20;
    }
}