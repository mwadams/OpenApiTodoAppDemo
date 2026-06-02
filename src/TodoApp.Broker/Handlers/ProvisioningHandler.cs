using Corvus.Text.Json;
using TodoApp.Broker.Services;

using Api = TodoApp.Broker.ApiClient;
using ApiModels = TodoApp.Broker.ApiClient.Models;
using Runtime = TodoApp.Broker.Runtime;
using RuntimeModels = TodoApp.Broker.Runtime.Models;

namespace TodoApp.Broker.Handlers;

/// <summary>
/// Handles provisioning requests from the back-end API.
/// Creates a provisioning ticket, starts background work to create the blob
/// container and notify the API via callback, and returns the ticket immediately.
/// </summary>
public sealed class ProvisioningHandler : Runtime.IApiProvisioningHandler
{
    private readonly BlobStorageService storage;
    private readonly Api.IApiCallbacksClient callbackClient;
    private readonly ProvisioningTracker tracker;
    private readonly ILogger<ProvisioningHandler> logger;

    public ProvisioningHandler(
        BlobStorageService storage,
        Api.IApiCallbacksClient callbackClient,
        ProvisioningTracker tracker,
        ILogger<ProvisioningHandler> logger)
    {
        this.storage = storage;
        this.callbackClient = callbackClient;
        this.tracker = tracker;
        this.logger = logger;
    }

    public ValueTask<Runtime.ProvisionUserStorageResult> HandleProvisionUserStorageAsync(
        Runtime.ProvisionUserStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        return this.HandleProvisionUserStorageCoreAsync(parameters, workspace, cancellationToken);
    }

    private async ValueTask<Runtime.ProvisionUserStorageResult> HandleProvisionUserStorageCoreAsync(
        Runtime.ProvisionUserStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        UserProvisioningOperation operation = await this.tracker.CreateUserOperationAsync(
            parameters.UserId,
            parameters.Body,
            cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await this.ProvisionUserAsync(operation);
                await this.tracker.MarkReadyAsync(operation.Ticket, CancellationToken.None);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Provisioning failed for user storage");
                try
                {
                    await this.tracker.MarkFailedAsync(operation.Ticket, ex.Message, CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    this.logger.LogError(markEx, "Failed to mark user provisioning ticket as failed");
                }
            }
        }, CancellationToken.None);

