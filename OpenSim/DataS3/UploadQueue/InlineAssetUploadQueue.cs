using System;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.UploadQueue
{
    /// <summary>
    /// Executes upload persistence work inline in the caller context.
    /// </summary>
    public sealed class InlineAssetUploadQueue : IAssetUploadQueue
    {
        /// <inheritdoc />
        public Task<bool> EnqueueAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (work == null)
                throw new ArgumentNullException(nameof(work));

            return work(cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
