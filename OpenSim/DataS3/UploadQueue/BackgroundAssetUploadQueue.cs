using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.UploadQueue
{
    /// <summary>
    /// Background upload queue with bounded pending items and configurable worker count.
    /// </summary>
    public sealed class BackgroundAssetUploadQueue : IAssetUploadQueue
    {
        private readonly ConcurrentQueue<QueueItem> _queue = new ConcurrentQueue<QueueItem>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _pendingSlots;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task[] _workers;

        /// <summary>
        /// Creates a background queue instance.
        /// </summary>
        public BackgroundAssetUploadQueue(int workerCount, int maxPending)
        {
            if (workerCount <= 0)
                workerCount = 1;
            if (maxPending <= 0)
                maxPending = 1024;

            _pendingSlots = new SemaphoreSlim(maxPending, maxPending);
            _workers = new Task[workerCount];
            for (int i = 0; i < _workers.Length; i++)
                _workers[i] = Task.Run(WorkerLoopAsync);
        }

        /// <inheritdoc />
        public async Task<bool> EnqueueAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            cancellationToken.ThrowIfCancellationRequested();
            await _pendingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new QueueItem(work, completion));
            _signal.Release();

            return await completion.Task.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cts.Cancel();

            for (int i = 0; i < _workers.Length; i++)
                _signal.Release();

            try
            {
                Task.WaitAll(_workers, TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Swallow shutdown errors during dispose.
            }

            while (_queue.TryDequeue(out QueueItem? item))
            {
                item.Completion.TrySetCanceled();
                _pendingSlots.Release();
            }

            _signal.Dispose();
            _pendingSlots.Dispose();
            _cts.Dispose();
        }

        private async Task WorkerLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_queue.TryDequeue(out QueueItem? item))
                    continue;

                try
                {
                    bool result = await item.Work(_cts.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    _pendingSlots.Release();
                }
            }
        }

        private sealed class QueueItem
        {
            public QueueItem(Func<CancellationToken, Task<bool>> work, TaskCompletionSource<bool> completion)
            {
                Work = work;
                Completion = completion;
            }

            public Func<CancellationToken, Task<bool>> Work { get; }

            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}
