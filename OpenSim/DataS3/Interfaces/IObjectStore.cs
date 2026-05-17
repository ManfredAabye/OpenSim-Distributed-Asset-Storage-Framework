using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.Interfaces
{
    public interface IObjectStore
    {
        Task<Stream> GetAsync(string key, CancellationToken cancellationToken);

        Task PutAsync(
            string key,
            Stream data,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken);

        Task DeleteAsync(string key, CancellationToken cancellationToken);

        Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

        Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken);
    }

    public sealed class ObjectStat
    {
        public long SizeBytes { get; init; }

        public string? ETag { get; init; }

        public string? ContentType { get; init; }
    }
}
