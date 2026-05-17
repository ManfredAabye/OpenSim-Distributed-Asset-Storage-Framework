using System;
using Nini.Config;
using OpenSim.DataS3;
using OpenSim.DataS3.Compatibility;
using OpenSim.DataS3.Providers;
using OpenSim.Services.AssetServiceS3.Models;
using OpenSim.Services.AssetServiceS3.Utils;
using OpenSim.Services.Base;

namespace OpenSim.Services.AssetServiceS3
{
    /// <summary>
    /// Shared setup and provider bootstrap for <see cref="AssetServiceS3"/>.
    /// </summary>
    public class AssetServiceS3Base : ServiceBase
    {
        protected IAssetDataPlugin m_Database;

        /// <summary>
        /// Initializes the DataS3-backed provider used by the service.
        /// </summary>
        /// <param name="config">Config source.</param>
        /// <param name="configName">Config section name.</param>
        public AssetServiceS3Base(IConfigSource config, string configName) : base(config)
        {
            AssetServiceS3Options options = AssetServiceS3ConfigReader.Read(config, configName);

            if (options.ObjectStore.Equals("MinIO", StringComparison.OrdinalIgnoreCase))
                DataS3MinioBootstrap.MaybeStartMinio(config);

            // First working step: direct internal provider wiring.
            m_Database = new HybridAssetData();
            m_Database.Initialise(options.ToProviderConnectionString());

            if (m_Database == null)
                throw new Exception("Failed to initialize DataS3 asset provider");
        }
    }
}
