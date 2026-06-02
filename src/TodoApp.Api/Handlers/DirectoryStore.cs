using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Corvus.Text.Json;
using Directory = TodoApp.Api.Directory;
using DirectoryModels = TodoApp.Api.Directory.Models;
using DS = TodoApp.Api.DirectoryStorage;

namespace TodoApp.Api.Handlers;

/// <summary>
/// Reads and writes the directory blob (users, organizations, memberships)
/// using a SAS token provided in the request header.
/// Uses generated Corvus types for zero-allocation JSON processing.
/// </summary>
public sealed class DirectoryStore
{
    /// <summary>
    /// Reads the directory into a mutable document builder (for create/update/delete).
    /// The builder is owned by the workspace and lives until
    /// the workspace is disposed (i.e., until the request completes).
    /// Returns an empty directory if the blob doesn't exist.
    /// </summary>
    public async Task<(JsonDocumentBuilder<DS.Directory.Mutable> Builder, ETag ETag)> ReadMutableAsync(
        string sasUrl, JsonWorkspace workspace, CancellationToken ct)
    {
        var blob = new BlobClient(new Uri(sasUrl));

        if (!await blob.ExistsAsync(ct))
        {
            return (CreateEmpty(workspace), default);
        }

        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        var builder = JsonDocumentBuilder<DS.Directory.Mutable>.Parse(workspace, stream);
        return (builder, response.Value.Details.ETag);
    }

    /// <summary>
    /// Writes the directory back to the blob with optimistic concurrency.
    /// </summary>
    public async Task WriteAsync(string sasUrl, JsonDocumentBuilder<DS.Directory.Mutable> directory, ETag expectedETag, CancellationToken ct)
    {
        var blob = new BlobClient(new Uri(sasUrl));

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

    /// <summary>
    /// Creates an empty directory document owned by the given workspace.
    /// </summary>
    public static JsonDocumentBuilder<DS.Directory.Mutable> CreateEmpty(JsonWorkspace workspace)
    {
        return JsonDocumentBuilder<DS.Directory.Mutable>.Parse(
            workspace,
            """{"users":[],"organizations":[],"memberships":[]}"""u8);
    }

    // ─── Query helpers ───────────────────────────────────────────────────────────
    // These return lightweight views into the directory document. Returned values
    // must not outlive the source workspace/builder.

    public static UnescapedUtf8JsonString GetUnescapedStringValue<TValue>(in TValue value)
        where TValue : struct, Corvus.Text.Json.Internal.IJsonElement<TValue>
    {
        return value.ParentDocument.GetUtf8JsonString(
            value.ParentDocumentIndex,
            Corvus.Text.Json.Internal.JsonTokenType.String);
    }

    public static bool JsonStringValueEquals<TLeft, TRight>(in TLeft left, in TRight right)
        where TLeft : struct, Corvus.Text.Json.Internal.IJsonElement<TLeft>
        where TRight : struct, Corvus.Text.Json.Internal.IJsonElement<TRight>
    {
        using UnescapedUtf8JsonString leftUtf8 = GetUnescapedStringValue(left);
        using UnescapedUtf8JsonString rightUtf8 = GetUnescapedStringValue(right);
        return leftUtf8.Span.SequenceEqual(rightUtf8.Span);
    }

    /// <summary>
    /// Finds a user by their ID (identity subject).
    /// </summary>
    public static DS.Directory.DirectoryUser.Mutable FindUserById<TUserId>(
        DS.Directory.Mutable directory,
        in TUserId userId)
        where TUserId : struct, Corvus.Text.Json.Internal.IJsonElement<TUserId>
    {
        using UnescapedUtf8JsonString userIdUtf8 = GetUnescapedStringValue(userId);
        foreach (DS.Directory.DirectoryUser.Mutable user in directory.Users.EnumerateArray())
        {
            if (user.Id.ValueEquals(userIdUtf8.Span)) return user;
        }

        return default;
    }

    /// <summary>
    /// Finds a user by email.
    /// </summary>
    public static DS.Directory.DirectoryUser.Mutable FindUserByEmail(
        DS.Directory.Mutable directory,
        in DirectoryModels.JsonEmail email)
    {
        using UnescapedUtf8JsonString emailUtf8 = email.GetUtf8String();
        foreach (DS.Directory.DirectoryUser.Mutable user in directory.Users.EnumerateArray())
        {
            if (user.Email.IsUndefined())
            {
                continue;
            }

            using UnescapedUtf8JsonString userEmailUtf8 = user.Email.GetUtf8String();
            if (Utf8OrdinalIgnoreCaseEquals(userEmailUtf8.Span, emailUtf8.Span))
            {
                return user;
            }
        }

        return default;
    }

    private static bool Utf8OrdinalIgnoreCaseEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            byte l = ToLowerAscii(left[i]);
            byte r = ToLowerAscii(right[i]);
            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value)
    {
        return (uint)(value - 'A') <= 'Z' - 'A'
            ? (byte)(value + ('a' - 'A'))
            : value;
    }

