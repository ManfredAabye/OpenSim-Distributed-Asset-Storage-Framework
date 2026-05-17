using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenSim.DataS3.Compatibility;
using OpenSim.DataS3.Caching.Memory;
using OpenSim.DataS3.Caching.Noop;
using OpenSim.DataS3.Metadata.Memory;
using OpenSim.DataS3.Metadata.SQLite;
using OpenSim.DataS3.Models;
using OpenSim.DataS3.ObjectStores.HybridBlob;
using OpenSim.DataS3.ObjectStores.MinIO;
using OpenSim.DataS3.ObjectStores.Memory;
using OpenSim.DataS3.Observability;
using OpenSim.DataS3.Resilience;
using OpenSim.DataS3.RateControl;
using OpenSim.DataS3.Security;
using OpenSim.DataS3.UploadQueue;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Integrity;
using OpenSim.Framework;

namespace OpenSim.DataS3.Providers
{
    /// <summary>
    /// DataS3 plugin entry point implementing the legacy-compatible asset data contract.
    /// </summary>
    public sealed class HybridAssetData : AssetDataBase
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(HybridAssetData));

        private bool _isInitialized;
        private HybridAssetDataProvider? _provider;
        private IUploadRateLimiter? _rateLimiter;
        private LegacyAssetFallbackReader? _legacyFallbackReader;
        private bool _forceLegacyReadEnabled;
        private bool _fallbackReadEnabled;
        private bool _readThroughMigrationEnabled;
        private bool _dualWriteEnabled;
        private bool _cutoverMode;
        private bool _directMigrationEnabled;
        private int _directMigrationBatchSize;
        private int _directMigrationMaxAssets;
        private BackgroundAssetIntegrityVerifier? _integrityVerifier;
        private BackgroundDataS3AlertMonitor? _alertMonitor;
        private long _fallbackReadsSinceLastAlert;
        private long _fallbackReadsTotal;
        private bool _integrityScanEnabled;
        private bool _integrityRepairEnabled;
        private int _lastDirectMigrationScanned;
        private int _lastDirectMigrationMigrated;
        private DateTimeOffset? _lastDirectMigrationCompletedAtUtc;
        private bool _authRequired;
        private bool _adminOnlyQuotaManagement;
        private long _roleUserMaxUploadBytes;
        private long _roleEstateOwnerMaxUploadBytes;
        private long _roleAdminMaxUploadBytes;
        private bool _ipRateLimitEnabled;
        private bool _denyMissingIpWhenIpLimitEnabled;
        private IpAccessController? _ipAccessController;

        /// <inheritdoc />
        public override string Version => "0.2.0.0";

        /// <inheritdoc />
        public override string Name => "Hybrid Asset storage engine (DataS3)";

        /// <inheritdoc />
        public override void Initialise(string connect)
        {
            string objectStore = ReadConnectionValue(connect, "ObjectStore") ?? "InMemory";
            string cacheProvider = ReadConnectionValue(connect, "CacheProvider") ?? "None";
            string metadataProvider = ReadConnectionValue(connect, "MetadataProvider") ?? "InMemory";
            string metadataConnectionString = ReadConnectionValue(connect, "MetadataConnectionString") ?? string.Empty;
            string legacyProvider = ReadConnectionValue(connect, "LegacyAssetProvider") ?? string.Empty;
            string legacyConnectionString = ReadConnectionValue(connect, "LegacyAssetConnectionString") ?? string.Empty;
            bool normalizeLegacyTableNames = ReadBoolConnectionValue(connect, "NormalizeLegacyTableNames", false);

            // MinIO bootstrap is disabled here because a global config accessor
            // is not available in all OpenSim framework variants.

            _forceLegacyReadEnabled = ReadBoolConnectionValue(connect, "ForceLegacyReadEnabled", false);
            _fallbackReadEnabled = ReadBoolConnectionValue(connect, "FallbackReadEnabled", false);
            _readThroughMigrationEnabled = ReadBoolConnectionValue(connect, "ReadThroughMigrationEnabled", false);
            _dualWriteEnabled = ReadBoolConnectionValue(connect, "DualWriteEnabled", false);
            _cutoverMode = ReadBoolConnectionValue(connect, "CutoverMode", false);
            _directMigrationEnabled = ReadBoolConnectionValue(connect, "DirectMigrationEnabled", false);
            _directMigrationBatchSize = ReadIntConnectionValue(connect, "DirectMigrationBatchSize", 500);
            _directMigrationMaxAssets = ReadIntConnectionValue(connect, "DirectMigrationMaxAssets", 0);

            if (_cutoverMode)
            {
                _forceLegacyReadEnabled = false;
                _fallbackReadEnabled = false;
                _readThroughMigrationEnabled = false;
                _dualWriteEnabled = false;
                _directMigrationEnabled = false;
            }

            if (_forceLegacyReadEnabled)
            {
                m_log.Warn("[DATAS3]: ForceLegacyReadEnabled is active. Reads are forced to the legacy asset store until the flag is cleared.");
            }

            m_log.InfoFormat(
                "[DATAS3]: Migration mode flags: ForceLegacyReadEnabled={0}, FallbackReadEnabled={1}, ReadThroughMigrationEnabled={2}, DualWriteEnabled={3}, DirectMigrationEnabled={4}, CutoverMode={5}",
                _forceLegacyReadEnabled,
                _fallbackReadEnabled,
                _readThroughMigrationEnabled,
                _dualWriteEnabled,
                _directMigrationEnabled,
                _cutoverMode);

            if (_directMigrationBatchSize <= 0)
                _directMigrationBatchSize = 500;
            if (_directMigrationMaxAssets < 0)
                _directMigrationMaxAssets = 0;

            bool rateLimitEnabled = ReadBoolConnectionValue(connect, "RateLimitEnabled", false);
            long maxUploadPerDay = ReadLongConnectionValue(connect, "MaxUploadPerDay", 1073741824L);
            int maxConcurrentUploads = ReadIntConnectionValue(connect, "MaxConcurrentUploads", 5);
            long maxBytesPerSecond = ReadLongConnectionValue(connect, "MaxBytesPerSecond", 10485760L);
            int quotaCheckInterval = ReadIntConnectionValue(connect, "QuotaCheckInterval", 86400);
            int cacheEntryTtlSeconds = ReadIntConnectionValue(connect, "CacheEntryTtlSeconds", 300);
            bool uploadQueueEnabled = ReadBoolConnectionValue(connect, "UploadQueueEnabled", false);
            int uploadQueueWorkers = ReadIntConnectionValue(connect, "UploadQueueWorkers", 2);
            int uploadQueueMaxPending = ReadIntConnectionValue(connect, "UploadQueueMaxPending", 1024);
            bool integrityScanEnabled = ReadBoolConnectionValue(connect, "IntegrityScanEnabled", false);
            int integrityScanIntervalSeconds = ReadIntConnectionValue(connect, "IntegrityScanIntervalSeconds", 1800);
            int integrityScanPageSize = ReadIntConnectionValue(connect, "IntegrityScanPageSize", 500);
            bool integrityRepairEnabled = ReadBoolConnectionValue(connect, "IntegrityRepairEnabled", false);
            bool alertingEnabled = ReadBoolConnectionValue(connect, "AlertingEnabled", false);
            int alertingIntervalSeconds = ReadIntConnectionValue(connect, "AlertingIntervalSeconds", 60);
            double alertMaxErrorRate = ReadDoubleConnectionValue(connect, "AlertMaxErrorRate", 0.05d);
            double alertMaxUpload429Rate = ReadDoubleConnectionValue(connect, "AlertMaxUpload429Rate", 0.10d);
            double alertMinObjectStoreAvailability = ReadDoubleConnectionValue(connect, "AlertMinObjectStoreAvailability", 0.99d);
            int alertMaxFallbackReadsPerInterval = ReadIntConnectionValue(connect, "AlertMaxFallbackReadsPerInterval", 50);
            int alertMaxUploadFailuresPerInterval = ReadIntConnectionValue(connect, "AlertMaxUploadFailuresPerInterval", 20);
            int globalMaxConcurrentUploads = ReadIntConnectionValue(connect, "GlobalMaxConcurrentUploads", 0);
            long globalMaxBytesPerSecond = ReadLongConnectionValue(connect, "GlobalMaxBytesPerSecond", 0);
            bool circuitBreakerEnabled = ReadBoolConnectionValue(connect, "CircuitBreakerEnabled", false);
            int circuitBreakerFailureThreshold = ReadIntConnectionValue(connect, "CircuitBreakerFailureThreshold", 5);
            int circuitBreakerOpenSeconds = ReadIntConnectionValue(connect, "CircuitBreakerOpenSeconds", 30);
            _authRequired = ReadBoolConnectionValue(connect, "AuthRequired", false);
            _adminOnlyQuotaManagement = ReadBoolConnectionValue(connect, "AdminOnlyQuotaManagement", false);
            _roleUserMaxUploadBytes = ReadLongConnectionValue(connect, "RoleUserMaxUploadBytes", 268435456L);
            _roleEstateOwnerMaxUploadBytes = ReadLongConnectionValue(connect, "RoleEstateOwnerMaxUploadBytes", 1073741824L);
            _roleAdminMaxUploadBytes = ReadLongConnectionValue(connect, "RoleAdminMaxUploadBytes", 0L);
            _ipRateLimitEnabled = ReadBoolConnectionValue(connect, "IpRateLimitEnabled", false);
            _denyMissingIpWhenIpLimitEnabled = ReadBoolConnectionValue(connect, "DenyMissingIpWhenIpLimitEnabled", true);
            int ipMaxRequestsPerMinute = ReadIntConnectionValue(connect, "IpMaxRequestsPerMinute", 120);
            int ipBanSeconds = ReadIntConnectionValue(connect, "IpBanSeconds", 120);

            IObjectStore objectStoreInstance;
            if (objectStore.Equals("MinIO", StringComparison.OrdinalIgnoreCase))
            {
                objectStoreInstance = new MinioObjectStore(connect);
            }
            else if (objectStore.Equals("HybridBlob", StringComparison.OrdinalIgnoreCase))
            {
                objectStoreInstance = new HybridBlobObjectStore(connect);
            }
            else
            {
                objectStoreInstance = new InMemoryObjectStore();
            }

            IAssetMetadataStore metadataStore = metadataProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase)
                ? new SQLiteAssetMetadataStore(metadataConnectionString)
                : new InMemoryAssetMetadataStore();

            IAssetReadCache readCache = cacheProvider.Equals("Memory", StringComparison.OrdinalIgnoreCase)
                ? new InMemoryAssetReadCache(TimeSpan.FromSeconds(cacheEntryTtlSeconds <= 0 ? 300 : cacheEntryTtlSeconds))
                : new NoopAssetReadCache();

            IUploadRateLimiter rateLimiter = rateLimitEnabled
                ? new UploadRateLimiter(maxUploadPerDay, maxConcurrentUploads, maxBytesPerSecond, quotaCheckInterval)
                : new NoopUploadRateLimiter();

            IAssetUploadQueue uploadQueue = uploadQueueEnabled
                ? new BackgroundAssetUploadQueue(uploadQueueWorkers, uploadQueueMaxPending)
                : new InlineAssetUploadQueue();

            GlobalUploadLimiter? globalUploadLimiter = (globalMaxConcurrentUploads > 0 || globalMaxBytesPerSecond > 0)
                ? new GlobalUploadLimiter(globalMaxConcurrentUploads, globalMaxBytesPerSecond)
                : null;

            DataS3CircuitBreaker? circuitBreaker = circuitBreakerEnabled
                ? new DataS3CircuitBreaker(circuitBreakerFailureThreshold, TimeSpan.FromSeconds(circuitBreakerOpenSeconds <= 0 ? 30 : circuitBreakerOpenSeconds))
                : null;

            _ipAccessController = _ipRateLimitEnabled
                ? new IpAccessController(ipMaxRequestsPerMinute, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(ipBanSeconds <= 0 ? 120 : ipBanSeconds))
                : null;

            _rateLimiter = rateLimiter;

            _provider = new HybridAssetDataProvider(
                objectStoreInstance,
                metadataStore,
                rateLimiter,
                readCache,
                uploadQueue,
                globalUploadLimiter,
                circuitBreaker);

            _integrityScanEnabled = integrityScanEnabled;
            _integrityRepairEnabled = integrityRepairEnabled;

            _legacyFallbackReader = new LegacyAssetFallbackReader(legacyProvider, legacyConnectionString);

            if (normalizeLegacyTableNames)
            {
                try
                {
                    LegacySchemaNormalizer.Normalize(legacyProvider, legacyConnectionString);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[DATAS3]: Legacy schema normalization failed: {0}", e.Message);
                }
            }

            if (_directMigrationEnabled)
                RunDirectMigration();

            _isInitialized = true;

            if (integrityScanEnabled)
            {
                _integrityVerifier = new BackgroundAssetIntegrityVerifier(
                    RequireProvider(),
                    integrityScanPageSize,
                    TimeSpan.FromSeconds(integrityScanIntervalSeconds <= 0 ? 1800 : integrityScanIntervalSeconds),
                    integrityRepairEnabled);
                _integrityVerifier.Start();

                m_log.InfoFormat(
                    "[DATAS3]: Integrity scan enabled (interval={0}s, pageSize={1}, repair={2}).",
                    integrityScanIntervalSeconds <= 0 ? 1800 : integrityScanIntervalSeconds,
                    integrityScanPageSize,
                    integrityRepairEnabled);
            }

            if (alertingEnabled)
            {
                DataS3AlertThresholds thresholds = new DataS3AlertThresholds
                {
                    MaxErrorRate = alertMaxErrorRate,
                    MaxUpload429Rate = alertMaxUpload429Rate,
                    MinObjectStoreAvailability = alertMinObjectStoreAvailability,
                    MaxFallbackReadsPerInterval = alertMaxFallbackReadsPerInterval,
                    MaxUploadFailuresPerInterval = alertMaxUploadFailuresPerInterval
                };

                _alertMonitor = new BackgroundDataS3AlertMonitor(
                    RequireProvider(),
                    () => Interlocked.Exchange(ref _fallbackReadsSinceLastAlert, 0),
                    thresholds,
                    TimeSpan.FromSeconds(alertingIntervalSeconds <= 0 ? 60 : alertingIntervalSeconds));
                _alertMonitor.Start();

                m_log.InfoFormat(
                    "[DATAS3]: Alerting enabled (interval={0}s, maxErrorRate={1}, max429Rate={2}, minAvailability={3}, maxFallbackReads={4}, maxUploadFailures={5}).",
                    alertingIntervalSeconds <= 0 ? 60 : alertingIntervalSeconds,
                    alertMaxErrorRate,
                    alertMaxUpload429Rate,
                    alertMinObjectStoreAvailability,
                    alertMaxFallbackReadsPerInterval,
                    alertMaxUploadFailuresPerInterval);
            }
        }

        /// <inheritdoc />
        public override void Initialise()
        {
            Initialise(string.Empty);
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            _integrityVerifier?.Dispose();
            _integrityVerifier = null;
            _alertMonitor?.Dispose();
            _alertMonitor = null;
            _provider?.Dispose();
            _isInitialized = false;
            _provider = null;
            _rateLimiter = null;
            _legacyFallbackReader = null;
            _fallbackReadsSinceLastAlert = 0;
            _fallbackReadsTotal = 0;
            _ipAccessController = null;
        }

        public DataS3DashboardSnapshot GetDashboardSnapshot()
        {
            EnsureInitialized();

            HybridAssetDataProvider provider = RequireProvider();
            DataS3OperationalMetricsSnapshot operational = provider.GetOperationalMetricsSnapshot();
            AssetIntegrityScanReport? integrityReport = _integrityVerifier?.LastReport;

            return new DataS3DashboardSnapshot
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Operational = operational,
                Migration = new DataS3MigrationStatusSnapshot
                {
                    CutoverMode = _cutoverMode,
                    ForceLegacyReadEnabled = _forceLegacyReadEnabled,
                    FallbackReadEnabled = _fallbackReadEnabled,
                    ReadThroughMigrationEnabled = _readThroughMigrationEnabled,
                    DualWriteEnabled = _dualWriteEnabled,
                    DirectMigrationEnabled = _directMigrationEnabled,
                    FallbackReadTotal = Interlocked.Read(ref _fallbackReadsTotal),
                    LastDirectMigrationScanned = _lastDirectMigrationScanned,
                    LastDirectMigrationMigrated = _lastDirectMigrationMigrated,
                    LastDirectMigrationCompletedAtUtc = _lastDirectMigrationCompletedAtUtc
                },
                Integrity = new DataS3IntegrityStatusSnapshot
                {
                    IntegrityScanEnabled = _integrityScanEnabled,
                    IntegrityRepairEnabled = _integrityRepairEnabled,
                    LastScanTotal = integrityReport?.TotalMetadataRows,
                    LastScanUnresolvedFailures = integrityReport?.UnresolvedFailures,
                    LastScanSucceeded = integrityReport?.Succeeded,
                    LastScanCompletedAtUtc = integrityReport?.CompletedAtUtc
                }
            };
        }

        public string GetDashboardJson()
        {
            return DataS3DashboardSerializer.ToJson(GetDashboardSnapshot());
        }

        /// <inheritdoc />
        public override AssetBase GetAsset(UUID uuid)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: false, requestedUploadBytes: null);
            HybridAssetDataProvider provider = RequireProvider();

            if (_forceLegacyReadEnabled)
                return ReadLegacyAsset(uuid, allowReadThroughMigration: false);

            var result = provider.GetAsync(uuid, CancellationToken.None).GetAwaiter().GetResult();
            if (result == null)
            {
                if (!_fallbackReadEnabled)
                    return null!;

                return ReadLegacyAsset(uuid, allowReadThroughMigration: true);
            }

            AssetMetadataRecord metadata = result.Value.Metadata;
            using (Stream stream = result.Value.Data)
            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                AssetBase asset = new AssetBase(metadata.AssetId, metadata.Name, (sbyte)metadata.AssetType, metadata.CreatorId)
                {
                    Description = metadata.Description,
                    Data = copy.ToArray(),
                    Flags = (AssetFlags)metadata.Flags
                };

                return asset;
            }
        }

        private AssetBase ReadLegacyAsset(UUID uuid, bool allowReadThroughMigration)
        {
            if (_legacyFallbackReader == null)
                return null!;

            AssetBase? legacyAsset = _legacyFallbackReader.TryGetAsset(uuid, CancellationToken.None);
            if (legacyAsset == null)
                return null!;

            Interlocked.Increment(ref _fallbackReadsSinceLastAlert);
            Interlocked.Increment(ref _fallbackReadsTotal);

            if (allowReadThroughMigration && _readThroughMigrationEnabled)
            {
                try
                {
                    StoreAssetInternal(legacyAsset, allowDualWrite: false);
                }
                catch
                {
                    // Keep legacy-read behavior robust even if migration writeback fails.
                }
            }

            return legacyAsset;
        }

        /// <inheritdoc />
        public override bool StoreAsset(AssetBase asset)
        {
            return StoreAssetInternal(asset, allowDualWrite: true);
        }

        private bool StoreAssetInternal(AssetBase asset, bool allowDualWrite)
        {
            EnsureInitialized();
            HybridAssetDataProvider provider = RequireProvider();

            if (asset == null || asset.Data == null)
                return false;

            UUID callerUserId = ResolveCallerUserId(asset);
            EnsureAccessAllowed(requireAdmin: false, requestedUploadBytes: asset.Data.LongLength);

            using (MemoryStream payload = new MemoryStream(asset.Data, writable: false))
            {
                bool stored = provider.PutAsync(
                    asset.FullID,
                    callerUserId,
                    asset.Type,
                    payload,
                    asset.Data.LongLength,
                    asset.Name,
                    asset.Description,
                    asset.CreatorID,
                    (int)asset.Flags,
                    CancellationToken.None).GetAwaiter().GetResult();

                if (stored && allowDualWrite && _dualWriteEnabled && _legacyFallbackReader != null)
                {
                    try
                    {
                        _legacyFallbackReader.UpsertAsset(asset, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[DATAS3]: Dual write to legacy asset store failed for {0}: {1}", asset.FullID, e.Message);
                    }
                }

                return stored;
            }
        }

        /// <inheritdoc />
        public override bool[] AssetsExist(UUID[] uuids)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: false, requestedUploadBytes: null);
            HybridAssetDataProvider provider = RequireProvider();

            bool[] result = new bool[uuids.Length];
            for (int i = 0; i < uuids.Length; i++)
                result[i] = provider.ExistsAsync(uuids[i], CancellationToken.None).GetAwaiter().GetResult();

            return result;
        }

        /// <inheritdoc />
        public override List<AssetMetadata> FetchAssetMetadataSet(int start, int count)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: false, requestedUploadBytes: null);
            HybridAssetDataProvider provider = RequireProvider();

            var rows = provider.ListMetadataAsync(start, count, CancellationToken.None).GetAwaiter().GetResult();
            List<AssetMetadata> result = new List<AssetMetadata>(rows.Count);

            foreach (AssetMetadataRecord row in rows)
            {
                AssetMetadata metadata = new AssetMetadata
                {
                    FullID = row.AssetId,
                    ID = row.AssetId.ToString(),
                    Name = row.Name,
                    Description = row.Description,
                    Type = (sbyte)row.AssetType,
                    CreatorID = row.CreatorId,
                    ContentType = row.ContentType,
                    SHA1 = null,
                    Flags = (AssetFlags)row.Flags
                };

                result.Add(metadata);
            }

            return result;
        }

        /// <inheritdoc />
        public override bool Delete(string id)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: false, requestedUploadBytes: null);
            HybridAssetDataProvider provider = RequireProvider();

            if (!UUID.TryParse(id, out UUID uuid))
                return false;

            bool deleted = provider.DeleteAsync(uuid, CancellationToken.None).GetAwaiter().GetResult();

            if (deleted && _dualWriteEnabled && _legacyFallbackReader != null)
            {
                try
                {
                    _legacyFallbackReader.DeleteAsset(uuid, CancellationToken.None);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[DATAS3]: Dual delete in legacy asset store failed for {0}: {1}", uuid, e.Message);
                }
            }

            return deleted;
        }

        public bool ResetQuota(UUID userId)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: true, requestedUploadBytes: null);
            RequireRateLimiter().ResetQuotaAsync(userId, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        public bool AddQuotaBytes(UUID userId, long additionalBytes)
        {
            EnsureInitialized();
            EnsureAccessAllowed(requireAdmin: true, requestedUploadBytes: null);

            if (additionalBytes <= 0)
                return false;

            RequireRateLimiter().AddQuotaBytesAsync(userId, additionalBytes, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        private UUID ResolveCallerUserId(AssetBase asset)
        {
            DataS3RequestContext? context = DataS3RequestContext.Current;
            if (context != null && context.UserId != UUID.Zero)
                return context.UserId;

            if (asset != null && UUID.TryParse(asset.CreatorID, out UUID creatorId) && creatorId != UUID.Zero)
                return creatorId;

            return UUID.Zero;
        }

        private void EnsureAccessAllowed(bool requireAdmin, long? requestedUploadBytes)
        {
            DataS3RequestContext? context = DataS3RequestContext.Current;

            if (_authRequired)
            {
                bool authenticated = context != null && context.IsAuthenticated && context.UserId != UUID.Zero;
                if (!authenticated)
                    throw new UnauthorizedAccessException("Authenticated DataS3 session required.");
            }

            if (_adminOnlyQuotaManagement && requireAdmin)
            {
                if (context == null || !context.HasRole(DataS3Role.Admin))
                    throw new UnauthorizedAccessException("Admin role required for quota management.");
            }

            if (_ipRateLimitEnabled && _ipAccessController != null)
            {
                string? ip = context?.RemoteIp;
                if (string.IsNullOrWhiteSpace(ip))
                {
                    if (_denyMissingIpWhenIpLimitEnabled)
                        throw new UnauthorizedAccessException("Remote IP is required while IP-based limits are enabled.");
                }
                else
                {
                    bool allowed = _ipAccessController.TryAcquire(ip, DateTimeOffset.UtcNow, out int retryAfterSeconds);
                    if (!allowed)
                        throw new InvalidOperationException($"IP rate limit exceeded or temporarily banned (retryAfter={retryAfterSeconds}s).");
                }
            }

            if (requestedUploadBytes.HasValue && requestedUploadBytes.Value > 0)
            {
                long roleLimit = GetEffectiveRoleUploadLimit(context);
                if (roleLimit > 0 && requestedUploadBytes.Value > roleLimit)
                {
                    throw new InvalidOperationException(
                        $"Upload size {requestedUploadBytes.Value} bytes exceeds role upload limit {roleLimit} bytes.");
                }
            }
        }

        private long GetEffectiveRoleUploadLimit(DataS3RequestContext? context)
        {
            if (context != null && context.HasRole(DataS3Role.Admin))
                return _roleAdminMaxUploadBytes;

            if (context != null && context.HasRole(DataS3Role.EstateOwner))
                return _roleEstateOwnerMaxUploadBytes;

            return _roleUserMaxUploadBytes;
        }

        /// <summary>
        /// Ensures initialization happened before any provider operation is attempted.
        /// </summary>
        private void EnsureInitialized()
        {
            if (!_isInitialized || _provider == null || _rateLimiter == null)
                throw new InvalidOperationException("HybridAssetData has not been initialized.");
        }

        private HybridAssetDataProvider RequireProvider()
        {
            return _provider ?? throw new InvalidOperationException("HybridAssetData provider is not available.");
        }

        private IUploadRateLimiter RequireRateLimiter()
        {
            return _rateLimiter ?? throw new InvalidOperationException("HybridAssetData rate limiter is not available.");
        }

        private void RunDirectMigration()
        {
            if (_legacyFallbackReader == null)
                return;

            HybridAssetDataProvider provider = RequireProvider();

            int migrated = 0;
            int scanned = 0;
            int offset = 0;

            while (true)
            {
                if (_directMigrationMaxAssets > 0 && scanned >= _directMigrationMaxAssets)
                    break;

                int batchSize = _directMigrationBatchSize;
                if (_directMigrationMaxAssets > 0)
                    batchSize = Math.Min(batchSize, _directMigrationMaxAssets - scanned);

                IReadOnlyList<AssetBase> page = _legacyFallbackReader.GetAssetBatch(offset, batchSize, CancellationToken.None);
                if (page.Count == 0)
                    break;

                foreach (AssetBase asset in page)
                {
                    scanned++;

                    try
                    {
                        bool exists = provider.ExistsAsync(asset.FullID, CancellationToken.None).GetAwaiter().GetResult();
                        if (exists)
                            continue;

                        if (StoreAssetForDirectMigration(asset, provider))
                            migrated++;
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[DATAS3]: Direct migration failed for asset {0}: {1}", asset.FullID, e.Message);
                    }
                }

                offset += page.Count;
            }

            m_log.InfoFormat("[DATAS3]: Direct migration completed. Scanned={0}, Migrated={1}", scanned, migrated);

            _lastDirectMigrationScanned = scanned;
            _lastDirectMigrationMigrated = migrated;
            _lastDirectMigrationCompletedAtUtc = DateTimeOffset.UtcNow;
        }

        private static bool StoreAssetForDirectMigration(AssetBase asset, HybridAssetDataProvider provider)
        {
            if (asset == null || asset.Data == null)
                return false;

            using MemoryStream payload = new MemoryStream(asset.Data, writable: false);
            return provider.PutAsync(
                asset.FullID,
                UUID.Zero,
                asset.Type,
                payload,
                asset.Data.LongLength,
                asset.Name,
                asset.Description,
                asset.CreatorID,
                (int)asset.Flags,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string? ReadConnectionValue(string connection, string key)
        {
            if (string.IsNullOrWhiteSpace(connection))
                return null;

            string[] tokens = connection.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens.Select(t => t.Trim()))
            {
                int idx = token.IndexOf('=');
                if (idx <= 0)
                    continue;

                string currentKey = token.Substring(0, idx).Trim();
                if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return token.Substring(idx + 1).Trim();
            }

            return null;
        }

        private static bool ReadBoolConnectionValue(string connection, string key, bool defaultValue)
        {
            string? raw = ReadConnectionValue(connection, key);
            return bool.TryParse(raw, out bool value) ? value : defaultValue;
        }

        private static int ReadIntConnectionValue(string connection, string key, int defaultValue)
        {
            string? raw = ReadConnectionValue(connection, key);
            return int.TryParse(raw, out int value) ? value : defaultValue;
        }

        private static long ReadLongConnectionValue(string connection, string key, long defaultValue)
        {
            string? raw = ReadConnectionValue(connection, key);
            return long.TryParse(raw, out long value) ? value : defaultValue;
        }

        private static double ReadDoubleConnectionValue(string connection, string key, double defaultValue)
        {
            string? raw = ReadConnectionValue(connection, key);
            return double.TryParse(raw, out double value) ? value : defaultValue;
        }
    }
}
