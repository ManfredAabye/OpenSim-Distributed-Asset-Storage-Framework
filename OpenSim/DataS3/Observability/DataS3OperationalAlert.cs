namespace OpenSim.DataS3.Observability
{
    public sealed class DataS3OperationalAlert
    {
        public string Code { get; init; } = string.Empty;

        public DataS3AlertSeverity Severity { get; init; }

        public string Message { get; init; } = string.Empty;
    }
}