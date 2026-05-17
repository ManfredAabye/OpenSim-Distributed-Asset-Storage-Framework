using System;

namespace OpenSim.DataS3.Observability
{
    public sealed class DataS3DashboardSnapshot
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }

        public DataS3OperationalMetricsSnapshot Operational { get; init; } = new DataS3OperationalMetricsSnapshot();

        public DataS3MigrationStatusSnapshot Migration { get; init; } = new DataS3MigrationStatusSnapshot();

        public DataS3IntegrityStatusSnapshot Integrity { get; init; } = new DataS3IntegrityStatusSnapshot();
    }

    public sealed class DataS3MigrationStatusSnapshot
    {
        public bool CutoverMode { get; init; }

        public bool ForceLegacyReadEnabled { get; init; }

        public bool FallbackReadEnabled { get; init; }

        public bool ReadThroughMigrationEnabled { get; init; }

        public bool DualWriteEnabled { get; init; }

        public bool DirectMigrationEnabled { get; init; }

        public long FallbackReadTotal { get; init; }

        public int LastDirectMigrationScanned { get; init; }

        public int LastDirectMigrationMigrated { get; init; }

        public DateTimeOffset? LastDirectMigrationCompletedAtUtc { get; init; }
    }

    public sealed class DataS3IntegrityStatusSnapshot
    {
        public bool IntegrityScanEnabled { get; init; }

        public bool IntegrityRepairEnabled { get; init; }

        public int? LastScanTotal { get; init; }

        public int? LastScanUnresolvedFailures { get; init; }

        public bool? LastScanSucceeded { get; init; }

        public DateTimeOffset? LastScanCompletedAtUtc { get; init; }
    }
}