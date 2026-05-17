using System;

namespace OpenSim.DataS3.Integrity
{
    /// <summary>
    /// Summary report for one integrity scan pass.
    /// </summary>
    public sealed class AssetIntegrityScanReport
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset CompletedAtUtc { get; init; }

        public int TotalMetadataRows { get; init; }

        public int VerifiedOk { get; init; }

        public int MissingOrUnreadableObjects { get; init; }

        public int ChecksumMismatches { get; init; }

        public int OtherFailures { get; init; }

        public int RepairAttempts { get; init; }

        public int RepairsSucceeded { get; init; }

        public int ReindexedChecksums { get; init; }

        public int MarkedInconsistentEntries { get; init; }

        public bool Cancelled { get; init; }

        public int UnresolvedFailures =>
            Math.Max(0, MissingOrUnreadableObjects + ChecksumMismatches + OtherFailures - RepairsSucceeded);

        public bool Succeeded => !Cancelled && UnresolvedFailures == 0;
    }
}
