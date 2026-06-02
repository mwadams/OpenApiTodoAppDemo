using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Corvus.Text.Json;
using TodoApp.Api.Server;
using TodoApp.Api.Server.Models;

namespace TodoApp.Api.Handlers;

/// <summary>
/// Service for reading/writing todo lists to Azure Blob Storage using SAS tokens.
/// Uses CTJ types directly — no STJ, no intermediate allocations.
/// </summary>
public sealed class BlobTodoStore
{
    private static BlobClient CreateBlobClient(JsonString sasToken)
    {
        // Uri requires a string — this is the one unavoidable allocation.
        return new BlobClient(new Uri(sasToken.GetString()!));
    }

    /// <summary>
    /// Reads the todo list as an immutable document (for read-only operations).
    /// The returned document is owned by the caller and must be kept alive while
    /// accessing its RootElement. In practice the workspace outlives the handler call.
    /// </summary>
    public async Task<ParsedJsonDocument<TodoList>?> ReadAsync(
        JsonString sasToken, CancellationToken ct)
    {
        BlobClient blob = CreateBlobClient(sasToken);

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        Response<BlobDownloadStreamingResult> response =
            await blob.DownloadStreamingAsync(cancellationToken: ct);

        await using Stream stream = response.Value.Content;
        return await ParsedJsonDocument<TodoList>.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Reads the todo list directly into a mutable document builder (for Create/Update/Delete).
    /// The builder is registered with the workspace and its lifetime is managed by the workspace.
    /// Returns null if the blob does not exist.
    /// </summary>
    public async Task<(JsonDocumentBuilder<TodoList.Mutable> Builder, ETag ETag)?> ReadMutableAsync(
        JsonString sasToken, JsonWorkspace workspace, CancellationToken ct)
    {
        BlobClient blob = CreateBlobClient(sasToken);

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        Response<BlobDownloadStreamingResult> response =
            await blob.DownloadStreamingAsync(cancellationToken: ct);

        await using Stream stream = response.Value.Content;
        JsonDocumentBuilder<TodoList.Mutable> builder =
            JsonDocumentBuilder<TodoList.Mutable>.Parse(workspace, stream);

        return (builder, response.Value.Details.ETag);
    }

    /// <summary>
    /// Writes the modified mutable document back to blob storage.
    /// Always uses conditional writes: If-Match when updating an existing blob,
    /// If-None-Match: * when creating a new blob.
    /// </summary>
    public async Task WriteAsync(
        JsonString sasToken,
        JsonDocumentBuilder<TodoList.Mutable> builder,
        ETag expectedETag,
        CancellationToken ct)
    {
        BlobClient blob = CreateBlobClient(sasToken);

        var options = new BlobOpenWriteOptions
        {
            OpenConditions = expectedETag == default
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = expectedETag },
        };

        await using var stream = await blob.OpenWriteAsync(overwrite: true, options, ct);
        using var writer = new Utf8JsonWriter(stream);
        builder.WriteTo(writer);
    }

    /// <summary>
    /// Creates a new empty TodoList mutable builder for first-time writes.
    /// </summary>
    public static JsonDocumentBuilder<TodoList.Mutable> CreateEmpty(JsonWorkspace workspace)
    {
        return JsonDocumentBuilder<TodoList.Mutable>.Parse(workspace, "[]"u8);
    }
}
