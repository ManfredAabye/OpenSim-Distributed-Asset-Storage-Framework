using System;
using OpenMetaverse;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.AssetServiceS3
{
    /// <summary>
    /// Doxygen-documented S3-oriented asset service implementation.
    /// </summary>
    /// <remarks>
    /// This class implements <see cref="IAssetService"/> against the new DataS3 provider path.
    /// </remarks>
    public class AssetServiceS3 : AssetServiceS3Base, IAssetService
    {
        /// <summary>
        /// Creates the service using the default AssetService config section.
        /// </summary>
        /// <param name="config">Config source.</param>
        public AssetServiceS3(IConfigSource config) : this(config, "AssetService")
        {
        }

        /// <summary>
        /// Creates the service using a specific config section.
        /// </summary>
        /// <param name="config">Config source.</param>
        /// <param name="configName">Section name to read provider settings from.</param>
        public AssetServiceS3(IConfigSource config, string configName) : base(config, configName)
        {
        }

        /// <inheritdoc />
        public AssetBase Get(string id)
        {
            if (!UUID.TryParse(id, out UUID assetId))
                return null;

            return m_Database.GetAsset(assetId);
        }

        /// <inheritdoc />
        public AssetBase Get(string id, string foreignAssetService, bool storeOnLocalGrid)
        {
            return Get(id);
        }

        /// <inheritdoc />
        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = Get(id);
            return asset?.Metadata;
        }

        /// <inheritdoc />
        public byte[] GetData(string id)
        {
            AssetBase asset = Get(id);
            return asset?.Data;
        }

        /// <inheritdoc />
        public AssetBase GetCached(string id)
        {
            return Get(id);
        }

        /// <inheritdoc />
        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            if (!UUID.TryParse(id, out _))
                return false;

            handler(id, sender, Get(id));
            return true;
        }

        /// <inheritdoc />
        public void Get(string id, string foreignAssetService, bool storeOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            callBack(Get(id));
        }

        /// <inheritdoc />
        public bool[] AssetsExist(string[] ids)
        {
            UUID[] uuids = Array.ConvertAll(ids, id => UUID.TryParse(id, out UUID parsed) ? parsed : UUID.Zero);
            bool[] existence = m_Database.AssetsExist(uuids);

            for (int i = 0; i < uuids.Length; i++)
            {
                if (uuids[i] == UUID.Zero)
                    existence[i] = false;
            }

            return existence;
        }

        /// <inheritdoc />
        public string Store(AssetBase asset)
        {
            if (asset == null)
                return string.Empty;

            if (!UUID.TryParse(asset.ID, out UUID existing) || existing.IsZero())
            {
                UUID created = UUID.Random();
                asset.FullID = created;
                asset.ID = created.ToString();
            }

            bool ok = m_Database.StoreAsset(asset);
            return ok ? asset.ID : string.Empty;
        }

        /// <inheritdoc />
        public bool UpdateContent(string id, byte[] data)
        {
            AssetBase asset = Get(id);
            if (asset == null)
                return false;

            asset.Data = data;
            return m_Database.StoreAsset(asset);
        }

        /// <inheritdoc />
        public bool Delete(string id)
        {
            return m_Database.Delete(id);
        }
    }
}
