namespace QueryFarm.VgiRpc.External;

/// <summary>A worker's large-value storage backend (direct-storage contract v1, §1).</summary>
public enum StorageBackend
{
    /// <summary>No external storage: everything stays inline.</summary>
    None,

    /// <summary>The Query.Farm blob vendor (<c>VGI_BLOB_VENDOR_URL</c>).</summary>
    Vendor,

    /// <summary>The operator's S3 or S3-compatible bucket (package <c>QueryFarm.VgiRpc.S3</c>).</summary>
    S3,

    /// <summary>The operator's GCS bucket (package <c>QueryFarm.VgiRpc.Gcs</c>).</summary>
    Gcs,
}

/// <summary>
/// Selects the storage backend from the environment: <c>VGI_STORAGE_BACKEND</c> = <c>vendor</c> |
/// <c>s3</c> | <c>gcs</c> | <c>none</c>; unset, <c>vendor</c> when <c>VGI_BLOB_VENDOR_URL</c> is set,
/// else <c>s3</c> when <c>VGI_STORAGE_S3_BUCKET</c> is set, else <c>gcs</c> when
/// <c>VGI_STORAGE_GCS_BUCKET</c> is set, else <c>none</c>. More than one of those set without an
/// explicit <c>VGI_STORAGE_BACKEND</c> refuses to start.
/// </summary>
public static class StorageBackendEnvironment
{
    /// <summary>Selects the backend explicitly.</summary>
    public const string Backend = "VGI_STORAGE_BACKEND";

    /// <summary>The S3 bucket (selects <see cref="StorageBackend.S3"/> when unset otherwise).</summary>
    public const string S3Bucket = "VGI_STORAGE_S3_BUCKET";

    /// <summary>The GCS bucket (selects <see cref="StorageBackend.Gcs"/> when unset otherwise).</summary>
    public const string GcsBucket = "VGI_STORAGE_GCS_BUCKET";

    /// <summary>Presigned URL lifetime in seconds (default 3600, capped by credential expiry).</summary>
    public const string UrlTtl = "VGI_STORAGE_URL_TTL_SECONDS";

    /// <summary>The default presigned URL lifetime: one hour.</summary>
    public static readonly TimeSpan DefaultUrlTtl = TimeSpan.FromHours(1);

    /// <summary>The backend the environment selects.</summary>
    /// <exception cref="ArgumentException">An unknown <c>VGI_STORAGE_BACKEND</c>, or more than one
    /// backend configured without it.</exception>
    public static StorageBackend Select(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var raw = env(Backend)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(raw))
        {
            return raw switch
            {
                "vendor" => StorageBackend.Vendor,
                "s3" => StorageBackend.S3,
                "gcs" => StorageBackend.Gcs,
                "none" => StorageBackend.None,
                _ => throw new ArgumentException($"{Backend} must be vendor, s3, gcs or none, got '{raw}'"),
            };
        }

        var set = new List<(string Name, StorageBackend Backend)>();
        foreach (var (name, backend) in new[]
                 {
                     (BlobVendorEnvironment.Url, StorageBackend.Vendor),
                     (S3Bucket, StorageBackend.S3),
                     (GcsBucket, StorageBackend.Gcs),
                 })
        {
            if (!string.IsNullOrWhiteSpace(env(name)))
            {
                set.Add((name, backend));
            }
        }

        return set.Count switch
        {
            0 => StorageBackend.None,
            1 => set[0].Backend,
            _ => throw new ArgumentException(
                $"storage configuration conflict: {string.Join(", ", set.Select(s => s.Name))} are all set; set {Backend} to choose one"),
        };
    }

    /// <summary><c>VGI_STORAGE_URL_TTL_SECONDS</c> (at least 60, at most 7 days), else one hour.</summary>
    /// <exception cref="ArgumentException">Not an integer of at least 60.</exception>
    public static TimeSpan UrlTtlFrom(Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(UrlTtl)?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return DefaultUrlTtl;
        }

        return long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 60
            ? TimeSpan.FromSeconds(Math.Min(seconds, 7 * 24 * 3600))
            : throw new ArgumentException($"{UrlTtl} must be an integer number of seconds >= 60, got '{raw}'");
    }
}
