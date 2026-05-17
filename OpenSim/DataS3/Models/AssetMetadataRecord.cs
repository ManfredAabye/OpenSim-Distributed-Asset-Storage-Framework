using OpenMetaverse;

namespace OpenSim.DataS3.Models
{
    public sealed class AssetMetadataRecord
    {
        public UUID AssetId { get; init; }

        public string ContentHash { get; init; } = string.Empty;

        public int AssetType { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string CreatorId { get; init; } = string.Empty;

        public int Flags { get; init; }

        public string ContentType { get; init; } = "application/octet-stream";

        public long SizeBytes { get; init; }

        public string StorageProvider { get; init; } = string.Empty;

        public string StorageBucket { get; init; } = string.Empty;

        public string StorageKey { get; init; } = string.Empty;

        public string? Compression { get; init; }

        public string? Checksum { get; init; }
    }
}