        return Runtime.ProvisionUserStorageResult.Accepted(
                body: RuntimeModels.ProvisionResponse.Build(
                (ref b) =>
                    {
                        b.Create(status: "accepted"u8, ticket: operation.Ticket);
                    }),
                workspace: workspace);
    }

    public ValueTask<Runtime.ProvisionOrgStorageResult> HandleProvisionOrgStorageAsync(
        Runtime.ProvisionOrgStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        return this.HandleProvisionOrgStorageCoreAsync(parameters, workspace, cancellationToken);
    }

    private async ValueTask<Runtime.ProvisionOrgStorageResult> HandleProvisionOrgStorageCoreAsync(
        Runtime.ProvisionOrgStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        OrganizationProvisioningOperation operation = await this.tracker.CreateOrganizationOperationAsync(
            parameters.OrgId,
            parameters.Body,
            cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await this.ProvisionOrgAsync(operation);
                await this.tracker.MarkReadyAsync(operation.Ticket, CancellationToken.None);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Provisioning failed for organization storage");
                try
                {
                    await this.tracker.MarkFailedAsync(operation.Ticket, ex.Message, CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    this.logger.LogError(markEx, "Failed to mark organization provisioning ticket as failed");
                }
            }
        }, CancellationToken.None);

        return Runtime.ProvisionOrgStorageResult.Accepted(
                body: RuntimeModels.ProvisionResponse.Build(
                (ref b) =>
                    {
                        b.Create(status: "accepted"u8, ticket: operation.Ticket);
                    }),
                workspace: workspace);
    }

    public ValueTask<Runtime.ProvisionOrgMemberStorageResult> HandleProvisionOrgMemberStorageAsync(
        Runtime.ProvisionOrgMemberStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        return this.HandleProvisionOrgMemberStorageCoreAsync(parameters, workspace, cancellationToken);
    }

    private async ValueTask<Runtime.ProvisionOrgMemberStorageResult> HandleProvisionOrgMemberStorageCoreAsync(
        Runtime.ProvisionOrgMemberStorageParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        OrganizationMemberProvisioningOperation operation =
            await this.tracker.CreateOrganizationMemberOperationAsync(
                parameters.OrgId,
                parameters.UserId,
                parameters.Body,
                cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await this.ProvisionOrgMemberAsync(operation);
                await this.tracker.MarkReadyAsync(operation.Ticket, CancellationToken.None);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Provisioning failed for organization member storage");
                try
                {
                    await this.tracker.MarkFailedAsync(operation.Ticket, ex.Message, CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    this.logger.LogError(markEx, "Failed to mark organization member provisioning ticket as failed");
                }
            }
        }, CancellationToken.None);

        return Runtime.ProvisionOrgMemberStorageResult.Accepted(
                body: RuntimeModels.ProvisionResponse.Build(
                (ref b) =>
                    {
                        b.Create(status: "accepted"u8, ticket: operation.Ticket);
                    }),
                workspace: workspace);
    }

    public async ValueTask<Runtime.GetProvisioningStatusResult> HandleGetProvisioningStatusAsync(
        Runtime.GetProvisioningStatusParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var result = await this.tracker.TryGetStatusAsync(parameters.Ticket, workspace, cancellationToken);
        if (!result.Found)
        {
            return Runtime.GetProvisioningStatusResult.NotFound(
                    body: RuntimeModels.ProblemDetails.Build(
                        (ref b) =>
                        {
                            b.Create(
                                type: "urn:todo-app:unknown-ticket"u8,
                                status: 404);
                        }),
                    workspace: workspace);
        }

        RuntimeModels.ProvisioningStatus status = result.Status;
        bool isProvisioning = status.Status.ValueEquals("provisioning"u8);

        return Runtime.GetProvisioningStatusResult.Ok(
                body: status,
                workspace: workspace,
                retryAfter: isProvisioning ? 2 : default);
    }

    private async Task ProvisionUserAsync(UserProvisioningOperation operation)
    {
        using JsonWorkspace workspace = JsonWorkspace.CreateUnrented();
        using var userIdBuilder = JsonDocumentBuilder<ApiModels.JsonUuid.Mutable>.Parse(workspace, operation.UserIdJson);
        using var payloadBuilder = JsonDocumentBuilder<ApiModels.NewUser.Mutable>.Parse(workspace, operation.PayloadJson);
        ApiModels.JsonUuid userId = userIdBuilder.RootElement;
        ApiModels.NewUser payload = payloadBuilder.RootElement;

        string containerName = BlobStorageService.UserContainerName(userId);
        await this.storage.CreateContainerAsync(containerName, CancellationToken.None);
        string sasToken = this.storage.GenerateBlobSasUri(containerName, "todos.json");

        await this.callbackClient.OnStorageProvisionedAsync(
            new ApiModels.ProvisioningNotification.Source(
            (ref b) =>
            {
                b.Create(entityId: userId, payload: payload, sasToken: sasToken);
            }),
            CancellationToken.None);
    }

    private async Task ProvisionOrgAsync(OrganizationProvisioningOperation operation)
    {
        using JsonWorkspace workspace = JsonWorkspace.CreateUnrented();
        using var orgIdBuilder = JsonDocumentBuilder<ApiModels.JsonUuid.Mutable>.Parse(workspace, operation.OrgIdJson);
        using var payloadBuilder = JsonDocumentBuilder<ApiModels.NewOrganization.Mutable>.Parse(workspace, operation.PayloadJson);
        ApiModels.JsonUuid orgId = orgIdBuilder.RootElement;
        ApiModels.NewOrganization payload = payloadBuilder.RootElement;

        string containerName = BlobStorageService.OrgContainerName(orgId);
        await this.storage.CreateContainerAsync(containerName, CancellationToken.None);
        string sasToken = this.storage.GenerateBlobSasUri(containerName, "todos.json");

        await this.callbackClient.OnStorageProvisionedAsync(
            new ApiModels.ProvisioningNotification.Source(
            (ref b) =>
            {
                b.Create(entityId: orgId, payload: payload, sasToken: sasToken);
            }),
            CancellationToken.None);
    }

    private async Task ProvisionOrgMemberAsync(OrganizationMemberProvisioningOperation operation)
    {
        using JsonWorkspace workspace = JsonWorkspace.CreateUnrented();
        using var orgIdBuilder = JsonDocumentBuilder<ApiModels.JsonUuid.Mutable>.Parse(workspace, operation.OrgIdJson);
        using var userIdBuilder = JsonDocumentBuilder<ApiModels.JsonUuid.Mutable>.Parse(workspace, operation.UserIdJson);
        using var payloadBuilder = JsonDocumentBuilder<ApiModels.AddMemberRequest.Mutable>.Parse(workspace, operation.PayloadJson);
        ApiModels.JsonUuid orgId = orgIdBuilder.RootElement;
        ApiModels.JsonUuid userId = userIdBuilder.RootElement;
        ApiModels.AddMemberRequest payload = payloadBuilder.RootElement;

        string containerName = BlobStorageService.OrgMemberContainerName(userId, orgId);
        await this.storage.CreateContainerAsync(containerName, CancellationToken.None);
        string sasToken = this.storage.GenerateBlobSasUri(containerName, "todos.json");

        await this.callbackClient.OnStorageProvisionedAsync(
            new ApiModels.ProvisioningNotification.Source(
            (ref b) =>
            {
                b.Create(
                    entityId: userId,
                    orgId: orgId,
                    payload: payload,
                    sasToken: sasToken);
            }),
            CancellationToken.None);
    }
}
