using Azure;
using Corvus.Text.Json;
using TodoApp.Api.Server;
using TodoApp.Api.Server.Models;

namespace TodoApp.Api.Handlers;

/// <summary>
/// Handles todo CRUD operations for both personal and organization-user todo lists.
/// Works entirely in the CTJ/Corvus type world — no STJ, no string materializations.
/// </summary>
public sealed class TodoHandler : IApiTodosHandler
{
    private readonly BlobTodoStore _store;

    public TodoHandler(BlobTodoStore store)
    {
        _store = store;
    }

    // ─── Personal Todos ───────────────────────────────────────────────────

    public async ValueTask<ListUserTodosResult> HandleListUserTodosAsync(
        ListUserTodosParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return ListUserTodosResult.Ok(
                    body: TodoList.Build(static (ref _) => { }),
                    workspace: workspace);
            }

            return ListUserTodosResult.Ok(body: (TodoList)result.Value.Builder.RootElement, workspace: workspace);
        }
        catch (RequestFailedException ex)
        {
            return ListUserTodosResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<CreateUserTodoResult> HandleCreateUserTodoAsync(
        CreateUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = await ReadOrCreateMutable(
                parameters.XStorageSasToken, workspace, cancellationToken);

            TodoList.Mutable list = builder.RootElement;
            list.AddItem(BuildNewTodoItem(parameters.Body));

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            // Return the newly added item (last in the array)
            TodoItem added = list[list.GetArrayLength() - 1];
            return CreateUserTodoResult.Created(body: added, workspace: workspace);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return CreateUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return CreateUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<GetUserTodoResult> HandleGetUserTodoAsync(
        GetUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return GetUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            if (TryFindItem((TodoList)result.Value.Builder.RootElement, parameters.TodoId, out TodoItem item))
            {
                return GetUserTodoResult.Ok(body: item, workspace: workspace);
            }

            return GetUserTodoResult.NotFound(BuildNotFound(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return GetUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<UpdateUserTodoResult> HandleUpdateUserTodoAsync(
        UpdateUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return UpdateUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = result.Value;

            if (!TryUpdateItem(builder.RootElement, parameters.TodoId, parameters.Body, out TodoItem.Mutable updatedItem))
            {
                return UpdateUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            return UpdateUserTodoResult.Ok(body: (TodoItem)updatedItem, workspace: workspace);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return UpdateUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return UpdateUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<DeleteUserTodoResult> HandleDeleteUserTodoAsync(
        DeleteUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return DeleteUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = result.Value;

            if (!TryRemoveItem(builder.RootElement, parameters.TodoId))
            {
                return DeleteUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            return DeleteUserTodoResult.NoContent();
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return DeleteUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return DeleteUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    // ─── Organization-User Todos ──────────────────────────────────────────

    public async ValueTask<ListOrgUserTodosResult> HandleListOrgUserTodosAsync(
        ListOrgUserTodosParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return ListOrgUserTodosResult.Ok(
                    body: TodoList.Build(static (ref _) => { }),
                    workspace: workspace);
            }

            return ListOrgUserTodosResult.Ok(body: (TodoList)result.Value.Builder.RootElement, workspace: workspace);
        }
        catch (RequestFailedException ex)
        {
            return ListOrgUserTodosResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<CreateOrgUserTodoResult> HandleCreateOrgUserTodoAsync(
        CreateOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = await ReadOrCreateMutable(
                parameters.XStorageSasToken, workspace, cancellationToken);

            TodoList.Mutable list = builder.RootElement;
            list.AddItem(BuildNewTodoItem(parameters.Body));

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            TodoItem added = list[list.GetArrayLength() - 1];
            return CreateOrgUserTodoResult.Created(body: added, workspace: workspace);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return CreateOrgUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return CreateOrgUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<GetOrgUserTodoResult> HandleGetOrgUserTodoAsync(
        GetOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return GetOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            if (TryFindItem((TodoList)result.Value.Builder.RootElement, parameters.TodoId, out TodoItem item))
            {
                return GetOrgUserTodoResult.Ok(body: item, workspace: workspace);
            }

            return GetOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return GetOrgUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<UpdateOrgUserTodoResult> HandleUpdateOrgUserTodoAsync(
        UpdateOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return UpdateOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = result.Value;

            if (!TryUpdateItem(builder.RootElement, parameters.TodoId, parameters.Body, out TodoItem.Mutable updatedItem))
            {
                return UpdateOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            return UpdateOrgUserTodoResult.Ok(body: (TodoItem)updatedItem, workspace: workspace);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return UpdateOrgUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return UpdateOrgUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    public async ValueTask<DeleteOrgUserTodoResult> HandleDeleteOrgUserTodoAsync(
        DeleteOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.ReadMutableAsync(
                parameters.XStorageSasToken, workspace, cancellationToken);

            if (result is null)
            {
                return DeleteOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            (JsonDocumentBuilder<TodoList.Mutable> builder, ETag etag) = result.Value;

            if (!TryRemoveItem(builder.RootElement, parameters.TodoId))
            {
                return DeleteOrgUserTodoResult.NotFound(BuildNotFound(), workspace);
            }

            await _store.WriteAsync(parameters.XStorageSasToken, builder, etag, cancellationToken);

            return DeleteOrgUserTodoResult.NoContent();
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return DeleteOrgUserTodoResult.Conflict(BuildConflict(), workspace);
        }
        catch (RequestFailedException ex)
        {
            return DeleteOrgUserTodoResult.Default(ex.Status, BuildStorageError(ex.Message), workspace);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the blob as a mutable builder, or creates an empty one if the blob doesn't exist.
    /// For new blobs, returns a default ETag so the first write has no If-Match condition.
    /// </summary>
    private async Task<(JsonDocumentBuilder<TodoList.Mutable> Builder, ETag ETag)> ReadOrCreateMutable(
        JsonString sasToken, JsonWorkspace workspace, CancellationToken ct)
    {
        var result = await _store.ReadMutableAsync(sasToken, workspace, ct);
        if (result is not null)
        {
            return result.Value;
        }

        return (BlobTodoStore.CreateEmpty(workspace), default);
    }

    /// <summary>
    /// Finds a TodoItem by ID in an immutable TodoList using zero-allocation UTF-8 comparison.
    /// </summary>
    private static bool TryFindItem(TodoList list, JsonString todoId, out TodoItem foundItem)
    {
        using var utf8Id = todoId.GetUtf8String();
        foreach (TodoItem item in list.EnumerateArray())
        {
            if (item.Id.ValueEquals(utf8Id.Span))
            {
                foundItem = item;
                return true;
            }
        }

        foundItem = default;
        return false;
    }

    /// <summary>
    /// Single-pass: finds the item by ID and applies the update in place.
    /// Returns true if the item was found and updated.
    /// </summary>
    private static bool TryUpdateItem(TodoList.Mutable list, JsonString todoId, TodoUpdate update, out TodoItem.Mutable updatedItem)
    {
        using var utf8Id = todoId.GetUtf8String();
        foreach (TodoItem.Mutable item in list.EnumerateArray())
        {
            if (item.Id.ValueEquals(utf8Id.Span))
            {
                ApplyUpdate(item, update);
                updatedItem = item;
                return true;
            }
        }

        updatedItem = default;
        return false;
    }

    /// <summary>
    /// Single-pass: finds the item by ID and removes it.
    /// Returns true if an item was removed.
    /// </summary>
    private static bool TryRemoveItem(TodoList.Mutable list, JsonString todoId)
    {
        using var utf8Id = todoId.GetUtf8String();
        int i = 0;
        foreach (TodoItem.Mutable item in list.EnumerateArray())
        {
            if (item.Id.ValueEquals(utf8Id.Span))
            {
                list.RemoveAt(i);
                return true;
            }

            i++;
        }

        return false;
    }

    /// <summary>
    /// Builds a new TodoItem.Source from a NewTodo request body.
    /// </summary>
    private static TodoItem.Source BuildNewTodoItem(NewTodo body)
    {
        return TodoItem.Build(
            (ref b) =>
            {
                b.Create(
                    createdAt: DateTimeOffset.UtcNow,
                    id: Guid.NewGuid(),
                    priority: body.Priority.IsUndefined() ? "medium"u8 : (TodoPriority)body.Priority,
                    status: body.Status.IsUndefined() ? "pending"u8 : (TodoStatus)body.Status,
                    title: body.Title,
                    assignee: body.Assignee.IsUndefined() ? default : body.Assignee,
                    description: body.Description.IsUndefined() ? default : body.Description,
                    dueDate: body.DueDate.IsUndefined() ? default : body.DueDate,
                    tags: body.Tags.IsUndefined() ? default : body.Tags);
            });
    }

    /// <summary>
    /// Applies a partial update to a mutable TodoItem using direct property setters.
    /// Only modifies fields that are present in the update payload.
    /// </summary>
    private static void ApplyUpdate(TodoItem.Mutable item, TodoUpdate update)
    {
        if (!update.Title.IsUndefined())
        {
            item.SetTitle(update.Title);
        }

        if (!update.Description.IsUndefined())
        {
            item.SetDescription(update.Description);
        }

        if (!update.Status.IsUndefined())
        {
            item.SetStatus(update.Status);
        }

        if (!update.Priority.IsUndefined())
        {
            item.SetPriority(update.Priority);
        }

        if (!update.DueDate.IsUndefined())
        {
            item.SetDueDate(update.DueDate);
        }

        if (!update.Tags.IsUndefined())
        {
            item.SetTags(update.Tags);
        }

        if (!update.Assignee.IsUndefined())
        {
            item.SetAssignee(update.Assignee);
        }

        item.SetUpdatedAt(DateTimeOffset.UtcNow);
    }

    private static ProblemDetails.Source BuildNotFound()
    {
        return ProblemDetails.Build(
            static (ref b) =>
            {
                b.Create(
                    status: 404,
                    type: "urn:todo-app:not-found"u8,
                    title: "Todo not found"u8,
                    detail: "The requested todo item does not exist"u8);
            });
    }

    private static ProblemDetails.Source BuildConflict()
    {
        return ProblemDetails.Build(
            static (ref b) =>
            {
                b.Create(
                    status: 409,
                    type: "urn:todo-app:conflict"u8,
                    title: "Concurrency conflict"u8,
                    detail: "The resource was modified by another request"u8);
            });
    }

    private static ProblemDetails.Source BuildStorageError(string message)
    {
        return ProblemDetails.Build(
            (ref b) =>
            {
                b.Create(
                    status: 500,
                    type: "urn:todo-app:storage-error"u8,
                    title: "Storage error"u8,
                    detail: message);
            });
    }
}
