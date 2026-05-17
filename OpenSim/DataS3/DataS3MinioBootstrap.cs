using System;
using Nini.Config;
using OpenSim.DataS3.Config;
using OpenSim.DataS3.Helpers;

namespace OpenSim.DataS3
{
    public static class DataS3MinioBootstrap
    {
        private static MinioLauncher? _launcher;

        public static void MaybeStartMinio(IConfigSource config)
        {
            var cfg = DataS3Config.LoadFromConfigSource(config);
            if (!cfg.MinioAutoStart)
                return;

            if (_launcher != null)
                return;

            _launcher = new MinioLauncher(cfg.MinioBinaryPath, cfg.MinioDataPath, cfg.MinioPort);
            _launcher.Start();
        }

        public static void Shutdown()
        {
            _launcher?.Dispose();
            _launcher = null;
        }
    }
}
