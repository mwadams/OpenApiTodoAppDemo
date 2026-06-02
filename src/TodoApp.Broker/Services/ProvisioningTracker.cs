using Corvus.Text.Json;

using ApiModels = TodoApp.Broker.ApiClient.Models;
using RuntimeModels = TodoApp.Broker.Runtime.Models;

namespace TodoApp.Broker.Services;

/// <summary>
/// Tracks provisioning ticket status in broker-owned blob storage.
/// </summary>
public sealed class ProvisioningTracker
{
    private const int MaxWriteAttempts = 3;
    private readonly BlobStorageService storage;

    public ProvisioningTracker(BlobStorageService storage)
    {
        this.storage = storage;
    }

    public async ValueTask<Guid> CreateTicketAsync(CancellationToken cancellationToken)
    {
        Guid ticket = Guid.NewGuid();
        await this.CreateInitialStatusAsync(ticket, cancellationToken);
        return ticket;
    }

    public async ValueTask<UserProvisioningOperation> CreateUserOperationAsync(
        RuntimeModels.JsonUuid userId,
        RuntimeModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        byte[] userIdJson;
        byte[] payloadJson;
        using (JsonWorkspace operationWorkspace = JsonWorkspace.CreateUnrented())
        {
            using JsonDocumentBuilder<ApiModels.JsonUuid.Mutable> callbackUserId =
                ApiModels.JsonUuid.CreateBuilder(operationWorkspace, ApiModels.JsonUuid.From(userId));
            using JsonDocumentBuilder<ApiModels.NewUser.Mutable> payload =
                ApiModels.NewUser.CreateBuilder(operationWorkspace, ApiModels.NewUser.From(request.Payload));

            userIdJson = ToJsonBytes(callbackUserId.RootElement);
            payloadJson = ToJsonBytes(payload.RootElement);
        }

        Guid ticket = await this.CreateTicketAsync(cancellationToken);
        return new UserProvisioningOperation(ticket, userIdJson, payloadJson);
    }

    public async ValueTask<OrganizationProvisioningOperation> CreateOrganizationOperationAsync(
        RuntimeModels.JsonUuid orgId,
        RuntimeModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        byte[] orgIdJson;
        byte[] payloadJson;
        using (JsonWorkspace operationWorkspace = JsonWorkspace.CreateUnrented())
        {
            using JsonDocumentBuilder<ApiModels.JsonUuid.Mutable> callbackOrgId =
                ApiModels.JsonUuid.CreateBuilder(operationWorkspace, ApiModels.JsonUuid.From(orgId));
            using JsonDocumentBuilder<ApiModels.NewOrganization.Mutable> payload =
                ApiModels.NewOrganization.CreateBuilder(operationWorkspace, ApiModels.NewOrganization.From(request.Payload));

            orgIdJson = ToJsonBytes(callbackOrgId.RootElement);
            payloadJson = ToJsonBytes(payload.RootElement);
        }

        Guid ticket = await this.CreateTicketAsync(cancellationToken);
        return new OrganizationProvisioningOperation(ticket, orgIdJson, payloadJson);
    }

    public async ValueTask<OrganizationMemberProvisioningOperation> CreateOrganizationMemberOperationAsync(
        RuntimeModels.JsonUuid orgId,
        RuntimeModels.JsonUuid userId,
        RuntimeModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        byte[] orgIdJson;
        byte[] userIdJson;
        byte[] payloadJson;
        using (JsonWorkspace operationWorkspace = JsonWorkspace.CreateUnrented())
        {
            using JsonDocumentBuilder<ApiModels.JsonUuid.Mutable> callbackOrgId =
                ApiModels.JsonUuid.CreateBuilder(operationWorkspace, ApiModels.JsonUuid.From(orgId));
            using JsonDocumentBuilder<ApiModels.JsonUuid.Mutable> callbackUserId =
                ApiModels.JsonUuid.CreateBuilder(operationWorkspace, ApiModels.JsonUuid.From(userId));
            using JsonDocumentBuilder<ApiModels.AddMemberRequest.Mutable> payload =
                ApiModels.AddMemberRequest.CreateBuilder(operationWorkspace, ApiModels.AddMemberRequest.From(request.Payload));

            orgIdJson = ToJsonBytes(callbackOrgId.RootElement);
            userIdJson = ToJsonBytes(callbackUserId.RootElement);
            payloadJson = ToJsonBytes(payload.RootElement);
        }

        Guid ticket = await this.CreateTicketAsync(cancellationToken);
        return new OrganizationMemberProvisioningOperation(ticket, orgIdJson, userIdJson, payloadJson);
    }

