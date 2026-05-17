using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.RateControl
{
    public sealed class GlobalUploadLimiter
    {
        private readonly SemaphoreSlim _concurrency;
        private readonly long _maxBytesPerSecond;
        private readonly object _sync = new object();
        private long _windowBytes;
        private DateTime _windowStartUtc;
        private readonly bool _enabled;

        public GlobalUploadLimiter(int maxConcurrentUploads, long maxBytesPerSecond)
        {
            if (maxConcurrentUploads <= 0 && maxBytesPerSecond <= 0)
            {
                _enabled = false;
                _concurrency = new SemaphoreSlim(1, 1);
                _maxBytesPerSecond = 0;
                _windowStartUtc = DateTime.UtcNow;
                return;
            }

            _enabled = true;
            _concurrency = new SemaphoreSlim(maxConcurrentUploads <= 0 ? 1 : maxConcurrentUploads, maxConcurrentUploads <= 0 ? 1 : maxConcurrentUploads);
            _maxBytesPerSecond = Math.Max(0, maxBytesPerSecond);
            _windowStartUtc = DateTime.UtcNow;
        }

        public async Task<GlobalUploadLease?> TryAcquireAsync(long bytes, CancellationToken cancellationToken)
        {
            if (!_enabled)
                return new GlobalUploadLease(null);

            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

            bool accepted = true;
            if (_maxBytesPerSecond > 0)
            {
                lock (_sync)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - _windowStartUtc).TotalSeconds >= 1)
                    {
                        _windowStartUtc = now;
                        _windowBytes = 0;
                    }

                    long next = _windowBytes + Math.Max(0, bytes);
                    if (next > _maxBytesPerSecond)
                    {
                        accepted = false;
                    }
                    else
                    {
                        _windowBytes = next;
                    }
                }
            }

            if (!accepted)
            {
                _concurrency.Release();
                return null;
            }

            return new GlobalUploadLease(_concurrency);
        }

        public sealed class GlobalUploadLease : IDisposable
        {
            private SemaphoreSlim? _gate;

            internal GlobalUploadLease(SemaphoreSlim? gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
                gate?.Release();
            }
        }
    }
}
