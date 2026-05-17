using System;

namespace OpenSim.DataS3.Resilience
{
    public sealed class DataS3CircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly TimeSpan _openDuration;
        private readonly object _sync = new object();
        private int _consecutiveFailures;
        private DateTimeOffset? _openUntilUtc;

        public DataS3CircuitBreaker(int failureThreshold, TimeSpan openDuration)
        {
            _failureThreshold = failureThreshold <= 0 ? 5 : failureThreshold;
            _openDuration = openDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : openDuration;
        }

        public bool IsOpen
        {
            get
            {
                lock (_sync)
                {
                    if (!_openUntilUtc.HasValue)
                        return false;

                    if (_openUntilUtc.Value <= DateTimeOffset.UtcNow)
                    {
                        _openUntilUtc = null;
                        _consecutiveFailures = 0;
                        return false;
                    }

                    return true;
                }
            }
        }

        public void ThrowIfOpen()
        {
            if (IsOpen)
                throw new InvalidOperationException("DataS3 backpressure active: object store circuit breaker is open.");
        }

        public void RecordSuccess()
        {
            lock (_sync)
            {
                _consecutiveFailures = 0;
                _openUntilUtc = null;
            }
        }

        public void RecordFailure()
        {
            lock (_sync)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= _failureThreshold)
                    _openUntilUtc = DateTimeOffset.UtcNow + _openDuration;
            }
        }
    }
}
