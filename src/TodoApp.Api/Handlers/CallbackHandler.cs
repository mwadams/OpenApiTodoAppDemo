using Azure;
using Corvus.Text.Json;
using TodoApp.Api.Server;
using TodoApp.Api.Server.Models;

namespace TodoApp.Api.Handlers;

/// <summary>
/// Handles storage provisioning callbacks from the request broker.
/// When the broker finishes provisioning storage for an entity, it calls
/// back with a SAS token and the original request payload. The handler
/// uses the SAS token to initialize the newly provisioned blob with an
/// empty todo list.
/// </summary>
public sealed class CallbackHandler : IApiCallbacksHandler
{
    private readonly BlobTodoStore _todoStore;

    public CallbackHandler(BlobTodoStore todoStore)
    {
        _todoStore = todoStore;
    }

    public async ValueTask<OnStorageProvisionedResult> HandleOnStorageProvisionedAsync(
        OnStorageProvisionedParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        return await parameters.Body.Match<(BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct), ValueTask<OnStorageProvisionedResult>>(
            (Store: _todoStore, Workspace: workspace, Ct: cancellationToken),
            static (in UserStorageProvisioned notification, in (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
                => InitializeStorage(notification.SasToken, ctx),
            static (in OrganizationStorageProvisioned notification, in (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
                => InitializeStorage(notification.SasToken, ctx),
            static (in OrganizationMemberStorageProvisioned notification, in (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
                => InitializeStorage(notification.SasToken, ctx),
            static (in StorageProvisioningFailed failure, in (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
                => new(OnStorageProvisionedResult.Default(
                    422,
                    ProblemDetails.Build(
                        (ref b) =>
                        {
                            b.Create(
                                status: 422,
                                type: "urn:todo-app:provisioning-failed"u8,
                                title: "Storage provisioning failed"u8);
                        }),
                    ctx.Workspace)),
            static (in ProvisioningNotification _, in (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
                => new(OnStorageProvisionedResult.Default(
                    400,
                    ProblemDetails.Build(
                        (ref b) =>
                        {
                            b.Create(
                                status: 400,
                                type: "urn:todo-app:unrecognized-notification"u8,
                                title: "Unrecognized provisioning notification"u8);
                        }),
                    ctx.Workspace)));
    }

    private static async ValueTask<OnStorageProvisionedResult> InitializeStorage(
        JsonString sasToken,
        (BlobTodoStore Store, JsonWorkspace Workspace, CancellationToken Ct) ctx)
    {
        try
        {
            JsonDocumentBuilder<TodoList.Mutable> empty = BlobTodoStore.CreateEmpty(ctx.Workspace);
            await ctx.Store.WriteAsync(sasToken, empty, default, ctx.Ct);
            return OnStorageProvisionedResult.Ok();
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return OnStorageProvisionedResult.Conflict(
                ProblemDetails.Build(
                    (ref b) =>
                    {
                        b.Create(
                            status: 409,
                            type: "urn:todo-app:storage-already-initialized"u8,
                            title: "Storage already initialized"u8);
                    }),
                ctx.Workspace);
        }
    }
}
