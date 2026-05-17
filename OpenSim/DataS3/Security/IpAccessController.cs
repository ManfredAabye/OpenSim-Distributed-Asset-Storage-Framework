using System;
using System.Collections.Concurrent;

namespace OpenSim.DataS3.Security
{
    public sealed class IpAccessController
    {
        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxRequestsPerWindow;
        private readonly TimeSpan _window;
        private readonly TimeSpan _banDuration;

        public IpAccessController(int maxRequestsPerWindow, TimeSpan window, TimeSpan banDuration)
        {
            _maxRequestsPerWindow = maxRequestsPerWindow <= 0 ? 120 : maxRequestsPerWindow;
            _window = window <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : window;
            _banDuration = banDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : banDuration;
        }

        public bool TryAcquire(string ip, DateTimeOffset now, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            Entry entry = _entries.GetOrAdd(ip, _ => new Entry(now));

            lock (entry.Sync)
            {
                if (entry.BannedUntilUtc.HasValue && entry.BannedUntilUtc.Value > now)
                {
                    retryAfterSeconds = (int)Math.Ceiling((entry.BannedUntilUtc.Value - now).TotalSeconds);
                    return false;
                }

                if (entry.WindowStartUtc + _window <= now)
                {
                    entry.WindowStartUtc = now;
                    entry.RequestCount = 0;
                }

                entry.RequestCount++;
                if (entry.RequestCount > _maxRequestsPerWindow)
                {
                    entry.BannedUntilUtc = now + _banDuration;
                    retryAfterSeconds = (int)Math.Ceiling(_banDuration.TotalSeconds);
                    return false;
                }

                return true;
            }
        }

        private sealed class Entry
        {
            public Entry(DateTimeOffset now)
            {
                WindowStartUtc = now;
            }

            public object Sync { get; } = new object();

            public DateTimeOffset WindowStartUtc { get; set; }

            public int RequestCount { get; set; }

            public DateTimeOffset? BannedUntilUtc { get; set; }
        }
    }
}
