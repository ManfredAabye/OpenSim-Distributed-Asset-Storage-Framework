using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenSim.DataS3.Common;
using OpenSim.DataS3.Models;
using OpenSim.DataS3.Providers;

namespace OpenSim.DataS3.Integrity
{
    /// <summary>
    /// Periodically verifies object-store payload integrity against metadata checksums.
    /// </summary>
    public sealed class BackgroundAssetIntegrityVerifier : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(BackgroundAssetIntegrityVerifier));

        private readonly HybridAssetDataProvider _provider;
        private readonly int _pageSize;
        private readonly TimeSpan _interval;
        private readonly bool _repairEnabled;

        private CancellationTokenSource? _cts;
        private Task? _worker;

        public BackgroundAssetIntegrityVerifier(HybridAssetDataProvider provider, int pageSize, TimeSpan interval, bool repairEnabled = false)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _pageSize = pageSize <= 0 ? 500 : pageSize;
            _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : interval;
            _repairEnabled = repairEnabled;
        }

        public AssetIntegrityScanReport? LastReport { get; private set; }

        public void Start()
        {
            if (_worker != null)
                return;

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        public async Task<AssetIntegrityScanReport> RunOnceAsync(CancellationToken cancellationToken)
        {
            return await RunInternalAsync(cancellationToken, applyRepairs: false).ConfigureAwait(false);
        }

        public async Task<AssetIntegrityScanReport> RunRepairOnceAsync(CancellationToken cancellationToken)
        {
            return await RunInternalAsync(cancellationToken, applyRepairs: true).ConfigureAwait(false);
        }

        private async Task<AssetIntegrityScanReport> RunInternalAsync(CancellationToken cancellationToken, bool applyRepairs)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;

            int total = 0;
            int ok = 0;
            int missing = 0;
            int checksumMismatches = 0;
            int otherFailures = 0;
            int repairAttempts = 0;
            int repairsSucceeded = 0;
            int reindexedChecksums = 0;
            int markedInconsistent = 0;
            int offset = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var rows = await _provider.ListMetadataAsync(offset, _pageSize, cancellationToken).ConfigureAwait(false);
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total++;

                        try
                        {
                            var result = await _provider.GetAsync(row.AssetId, cancellationToken).ConfigureAwait(false);
                            if (result == null)
                            {
                                missing++;

                                if (applyRepairs)
                                {
                                    repairAttempts++;
                                    if (await TryMarkInconsistentAsync(row, "metadata-without-object", cancellationToken).ConfigureAwait(false))
                                    {
                                        repairsSucceeded++;
                                        markedInconsistent++;
                                    }
                                }

                                continue;
                            }

                            result.Value.Data.Dispose();
                            ok++;
                        }
                        catch (InvalidDataException)
                        {
                            checksumMismatches++;

                            if (applyRepairs)
                            {
                                repairAttempts++;
                                if (await TryReindexChecksumAsync(row, cancellationToken).ConfigureAwait(false))
                                {
                                    repairsSucceeded++;
                                    reindexedChecksums++;
                                }
                            }
                        }
                        catch (KeyNotFoundException)
                        {
                            missing++;

                            if (applyRepairs)
                            {
                                repairAttempts++;
                                if (await TryMarkInconsistentAsync(row, "object-not-found", cancellationToken).ConfigureAwait(false))
                                {
                                    repairsSucceeded++;
                                    markedInconsistent++;
                                }
                            }
                        }
                        catch (FileNotFoundException)
                        {
                            missing++;

                            if (applyRepairs)
                            {
                                repairAttempts++;
                                if (await TryMarkInconsistentAsync(row, "object-not-found", cancellationToken).ConfigureAwait(false))
                                {
                                    repairsSucceeded++;
                                    markedInconsistent++;
                                }
                            }
                        }
                        catch
                        {
                            otherFailures++;

                            if (applyRepairs)
                            {
                                repairAttempts++;
                                if (await TryMarkInconsistentAsync(row, "read-failure", cancellationToken).ConfigureAwait(false))
                                {
                                    repairsSucceeded++;
                                    markedInconsistent++;
                                }
                            }
                        }
                    }

                    offset += rows.Count;
                }

                AssetIntegrityScanReport report = new AssetIntegrityScanReport
                {
                    StartedAtUtc = started,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    TotalMetadataRows = total,
                    VerifiedOk = ok,
                    MissingOrUnreadableObjects = missing,
                    ChecksumMismatches = checksumMismatches,
                    OtherFailures = otherFailures,
                    RepairAttempts = repairAttempts,
                    RepairsSucceeded = repairsSucceeded,
                    ReindexedChecksums = reindexedChecksums,
                    MarkedInconsistentEntries = markedInconsistent,
                    Cancelled = false
                };

                LastReport = report;
                return report;
            }
            catch (OperationCanceledException)
            {
                AssetIntegrityScanReport report = new AssetIntegrityScanReport
                {
                    StartedAtUtc = started,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    TotalMetadataRows = total,
                    VerifiedOk = ok,
                    MissingOrUnreadableObjects = missing,
                    ChecksumMismatches = checksumMismatches,
                    OtherFailures = otherFailures,
                    RepairAttempts = repairAttempts,
                    RepairsSucceeded = repairsSucceeded,
                    ReindexedChecksums = reindexedChecksums,
                    MarkedInconsistentEntries = markedInconsistent,
                    Cancelled = true
                };

                LastReport = report;
                return report;
            }
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AssetIntegrityScanReport report = _repairEnabled
                    ? await RunRepairOnceAsync(cancellationToken).ConfigureAwait(false)
                    : await RunOnceAsync(cancellationToken).ConfigureAwait(false);

                m_log.InfoFormat(
                    "[DATAS3]: Integrity scan completed: total={0}, ok={1}, missing={2}, checksum={3}, other={4}, repairs={5}/{6}, reindexed={7}, marked={8}, unresolved={9}, cancelled={10}",
                    report.TotalMetadataRows,
                    report.VerifiedOk,
                    report.MissingOrUnreadableObjects,
                    report.ChecksumMismatches,
                    report.OtherFailures,
                    report.RepairsSucceeded,
                    report.RepairAttempts,
                    report.ReindexedChecksums,
                    report.MarkedInconsistentEntries,
                    report.UnresolvedFailures,
                    report.Cancelled);

                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<bool> TryReindexChecksumAsync(AssetMetadataRecord row, CancellationToken cancellationToken)
        {
            try
            {
                var raw = await _provider.GetRawPayloadAsync(row.AssetId, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    return false;

                string hash = AssetObjectKeyBuilder.ComputeSha256Hex(raw.Value.Payload);
                await _provider.StoreMetadataAsync(CloneMetadata(raw.Value.Metadata, checksum: hash, contentHash: hash), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TryMarkInconsistentAsync(AssetMetadataRecord row, string reason, CancellationToken cancellationToken)
        {
            try
            {
                int flags = row.Flags | AssetIntegrityFlags.InconsistentEntry;
                string description = BuildMarkedDescription(row.Description, reason);
                await _provider.StoreMetadataAsync(CloneMetadata(row, flags: flags, description: description), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static AssetMetadataRecord CloneMetadata(
            AssetMetadataRecord source,
            int? flags = null,
            string? description = null,
            string? checksum = null,
            string? contentHash = null)
        {
            return new AssetMetadataRecord
            {
                AssetId = source.AssetId,
                ContentHash = contentHash ?? source.ContentHash,
                AssetType = source.AssetType,
                Name = source.Name,
                Description = description ?? source.Description,
                CreatorId = source.CreatorId,
                Flags = flags ?? source.Flags,
                ContentType = source.ContentType,
                SizeBytes = source.SizeBytes,
                StorageProvider = source.StorageProvider,
                StorageBucket = source.StorageBucket,
                StorageKey = source.StorageKey,
                Compression = source.Compression,
                Checksum = checksum ?? source.Checksum
            };
        }

        private static string BuildMarkedDescription(string description, string reason)
        {
            const string markerPrefix = "[DATAS3-INTEGRITY:";
            if (description.IndexOf(markerPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return description;

            string marker = $"[DATAS3-INTEGRITY:{reason}]";
            if (string.IsNullOrWhiteSpace(description))
                return marker;

            return description + " " + marker;
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
                // Best effort shutdown.
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _worker = null;
            }
        }
    }
}