    /// <summary>
    /// Finds an organization by its ID.
    /// </summary>
    public static DS.Directory.DirectoryOrganization.Mutable FindOrganizationById<TOrgId>(
        DS.Directory.Mutable directory,
        in TOrgId orgId)
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
    {
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);
        foreach (DS.Directory.DirectoryOrganization.Mutable org in directory.Organizations.EnumerateArray())
        {
            if (org.Id.ValueEquals(orgIdUtf8.Span)) return org;
        }

        return default;
    }

    /// <summary>
    /// Checks whether the given user holds the specified role in the given organization.
    /// Returns whether the org has any members at all (for bootstrap detection) and whether
    /// the user holds the requested role.
    /// </summary>
    /// <remarks>
    /// In a production application this would be a cached database lookup or, better still,
    /// a claim on the security principal populated during authentication so that authorization
    /// checks do not require reading storage on every request.
    /// </remarks>
    public static (bool OrgHasMembers, bool UserHasRole) IsInOrganizationRole<TOrgId, TUserId>(
        DS.Directory.Mutable directory,
        in TOrgId orgId,
        in TUserId userId,
        ReadOnlySpan<byte> requiredRoleUtf8)
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
        where TUserId : struct, Corvus.Text.Json.Internal.IJsonElement<TUserId>
    {
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);
        using UnescapedUtf8JsonString userIdUtf8 = GetUnescapedStringValue(userId);

        bool orgHasMembers = false;
        bool userHasRole = false;
        foreach (DS.Directory.DirectoryMembership.Mutable m in directory.Memberships.EnumerateArray())
        {
            if (m.OrgId.ValueEquals(orgIdUtf8.Span))
            {
                orgHasMembers = true;
                if (m.UserId.ValueEquals(userIdUtf8.Span) && m.Role.ValueEquals(requiredRoleUtf8))
                {
                    userHasRole = true;
                }
            }
        }

        return (orgHasMembers, userHasRole);
    }

    /// <summary>
    /// Checks whether a user is already a member of the given organization (any role).
    /// </summary>
    public static bool IsMemberOfOrganization<TOrgId, TUserId>(
        DS.Directory.Mutable directory,
        in TOrgId orgId,
        in TUserId userId)
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
        where TUserId : struct, Corvus.Text.Json.Internal.IJsonElement<TUserId>
    {
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);
        using UnescapedUtf8JsonString userIdUtf8 = GetUnescapedStringValue(userId);

        foreach (DS.Directory.DirectoryMembership.Mutable m in directory.Memberships.EnumerateArray())
        {
            if (m.OrgId.ValueEquals(orgIdUtf8.Span) && m.UserId.ValueEquals(userIdUtf8.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the number of admins in an organization.
    /// </summary>
    public static int CountOrganizationAdmins<TOrgId>(
        DS.Directory.Mutable directory,
        in TOrgId orgId)
        where TOrgId : struct, Corvus.Text.Json.Internal.IJsonElement<TOrgId>
    {
        using UnescapedUtf8JsonString orgIdUtf8 = GetUnescapedStringValue(orgId);
        int count = 0;
        foreach (DS.Directory.DirectoryMembership.Mutable m in directory.Memberships.EnumerateArray())
        {
            if (m.OrgId.ValueEquals(orgIdUtf8.Span) && m.Role.ValueEquals("admin"u8)) count++;
        }

        return count;
    }
}
