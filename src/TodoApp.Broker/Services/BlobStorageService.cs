using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Corvus.Text.Json;
using RuntimeModels = TodoApp.Broker.Runtime.Models;

namespace TodoApp.Broker.Services;

/// <summary>
/// Encapsulates all Azurite/Azure Blob Storage interactions for the broker.
/// This is the ONLY component in the system that holds a storage connection string.
/// </summary>
public sealed class BlobStorageService
{
    private const string RegistryContainer = "registry";
    private const string RegistryBlobName = "registry.json";
    private const string ProvisioningTicketBlobPrefix = "tickets/";
    private const string ProvisioningTicketBlobSuffix = ".json";
    private const string CatalogContainer = "catalog";
    private const string CatalogBlobName = "catalog.json";

    private readonly BlobServiceClient _serviceClient;

    public BlobStorageService(BlobServiceClient serviceClient)
    {
        _serviceClient = serviceClient;
    }

    /// <summary>
    /// Creates a blob container if it doesn't already exist.
    /// </summary>
    public async Task CreateContainerAsync(string containerName, CancellationToken ct)
    {
        var containerClient = _serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Generates a blob-level SAS URI with read/write/create permissions and a 1-hour expiry.
    /// </summary>
    public string GenerateBlobSasUri(string containerName, string blobName)
    {
        var containerClient = _serviceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Write | BlobSasPermissions.Create);

        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }

    /// <summary>
    /// Reads the registry blob, returning the parsed document and ETag.
    /// Returns null if the registry doesn't exist yet.
    /// </summary>
    public async Task<(JsonDocumentBuilder<Models.Registry.Mutable> Builder, Azure.ETag ETag)?> ReadRegistryAsync(
        JsonWorkspace workspace, CancellationToken ct)
    {
        var containerClient = _serviceClient.GetBlobContainerClient(RegistryContainer);
        var blobClient = containerClient.GetBlobClient(RegistryBlobName);

        if (!await blobClient.ExistsAsync(ct))
        {
            return null;
        }

        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;

        var builder = JsonDocumentBuilder<Models.Registry.Mutable>.Parse(workspace, stream);
        return (builder, response.Value.Details.ETag);
    }

