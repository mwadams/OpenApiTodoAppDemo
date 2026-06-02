using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Corvus.Text.Json;
using IS = TodoApp.Identity.Storage;

namespace TodoApp.Identity;

/// <summary>
/// Blob-backed account store using CTJ types. All accounts are stored in a single
/// JSON blob (identity/accounts.json). Concurrency managed via ETag + in-process semaphore.
/// </summary>
internal sealed class AccountStore
{
    private const string ContainerName = "identity";
    private const string BlobName = "accounts.json";

    private readonly BlobServiceClient _blobService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AccountStore(BlobServiceClient blobService)
    {
        _blobService = blobService;
    }

    /// <summary>
    /// Result of a lookup that keeps the backing document alive until disposed.
    /// </summary>
    public readonly struct LookupResult : IDisposable
    {
        private readonly ParsedJsonDocument<IS.AccountDirectory>? _doc;

        public IS.AccountDirectory.Account? Account { get; }

        internal LookupResult(ParsedJsonDocument<IS.AccountDirectory>? doc, IS.AccountDirectory.Account? account)
        {
            _doc = doc;
            Account = account;
        }

        public void Dispose() => _doc?.Dispose();
    }

    /// <summary>
    /// Finds an account by email (case-insensitive). Caller must dispose the result.
    /// </summary>
    public async Task<LookupResult> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var doc = await ReadAsync(ct);
        if (doc is null)
        {
            return new LookupResult(null, null);
        }

        foreach (IS.AccountDirectory.Account account in doc.RootElement.Accounts.EnumerateArray())
        {
            if (account.Email.ValueEquals(email))
            {
                return new LookupResult(doc, account);
            }
        }

        return new LookupResult(doc, null);
    }

    /// <summary>
    /// Finds an account by subject identifier. Caller must dispose the result.
    /// </summary>
    public async Task<LookupResult> FindBySubAsync(string sub, CancellationToken ct = default)
    {
        var doc = await ReadAsync(ct);
        if (doc is null)
        {
            return new LookupResult(null, null);
        }

        foreach (IS.AccountDirectory.Account account in doc.RootElement.Accounts.EnumerateArray())
        {
            if (account.Sub.ValueEquals(sub))
            {
                return new LookupResult(doc, account);
            }
        }

        return new LookupResult(doc, null);
    }

    /// <summary>
    /// Adds a new account to the store using optimistic concurrency.
    /// </summary>
    public async Task AddAccountAsync(
        string sub, string email, string displayName, string passwordHash, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var workspace = JsonWorkspace.CreateUnrented();
            var (builder, etag) = await ReadMutableAsync(workspace, ct);
            IS.AccountDirectory.Mutable directory = builder.RootElement;

            // Build a new directory with the existing accounts plus the new one
            using var updated = IS.AccountDirectory.CreateBuilder(workspace, (ref b) =>
            {
                b.Create(
                    accounts: IS.AccountDirectory.AccountsArray.Build((ref ab) =>
                    {
                        foreach (IS.AccountDirectory.Account.Mutable existing in directory.Accounts.EnumerateArray())
                        {
#pragma warning disable CTJ002 // C# requires explicit cast (two implicit conversions cannot chain)
                            ab.AddItem((IS.AccountDirectory.Account)existing);
#pragma warning restore CTJ002
                        }

                        ab.AddItem(IS.AccountDirectory.Account.Build((ref acb) =>
                        {
                            acb.Create(sub: sub, email: email, displayName: displayName, passwordHash: passwordHash);
                        }));
                    }));
            });

            await WriteAsync(updated, etag, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ensures the identity container exists.
    /// </summary>
    public async Task EnsureContainerExistsAsync(CancellationToken ct = default)
    {
        var container = _blobService.GetBlobContainerClient(ContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    private async Task<ParsedJsonDocument<IS.AccountDirectory>?> ReadAsync(CancellationToken ct)
    {
        var blob = GetBlobClient();

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        Response<BlobDownloadStreamingResult> response =
            await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using Stream stream = response.Value.Content;
        return await ParsedJsonDocument<IS.AccountDirectory>.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<(JsonDocumentBuilder<IS.AccountDirectory.Mutable> Builder, ETag ETag)> ReadMutableAsync(
        JsonWorkspace workspace, CancellationToken ct)
    {
        var blob = GetBlobClient();

        if (!await blob.ExistsAsync(ct))
        {
            return (CreateEmpty(workspace), default);
        }

        Response<BlobDownloadStreamingResult> response =
            await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using Stream stream = response.Value.Content;
#pragma warning disable CTJ006 // Builder lifetime managed by workspace
        var builder = JsonDocumentBuilder<IS.AccountDirectory.Mutable>.Parse(workspace, stream);
#pragma warning restore CTJ006
        return (builder, response.Value.Details.ETag);
    }

    private async Task WriteAsync(
        JsonDocumentBuilder<IS.AccountDirectory.Mutable> directory, ETag expectedETag, CancellationToken ct)
    {
        var blob = GetBlobClient();

        var options = new BlobOpenWriteOptions
        {
            OpenConditions = expectedETag == default
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = expectedETag },
        };

        await using var stream = await blob.OpenWriteAsync(overwrite: true, options, ct);
        using var writer = new Corvus.Text.Json.Utf8JsonWriter(stream);
        directory.WriteTo(writer);
    }

    private BlobClient GetBlobClient()
    {
        return _blobService.GetBlobContainerClient(ContainerName).GetBlobClient(BlobName);
    }

    private static JsonDocumentBuilder<IS.AccountDirectory.Mutable> CreateEmpty(JsonWorkspace workspace)
    {
        return JsonDocumentBuilder<IS.AccountDirectory.Mutable>.Parse(workspace, """{"accounts":[]}"""u8);
    }
}
