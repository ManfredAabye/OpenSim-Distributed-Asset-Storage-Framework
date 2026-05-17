using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OpenSim.DataS3.Observability
{
    /// <summary>
    /// Thread-safe collector for core operational metrics used by DataS3.
    /// </summary>
    public sealed class DataS3MetricsCollector
    {
        private readonly object _sync = new object();
        private readonly Queue<long> _putLatenciesMs = new Queue<long>();
        private readonly Queue<long> _getLatenciesMs = new Queue<long>();
        private readonly int _maxLatencySamples;

        private long _uploadSuccessCount;
        private long _uploadRateLimitedCount;
        private long _uploadFailureCount;
        private long _readSuccessCount;
        private long _readFailureCount;
        private long _deleteSuccessCount;
        private long _deleteFailureCount;
        private long _uploadedBytes;
        private long _objectStoreCalls;
        private long _objectStoreFailures;

        public DataS3MetricsCollector(int maxLatencySamples = 4096)
        {
            _maxLatencySamples = maxLatencySamples <= 0 ? 4096 : maxLatencySamples;
        }

        public void RecordUploadSuccess(long bytes, long latencyMs)
        {
            Interlocked.Increment(ref _uploadSuccessCount);
            Interlocked.Add(ref _uploadedBytes, Math.Max(0, bytes));
            AddLatencySample(_putLatenciesMs, latencyMs);
        }

        public void RecordUploadRateLimited(long latencyMs)
        {
            Interlocked.Increment(ref _uploadRateLimitedCount);
            AddLatencySample(_putLatenciesMs, latencyMs);
        }

        public void RecordUploadFailure(long latencyMs)
        {
            Interlocked.Increment(ref _uploadFailureCount);
            AddLatencySample(_putLatenciesMs, latencyMs);
        }

        public void RecordReadSuccess(long latencyMs)
        {
            Interlocked.Increment(ref _readSuccessCount);
            AddLatencySample(_getLatenciesMs, latencyMs);
        }

        public void RecordReadFailure(long latencyMs)
        {
            Interlocked.Increment(ref _readFailureCount);
            AddLatencySample(_getLatenciesMs, latencyMs);
        }

        public void RecordDeleteSuccess()
        {
            Interlocked.Increment(ref _deleteSuccessCount);
        }

        public void RecordDeleteFailure()
        {
            Interlocked.Increment(ref _deleteFailureCount);
        }

        public void RecordObjectStoreCall(bool success)
        {
            Interlocked.Increment(ref _objectStoreCalls);
            if (!success)
                Interlocked.Increment(ref _objectStoreFailures);
        }

        public DataS3OperationalMetricsSnapshot GetSnapshot()
        {
            long uploadSuccess = Interlocked.Read(ref _uploadSuccessCount);
            long uploadRateLimited = Interlocked.Read(ref _uploadRateLimitedCount);
            long uploadFailure = Interlocked.Read(ref _uploadFailureCount);
            long readSuccess = Interlocked.Read(ref _readSuccessCount);
            long readFailure = Interlocked.Read(ref _readFailureCount);
            long deleteSuccess = Interlocked.Read(ref _deleteSuccessCount);
            long deleteFailure = Interlocked.Read(ref _deleteFailureCount);
            long uploadedBytes = Interlocked.Read(ref _uploadedBytes);
            long objectStoreCalls = Interlocked.Read(ref _objectStoreCalls);
            long objectStoreFailures = Interlocked.Read(ref _objectStoreFailures);

            long[] putLatencies;
            long[] getLatencies;
            lock (_sync)
            {
                putLatencies = _putLatenciesMs.ToArray();
                getLatencies = _getLatenciesMs.ToArray();
            }

            long uploadAttempts = uploadSuccess + uploadRateLimited + uploadFailure;
            long operationTotal = uploadSuccess + uploadFailure + readSuccess + readFailure + deleteSuccess + deleteFailure;
            long operationFailures = uploadFailure + readFailure + deleteFailure;

            return new DataS3OperationalMetricsSnapshot
            {
                UploadSuccessCount = uploadSuccess,
                UploadRateLimitedCount = uploadRateLimited,
                UploadFailureCount = uploadFailure,
                ReadSuccessCount = readSuccess,
                ReadFailureCount = readFailure,
                DeleteSuccessCount = deleteSuccess,
                DeleteFailureCount = deleteFailure,
                UploadedBytes = uploadedBytes,
                PutLatencyP95Ms = ComputePercentile(putLatencies, 95),
                PutLatencyP99Ms = ComputePercentile(putLatencies, 99),
                GetLatencyP95Ms = ComputePercentile(getLatencies, 95),
                GetLatencyP99Ms = ComputePercentile(getLatencies, 99),
                ObjectStoreCalls = objectStoreCalls,
                ObjectStoreFailures = objectStoreFailures,
                Upload429Rate = uploadAttempts <= 0 ? 0d : (double)uploadRateLimited / uploadAttempts,
                ErrorRate = operationTotal <= 0 ? 0d : (double)operationFailures / operationTotal,
                ObjectStoreAvailability = objectStoreCalls <= 0 ? 1d : 1d - ((double)objectStoreFailures / objectStoreCalls)
            };
        }

        private void AddLatencySample(Queue<long> queue, long latencyMs)
        {
            long sample = Math.Max(0, latencyMs);

            lock (_sync)
            {
                queue.Enqueue(sample);
                while (queue.Count > _maxLatencySamples)
                    queue.Dequeue();
            }
        }

        private static double ComputePercentile(long[] values, int percentile)
        {
            if (values.Length == 0)
                return 0d;

            long[] sorted = values.OrderBy(v => v).ToArray();
            int rank = (int)Math.Ceiling((percentile / 100d) * sorted.Length);
            int index = Math.Min(Math.Max(rank - 1, 0), sorted.Length - 1);
            return sorted[index];
        }
    }
}