    public Task MarkReadyAsync(Guid ticket, CancellationToken cancellationToken)
    {
        return this.UpdateStatusAsync(ticket, failed: false, message: null, cancellationToken);
    }

    public Task MarkFailedAsync(Guid ticket, string? message, CancellationToken cancellationToken)
    {
        return this.UpdateStatusAsync(ticket, failed: true, message: message, cancellationToken);
    }

    public async ValueTask<(bool Found, RuntimeModels.ProvisioningStatus Status)> TryGetStatusAsync(
        RuntimeModels.JsonUuid ticket,
        JsonWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var result = await this.storage.ReadProvisioningStatusAsync(ticket, workspace, cancellationToken);
        if (result is null)
        {
            return (false, default);
        }

        return (true, result.Value.Builder.RootElement);
    }

    private async Task CreateInitialStatusAsync(Guid ticket, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using JsonWorkspace workspace = JsonWorkspace.CreateUnrented();
        using var statusBuilder = RuntimeModels.ProvisioningStatus.CreateBuilder(
            workspace,
            status: "provisioning"u8,
            ticket: ticket,
            createdAt: now,
            retryAfterSeconds: 2,
            updatedAt: now);

        await this.storage.CreateProvisioningStatusAsync(ticket, statusBuilder.RootElement, cancellationToken);
    }

    private async Task UpdateStatusAsync(
        Guid ticket,
        bool failed,
        string? message,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            using JsonWorkspace workspace = JsonWorkspace.CreateUnrented();
            using var ticketBuilder = RuntimeModels.JsonUuid.CreateBuilder(workspace, ticket);
            var existing = await this.storage.ReadProvisioningStatusAsync(
                ticketBuilder.RootElement,
                workspace,
                cancellationToken);

            if (existing is null)
            {
                return;
            }

            using JsonDocumentBuilder<RuntimeModels.ProvisioningStatus.Mutable> existingBuilder = existing.Value.Builder;
            RuntimeModels.ProvisioningStatus previous = existingBuilder.RootElement;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RuntimeModels.JsonString.Source messageSource = default;
            if (message is not null)
            {
                messageSource = message;
            }

            using var statusBuilder = failed
                ? RuntimeModels.ProvisioningStatus.CreateBuilder(
                    workspace,
                    status: "failed"u8,
                    ticket: previous.Ticket,
                    createdAt: previous.CreatedAt,
                    message: messageSource,
                    updatedAt: now)
                : RuntimeModels.ProvisioningStatus.CreateBuilder(
                    workspace,
                    status: "ready"u8,
                    ticket: previous.Ticket,
                    createdAt: previous.CreatedAt,
                    updatedAt: now);

            if (await this.storage.WriteProvisioningStatusAsync(
                ticket,
                statusBuilder.RootElement,
                existing.Value.ETag,
                cancellationToken))
            {
                return;
            }
        }
    }

    private static byte[] ToJsonBytes<TValue>(TValue value)
        where TValue : struct, Corvus.Text.Json.Internal.IJsonElement<TValue>
    {
        using var stream = new MemoryStream();
        using (var writer = new Corvus.Text.Json.Utf8JsonWriter(stream))
        {
            value.WriteTo(writer);
        }

        return stream.ToArray();
    }
}

public abstract class ProvisioningOperation(Guid ticket)
{
    public Guid Ticket { get; } = ticket;
}

public sealed class UserProvisioningOperation(
    Guid ticket,
    byte[] userIdJson,
    byte[] payloadJson) : ProvisioningOperation(ticket)
{
    public byte[] UserIdJson => userIdJson;

    public byte[] PayloadJson => payloadJson;
}

public sealed class OrganizationProvisioningOperation(
    Guid ticket,
    byte[] orgIdJson,
    byte[] payloadJson) : ProvisioningOperation(ticket)
{
    public byte[] OrgIdJson => orgIdJson;

    public byte[] PayloadJson => payloadJson;
}

public sealed class OrganizationMemberProvisioningOperation(
    Guid ticket,
    byte[] orgIdJson,
    byte[] userIdJson,
    byte[] payloadJson) : ProvisioningOperation(ticket)
{
    public byte[] OrgIdJson => orgIdJson;

    public byte[] UserIdJson => userIdJson;

    public byte[] PayloadJson => payloadJson;
}
