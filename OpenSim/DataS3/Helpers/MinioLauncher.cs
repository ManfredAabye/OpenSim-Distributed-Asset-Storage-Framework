using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenSim.DataS3.Helpers
{
    public class MinioLauncher : IDisposable
    {
        private Process? _minioProcess;
        private readonly string _binaryPath;
        private readonly string _dataPath;
        private readonly int _port;
        private bool _started;

        public MinioLauncher(string binaryPath, string dataPath, int port)
        {
            _binaryPath = binaryPath;
            _dataPath = dataPath;
            _port = port;
        }

        public void Start()
        {
            if (_started)
                return;

            if (!File.Exists(_binaryPath))
                throw new FileNotFoundException($"MinIO-Binary nicht gefunden: {_binaryPath}");

            Directory.CreateDirectory(_dataPath);

            var psi = new ProcessStartInfo
            {
                FileName = _binaryPath,
                Arguments = $"server \"{_dataPath}\" --address :{_port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _minioProcess = Process.Start(psi);
            if (_minioProcess == null || _minioProcess.HasExited)
                throw new Exception("MinIO-Prozess konnte nicht gestartet werden.");

            _started = true;
        }

        public void Dispose()
        {
            try
            {
                if (_minioProcess != null && !_minioProcess.HasExited)
                {
                    _minioProcess.Kill();
                    _minioProcess.Dispose();
                }
            }
            catch { }
        }
    }
}
