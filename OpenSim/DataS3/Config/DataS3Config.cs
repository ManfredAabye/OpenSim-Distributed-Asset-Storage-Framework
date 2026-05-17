using System;
using System.IO;
using Nini.Config;

namespace OpenSim.DataS3.Config
{
    public class DataS3Config
    {
        public bool MinioAutoStart { get; set; } = true;
        public string MinioBinaryPath { get; set; } = "bin/minio/minio.exe";
        public string MinioDataPath { get; set; } = "bin/minio/data";
        public int MinioPort { get; set; } = 9000;

        public static DataS3Config LoadFromConfigSource(IConfigSource config)
        {
            var cfg = config.Configs["AssetStorage"];
            var result = new DataS3Config();
            if (cfg != null)
            {
                result.MinioAutoStart = cfg.GetBoolean("MinioAutoStart", true);
                result.MinioBinaryPath = cfg.GetString("MinioBinaryPath", "bin/minio/minio.exe");
                result.MinioDataPath = cfg.GetString("MinioDataPath", "bin/minio/data");
                result.MinioPort = cfg.GetInt("MinioPort", 9000);
            }
            return result;
        }
    }
}
