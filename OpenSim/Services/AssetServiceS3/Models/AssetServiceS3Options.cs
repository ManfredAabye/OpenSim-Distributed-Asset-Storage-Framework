namespace OpenSim.Services.AssetServiceS3.Models
{
    /// <summary>
    /// Runtime options for AssetServiceS3 bootstrapping.
    /// </summary>
    public sealed class AssetServiceS3Options
    {
        /// <summary>
        /// Object store mode used by DataS3 provider (for example InMemory or MinIO).
        /// </summary>
        public string ObjectStore { get; init; } = "InMemory";

        /// <summary>
        /// Original provider connection string from configuration.
        /// </summary>
        public string ConnectionString { get; init; } = string.Empty;

        /// <summary>
        /// Converts options to a provider initialization string.
        /// </summary>
        /// <returns>Normalized connection string with ObjectStore prefix.</returns>
        public string ToProviderConnectionString()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return $"ObjectStore={ObjectStore}";

            return $"ObjectStore={ObjectStore};{ConnectionString}";
        }
    }
}
