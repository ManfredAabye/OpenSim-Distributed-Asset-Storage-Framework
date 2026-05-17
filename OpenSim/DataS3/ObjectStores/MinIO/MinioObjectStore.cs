using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.ObjectStores.MinIO
{
    /// <summary>
    /// Runtime MinIO object store bridge.
    /// </summary>
    /// <remarks>
    /// This implementation intentionally avoids a hard compile-time dependency on Minio.* assemblies.
    /// It binds to minio-dotnet via reflection at runtime.
    /// </remarks>
    public sealed class MinioObjectStore : IObjectStore
    {
        private readonly string _endpoint;
        private readonly string _bucket;
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly string _region;
        private readonly bool _autoCreateBucket;

        private object? _client;
        private bool _bucketInitialized;
        private readonly object _sync = new object();

        /// <summary>
        /// Creates a new MinIO object store with values parsed from the provider connection string.
        /// </summary>
        /// <param name="connectionString">
        /// Expected keys: MinioEndpoint, MinioBucket, MinioAccessKey, MinioSecretKey, MinioRegion, MinioAutoCreateBucket.
        /// </param>
        public MinioObjectStore(string? connectionString = null)
        {
            _endpoint = ReadConnectionValue(connectionString, "MinioEndpoint") ?? "http://127.0.0.1:9000";
            _bucket = ReadConnectionValue(connectionString, "MinioBucket") ?? "assets";
            _accessKey = ReadConnectionValue(connectionString, "MinioAccessKey") ?? string.Empty;
            _secretKey = ReadConnectionValue(connectionString, "MinioSecretKey") ?? string.Empty;
            _region = ReadConnectionValue(connectionString, "MinioRegion") ?? "us-east-1";
            _autoCreateBucket = ReadBoolConnectionValue(connectionString, "MinioAutoCreateBucket", true);
        }

        /// <inheritdoc />
        public async Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
        {
            object client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

            object? taskObj = InvokeMethod(client, "GetObjectAsync", _bucket, key, null, cancellationToken);
            object objectInfoStream = await AwaitTaskResultAsync(taskObj).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MinIO GetObjectAsync returned null stream.");

            // Copy to a detached stream so callers are independent from SDK-owned stream lifetime.
            using (Stream source = objectInfoStream as Stream
                ?? throw new InvalidOperationException("MinIO GetObjectAsync did not return a Stream."))
            {
                MemoryStream copy = new MemoryStream();
                await source.CopyToAsync(copy, 81920, cancellationToken).ConfigureAwait(false);
                copy.Position = 0;
                return copy;
            }
        }

        /// <inheritdoc />
        public Task PutAsync(
            string key,
            Stream data,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            return PutCoreAsync(key, data, metadata, cancellationToken);
        }

        private async Task PutCoreAsync(
            string key,
            Stream data,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            object client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

            object? options = CreatePutObjectOptions(metadata);
            object? taskObj = InvokeMethod(client, "PutObjectAsync", _bucket, key, data, options, null, cancellationToken);
            await AwaitTaskOnlyAsync(taskObj).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            object client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

            object? taskObj = InvokeMethod(client, "DeleteObjectAsync", _bucket, key, null, false, null, null, cancellationToken);
            await AwaitTaskOnlyAsync(taskObj).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
        {
            object client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

            try
            {
                object? taskObj = InvokeMethod(client, "HeadObjectAsync", _bucket, key, null, cancellationToken);
                await AwaitTaskResultAsync(taskObj).ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                if (LooksLikeNotFound(e))
                    return false;

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
        {
            object client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

            object? taskObj = InvokeMethod(client, "HeadObjectAsync", _bucket, key, null, cancellationToken);
            object headResult = await AwaitTaskResultAsync(taskObj).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MinIO HeadObjectAsync returned null object info.");

            long size = ReadNullableLongProperty(headResult, "ContentLength") ?? 0L;
            string etag = ReadStringProperty(headResult, "Etag");
            string contentType = ReadStringProperty(headResult, "ContentType");

            return new ObjectStat
            {
                SizeBytes = size,
                ETag = etag,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            };
        }

        private Task<object> GetClientAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_client != null)
                return Task.FromResult(_client);

            lock (_sync)
            {
                if (_client != null)
                    return Task.FromResult(_client);

                _client = BuildClient();
                return Task.FromResult(_client);
            }
        }

        private async Task EnsureBucketAsync(object client, CancellationToken cancellationToken)
        {
            if (_bucketInitialized)
                return;

            object? existsTask = InvokeMethod(client, "BucketExistsAsync", _bucket, cancellationToken);
            bool exists = (bool)(await AwaitTaskResultAsync(existsTask).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MinIO BucketExistsAsync returned null."));

            if (!exists)
            {
                if (!_autoCreateBucket)
                    throw new InvalidOperationException($"Configured MinIO bucket '{_bucket}' does not exist.");

                object? createTask = InvokeMethod(client, "CreateBucketAsync", _bucket, false, _region, cancellationToken);
                await AwaitTaskResultAsync(createTask).ConfigureAwait(false);
            }

            _bucketInitialized = true;
        }

        private object BuildClient()
        {
            Type? builderType = ResolveMinioBuilderType();
            if (builderType == null)
            {
                string baseDir = AppContext.BaseDirectory;
                string asmDir = Path.GetDirectoryName(typeof(MinioObjectStore).Assembly.Location) ?? baseDir;
                throw new InvalidOperationException(
                    $"MinIO SDK assembly not found. Ensure Minio.dll is available. Probed: '{Path.Combine(baseDir, "Minio.dll")}' and '{Path.Combine(asmDir, "Minio.dll")}'.");
            }

            object builder = Activator.CreateInstance(builderType, _endpoint)
                ?? throw new InvalidOperationException("Unable to create MinioClientBuilder instance.");

            if (!string.IsNullOrWhiteSpace(_accessKey) && !string.IsNullOrWhiteSpace(_secretKey))
            {
                InvokeMethod(builder, "WithStaticCredentials", _accessKey, _secretKey, null);
            }
            else
            {
                InvokeMethod(builder, "WithEnvironmentCredentials");
            }

            if (!string.IsNullOrWhiteSpace(_region))
                InvokeMethod(builder, "WithRegion", _region);

            object? client = InvokeMethod(builder, "Build");
            return client ?? throw new InvalidOperationException("MinioClientBuilder.Build() returned null.");
        }

        private static Type? ResolveMinioBuilderType()
        {
            Type? builderType = Type.GetType("Minio.MinioClientBuilder, Minio", throwOnError: false);
            if (builderType != null)
                return builderType;

            string baseDir = AppContext.BaseDirectory;
            string asmDir = Path.GetDirectoryName(typeof(MinioObjectStore).Assembly.Location) ?? baseDir;

            string[] candidatePaths =
            {
                Path.Combine(baseDir, "Minio.dll"),
                Path.Combine(asmDir, "Minio.dll")
            };

            foreach (string candidate in candidatePaths)
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    Assembly minioAssembly = Assembly.LoadFrom(candidate);
                    builderType = minioAssembly.GetType("Minio.MinioClientBuilder", throwOnError: false);
                    if (builderType != null)
                        return builderType;
                }
                catch
                {
                    // The original failure path below will report a consistent diagnostic.
                }
            }

            return null;
        }

        private static object? CreatePutObjectOptions(IReadOnlyDictionary<string, string>? metadata)
        {
            Type? optionsType = Type.GetType("Minio.Model.PutObjectOptions, Minio", throwOnError: false);
            if (optionsType == null)
                return null;

            object options = Activator.CreateInstance(optionsType)
                ?? throw new InvalidOperationException("Unable to create Minio.Model.PutObjectOptions.");

            if (metadata == null || metadata.Count == 0)
                return options;

            PropertyInfo? userMetadataProp = optionsType.GetProperty("UserMetadata");
            if (userMetadataProp?.GetValue(options) is IDictionary<string, string> userMeta)
            {
                foreach (KeyValuePair<string, string> kv in metadata)
                    userMeta[kv.Key] = kv.Value;
            }

            if (metadata.TryGetValue("ContentType", out string? contentType) && !string.IsNullOrWhiteSpace(contentType))
            {
                PropertyInfo? contentTypeProp = optionsType.GetProperty("ContentType");
                if (contentTypeProp != null)
                    contentTypeProp.SetValue(options, MediaTypeHeaderValue.Parse(contentType));
            }

            return options;
        }

        private static object? InvokeMethod(object instance, string methodName, params object?[] args)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            Type type = instance.GetType();
            MethodInfo? method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);

            if (method == null)
                throw new MissingMethodException(type.FullName, methodName);

            return method.Invoke(instance, args);
        }

        private static async Task AwaitTaskOnlyAsync(object? taskObj)
        {
            if (taskObj is not Task task)
                throw new InvalidOperationException("Reflection target did not return a Task.");

            await task.ConfigureAwait(false);
        }

        private static async Task<object?> AwaitTaskResultAsync(object? taskObj)
        {
            if (taskObj is not Task task)
                throw new InvalidOperationException("Reflection target did not return a Task.");

            await task.ConfigureAwait(false);

            PropertyInfo? resultProp = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return resultProp?.GetValue(task);
        }

        private static bool LooksLikeNotFound(Exception exception)
        {
            string message = exception.ToString();
            return message.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("NoSuchKey", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static long? ReadNullableLongProperty(object obj, string propertyName)
        {
            PropertyInfo? prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            object? value = prop?.GetValue(obj);
            return value switch
            {
                null => null,
                long l => l,
                int i => i,
                _ => null
            };
        }

        private static string? ReadConnectionValue(string? connection, string key)
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

        private static bool ReadBoolConnectionValue(string? connection, string key, bool defaultValue)
        {
            string? raw = ReadConnectionValue(connection, key);
            return bool.TryParse(raw, out bool value) ? value : defaultValue;
        }

        private static string ReadStringProperty(object obj, string propertyName)
        {
            PropertyInfo? prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            object? value = prop?.GetValue(obj);
            return value?.ToString() ?? string.Empty;
        }
    }
}
