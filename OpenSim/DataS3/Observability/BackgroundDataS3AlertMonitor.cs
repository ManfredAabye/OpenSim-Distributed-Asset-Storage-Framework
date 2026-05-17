using System;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenSim.DataS3.Providers;

namespace OpenSim.DataS3.Observability
{
    public sealed class BackgroundDataS3AlertMonitor : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(BackgroundDataS3AlertMonitor));

        private readonly HybridAssetDataProvider _provider;
        private readonly Func<long> _consumeFallbackReadDelta;
        private readonly DataS3AlertThresholds _thresholds;
        private readonly DataS3AlertEvaluator _evaluator;
        private readonly TimeSpan _interval;

        private CancellationTokenSource? _cts;
        private Task? _worker;
        private DataS3OperationalMetricsSnapshot? _previous;

        public BackgroundDataS3AlertMonitor(
            HybridAssetDataProvider provider,
            Func<long> consumeFallbackReadDelta,
            DataS3AlertThresholds thresholds,
            TimeSpan interval)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _consumeFallbackReadDelta = consumeFallbackReadDelta ?? throw new ArgumentNullException(nameof(consumeFallbackReadDelta));
            _thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
            _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : interval;
            _evaluator = new DataS3AlertEvaluator();
        }

        public void Start()
        {
            if (_worker != null)
                return;

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            if (_cts == null)
                return;

            try
            {
                _cts.Cancel();
                _worker?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _worker = null;
                _previous = null;
            }
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EvaluateAndLogWindow();
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }

        private void EvaluateAndLogWindow()
        {
            DataS3OperationalMetricsSnapshot current = _provider.GetOperationalMetricsSnapshot();
            long fallbackDelta = Math.Max(0, _consumeFallbackReadDelta());

            var alerts = _evaluator.Evaluate(current, _previous, fallbackDelta, _thresholds);
            foreach (DataS3OperationalAlert alert in alerts)
            {
                string line = $"[DATAS3]: ALERT {alert.Code} ({alert.Severity}): {alert.Message}";
                if (alert.Severity == DataS3AlertSeverity.Critical)
                    m_log.Error(line);
                else
                    m_log.Warn(line);
            }

            _previous = current;
        }
    }
}