    /// <summary>
    /// Writes the registry blob with conditional ETag. Returns true on success.
    /// </summary>
    public async Task<bool> WriteRegistryAsync(
        JsonDocumentBuilder<Models.Registry.Mutable> builder, Azure.ETag etag, CancellationToken ct)
    {
        var containerClient = _serviceClient.GetBlobContainerClient(RegistryContainer);
        var blobClient = containerClient.GetBlobClient(RegistryBlobName);

        using var stream = new MemoryStream();
        await using (var writer = new Corvus.Text.Json.Utf8JsonWriter(stream))
        {
            builder.RootElement.WriteTo(writer);
        }

        stream.Position = 0;
        try
        {
            await blobClient.UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = etag },
                },
                ct);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a provisioning ticket status blob. Returns false if the ticket already exists.
    /// </summary>
    public async Task<bool> CreateProvisioningStatusAsync(
        Guid ticket,
        RuntimeModels.ProvisioningStatus status,
        CancellationToken ct)
    {
        return await UploadProvisioningStatusAsync(
            ticket,
            status,
            new BlobRequestConditions { IfNoneMatch = Azure.ETag.All },
            ct);
    }

    /// <summary>
    /// Reads a provisioning ticket status blob into the supplied workspace.
    /// </summary>
    public async Task<(JsonDocumentBuilder<RuntimeModels.ProvisioningStatus.Mutable> Builder, Azure.ETag ETag)?> ReadProvisioningStatusAsync(
        RuntimeModels.JsonUuid ticket,
        JsonWorkspace workspace,
        CancellationToken ct)
    {
        var blobClient = GetProvisioningTicketBlobClient(ticket);

        if (!await blobClient.ExistsAsync(ct))
        {
            return null;
        }

        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        var builder = JsonDocumentBuilder<RuntimeModels.ProvisioningStatus.Mutable>.Parse(workspace, stream);
        return (builder, response.Value.Details.ETag);
    }

    /// <summary>
    /// Updates a provisioning ticket status blob with conditional ETag protection.
    /// </summary>
    public async Task<bool> WriteProvisioningStatusAsync(
        Guid ticket,
        RuntimeModels.ProvisioningStatus status,
        Azure.ETag etag,
        CancellationToken ct)
    {
        return await UploadProvisioningStatusAsync(
            ticket,
            status,
            new BlobRequestConditions { IfMatch = etag },
            ct);
    }

    /// <summary>
    /// Creates the registry container and initializes an empty registry blob if it doesn't exist.
    /// Also ensures the catalog container exists.
    /// </summary>
    public async Task EnsureRegistryExistsAsync(CancellationToken ct)
    {
        var containerClient = _serviceClient.GetBlobContainerClient(RegistryContainer);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

        var blobClient = containerClient.GetBlobClient(RegistryBlobName);
        if (!await blobClient.ExistsAsync(ct))
        {
            var emptyRegistry = """{"containers":[]}"""u8;
            using var stream = new MemoryStream(emptyRegistry.ToArray());
            await blobClient.UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = Azure.ETag.All },
                },
                ct);
        }

        // Ensure the catalog container exists (stores directory data accessed via SAS)
        var catalogClient = _serviceClient.GetBlobContainerClient(CatalogContainer);
        await catalogClient.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Derives the container name for a user's personal storage.
    /// Uses a truncated SHA256 hash of the identity subject for safe container naming.
    /// </summary>
    public static string UserContainerName(string identitySub)
        => $"user-{HashForContainerName(identitySub)}";

    public static string UserContainerName<TIdentitySub>(in TIdentitySub identitySub)
        where TIdentitySub : struct, Corvus.Text.Json.Internal.IJsonElement<TIdentitySub>
    {
        using UnescapedUtf8JsonString identitySubUtf8 = GetUnescapedStringValue(identitySub);
        return $"user-{HashForContainerName(identitySubUtf8.Span)}";
    }

    /// <summary>
    /// Derives the container name for an organization entity.
    /// </summary>
    public static string OrgContainerName(string orgId)
        => $"org-{HashForContainerName(orgId)}";

    public static string OrgContainerName<TOrgId>(in TOrgId orgId)
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
    {
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);
        return $"org-{HashForContainerName(orgIdUtf8.Span)}";
    }

    /// <summary>
    /// Derives the container name for a user's storage within an organization.
    /// </summary>
    public static string OrgMemberContainerName(string identitySub, string orgId)
    {
        // "om-" (3) + 28-char hash + "-" (1) + 28-char hash = 60 chars (within 63 limit)
        return $"om-{HashForContainerName(identitySub)}-{HashForContainerName(orgId)}";
    }

    public static string OrgMemberContainerName<TIdentitySub, TOrgId>(in TIdentitySub identitySub, in TOrgId orgId)
        where TIdentitySub : struct, Corvus.Text.Json.Internal.IJsonElement<TIdentitySub>
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
    {
        using UnescapedUtf8JsonString identitySubUtf8 = GetUnescapedStringValue(identitySub);
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);

        // "om-" (3) + 28-char hash + "-" (1) + 28-char hash = 60 chars (within 63 limit)
        return $"om-{HashForContainerName(identitySubUtf8.Span)}-{HashForContainerName(orgIdUtf8.Span)}";
    }

    private static UnescapedUtf8JsonString GetUnescapedStringValue<TValue>(in TValue value)
        where TValue : struct, Corvus.Text.Json.Internal.IJsonElement<TValue>
    {
        return value.ParentDocument.GetUtf8JsonString(
            value.ParentDocumentIndex,
            Corvus.Text.Json.Internal.JsonTokenType.String);
    }

    private async Task<bool> UploadProvisioningStatusAsync(
        Guid ticket,
        RuntimeModels.ProvisioningStatus status,
        BlobRequestConditions conditions,
        CancellationToken ct)
    {
        var blobClient = GetProvisioningTicketBlobClient(ticket);
        using var stream = new MemoryStream();
        using (var writer = new Corvus.Text.Json.Utf8JsonWriter(stream))
        {
            status.WriteTo(writer);
        }

        stream.Position = 0;

        try
        {
            await blobClient.UploadAsync(
                stream,
                new BlobUploadOptions { Conditions = conditions },
                ct);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    private BlobClient GetProvisioningTicketBlobClient(Guid ticket)
    {
        return _serviceClient
            .GetBlobContainerClient(RegistryContainer)
            .GetBlobClient(ProvisioningTicketBlobName(ticket));
    }

    private BlobClient GetProvisioningTicketBlobClient(RuntimeModels.JsonUuid ticket)
    {
        if (!ticket.TryGetValue(out Guid ticketId))
        {
            throw new FormatException("Provisioning ticket is not a valid UUID.");
        }

        return GetProvisioningTicketBlobClient(ticketId);
    }

    private static string ProvisioningTicketBlobName(Guid ticket)
        => string.Concat(ProvisioningTicketBlobPrefix, ticket.ToString("D"), ProvisioningTicketBlobSuffix);

    /// <summary>
    /// Produces a deterministic, Azure-safe container name segment from an arbitrary string.
    /// Returns 28 lowercase hex chars (first 14 bytes of SHA256).
    /// </summary>
    private static string HashForContainerName(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..14]).ToLowerInvariant();
    }

    private static string HashForContainerName(ReadOnlySpan<byte> value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(value, hash);
        return Convert.ToHexString(hash[..14]).ToLowerInvariant();
    }
}
