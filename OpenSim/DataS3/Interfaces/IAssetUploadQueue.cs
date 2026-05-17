using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.Interfaces
{
    /// <summary>
    /// Dispatches upload persistence work, optionally via background workers.
    /// </summary>
    public interface IAssetUploadQueue : IDisposable
    {
        Task<bool> EnqueueAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken);
    }
}
