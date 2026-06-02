using System.Net.Http.Headers;
using System.Security.Claims;
using Corvus.Text.Json;
using TodoApp.Broker.Frontend;
using TodoApp.Broker.Frontend.Models;
using TodoApp.Broker.Services;

namespace TodoApp.Broker.Handlers;

/// <summary>
/// Proxies requests from the frontend to the back-end API, injecting SAS tokens.
/// For todo operations, generates a SAS token and forwards via the typed client.
/// For entity creation, forwards directly to the API (which will call back for provisioning).
/// </summary>
public sealed class ProxyHandler : IApiProxyHandler
{
    private readonly BlobStorageService _storage;
    private readonly HttpClient _apiClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ProxyHandler(
        BlobStorageService storage,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor)
    {
        _storage = storage;
        _apiClient = httpClientFactory.CreateClient("BackendApi");
        _httpContextAccessor = httpContextAccessor;
    }

    // ─── User Todo Endpoints ─────────────────────────────────────────────

    public async ValueTask<ProxyListUserTodosResult> HandleProxyListUserTodosAsync(
        ProxyListUserTodosParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string userId = (string)parameters.UserId;
        string sasToken = GenerateUserTodoSas(userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/users/{userId}/todos");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoList.Mutable>(response, workspace, cancellationToken);
        return ProxyListUserTodosResult.Ok((TodoList)body, workspace);
    }

    public async ValueTask<ProxyCreateUserTodoResult> HandleProxyCreateUserTodoAsync(
        ProxyCreateUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string userId = (string)parameters.UserId;
        string sasToken = GenerateUserTodoSas(userId);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/users/{userId}/todos");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);
        request.Content = SerializeBody(parameters.Body);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyCreateUserTodoResult.Created((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyGetUserTodoResult> HandleProxyGetUserTodoAsync(
        ProxyGetUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateUserTodoSas(userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ProxyGetUserTodoResult.NotFound(BuildNotFound(workspace), workspace);
        }

        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyGetUserTodoResult.Ok((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyUpdateUserTodoResult> HandleProxyUpdateUserTodoAsync(
        ProxyUpdateUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateUserTodoSas(userId);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);
        request.Content = SerializeBody(parameters.Body);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyUpdateUserTodoResult.Ok((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyDeleteUserTodoResult> HandleProxyDeleteUserTodoAsync(
        ProxyDeleteUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateUserTodoSas(userId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        return ProxyDeleteUserTodoResult.NoContent();
    }

    // ─── Org User Todo Endpoints ─────────────────────────────────────────

    public async ValueTask<ProxyListOrgUserTodosResult> HandleProxyListOrgUserTodosAsync(
        ProxyListOrgUserTodosParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        string userId = (string)parameters.UserId;
        string sasToken = GenerateOrgUserTodoSas(orgId, userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/organizations/{orgId}/users/{userId}/todos");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoList.Mutable>(response, workspace, cancellationToken);
        return ProxyListOrgUserTodosResult.Ok((TodoList)body, workspace);
    }

    public async ValueTask<ProxyCreateOrgUserTodoResult> HandleProxyCreateOrgUserTodoAsync(
        ProxyCreateOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        string userId = (string)parameters.UserId;
        string sasToken = GenerateOrgUserTodoSas(orgId, userId);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/organizations/{orgId}/users/{userId}/todos");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);
        request.Content = SerializeBody(parameters.Body);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyCreateOrgUserTodoResult.Created((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyGetOrgUserTodoResult> HandleProxyGetOrgUserTodoAsync(
        ProxyGetOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateOrgUserTodoSas(orgId, userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/organizations/{orgId}/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ProxyGetOrgUserTodoResult.NotFound(BuildNotFound(workspace), workspace);
        }

        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyGetOrgUserTodoResult.Ok((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyUpdateOrgUserTodoResult> HandleProxyUpdateOrgUserTodoAsync(
        ProxyUpdateOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateOrgUserTodoSas(orgId, userId);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/organizations/{orgId}/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);
        request.Content = SerializeBody(parameters.Body);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<TodoItem.Mutable>(response, workspace, cancellationToken);
        return ProxyUpdateOrgUserTodoResult.Ok((TodoItem)body, workspace);
    }

    public async ValueTask<ProxyDeleteOrgUserTodoResult> HandleProxyDeleteOrgUserTodoAsync(
        ProxyDeleteOrgUserTodoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        string userId = (string)parameters.UserId;
        string todoId = (string)parameters.TodoId;
        string sasToken = GenerateOrgUserTodoSas(orgId, userId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/organizations/{orgId}/users/{userId}/todos/{todoId}");
        request.Headers.Add("X-Storage-Sas-Token", sasToken);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        return ProxyDeleteOrgUserTodoResult.NoContent();
    }

    // ─── Entity Creation Proxies (forwarded with catalog SAS + identity) ─

    public async ValueTask<ProxyCreateUserResult> HandleProxyCreateUserAsync(
        ProxyCreateUserParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        if (_httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated != true)
        {
            return ProxyCreateUserResult.Default(
                401,
                ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:not-authenticated"u8, status: 401)),
                workspace);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/users");
        request.Content = SerializeBody(parameters.Body);
        AddCatalogHeaders(request);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Accepted => ProxyCreateUserResult.Accepted(
                (User)await ParseResponseBodyAsync<User.Mutable>(response, workspace, cancellationToken),
                workspace),
            System.Net.HttpStatusCode.OK => ProxyCreateUserResult.Ok(
                (User)await ParseResponseBodyAsync<User.Mutable>(response, workspace, cancellationToken),
                workspace),
            System.Net.HttpStatusCode.BadRequest => ProxyCreateUserResult.BadRequest(
                ProblemDetails.From(await ParseResponseBodyAsync<ProblemDetails.Mutable>(response, workspace, cancellationToken, ensureSuccessStatusCode: false)),
                workspace),
            System.Net.HttpStatusCode.Conflict => ProxyCreateUserResult.Conflict(
                ProblemDetails.From(await ParseResponseBodyAsync<ProblemDetails.Mutable>(response, workspace, cancellationToken, ensureSuccessStatusCode: false)),
                workspace),
            _ => ProxyCreateUserResult.Default(
                (int)response.StatusCode,
                ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:api-error"u8, status: (int)response.StatusCode)),
                workspace),
        };
    }

    public async ValueTask<ProxyCreateOrganizationResult> HandleProxyCreateOrganizationAsync(
        ProxyCreateOrganizationParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/organizations");
        request.Content = SerializeBody(parameters.Body);
        AddCatalogHeaders(request);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        var body = await ParseResponseBodyAsync<Organization.Mutable>(response, workspace, cancellationToken);
        return ProxyCreateOrganizationResult.Accepted((Organization)body, workspace);
    }

    public async ValueTask<ProxyAddOrganizationMemberResult> HandleProxyAddOrganizationMemberAsync(
        ProxyAddOrganizationMemberParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string orgId = (string)parameters.OrgId;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/organizations/{orgId}/members");
        request.Content = SerializeBody(parameters.Body);
        AddCatalogHeaders(request);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            return ProxyAddOrganizationMemberResult.Default(
                status,
                ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:api-error"u8, status: status)),
                workspace);
        }

        var body = await ParseResponseBodyAsync<OrganizationMember.Mutable>(response, workspace, cancellationToken);
        return ProxyAddOrganizationMemberResult.Ok((OrganizationMember)body, workspace);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void AddCatalogHeaders(HttpRequestMessage request)
    {
        string catalogSas = _storage.GenerateBlobSasUri("catalog", "catalog.json");
        request.Headers.Add("X-Storage-Sas-Token", catalogSas);

        // Read identity from the authenticated principal (set by middleware)
        var principal = _httpContextAccessor.HttpContext?.User;
        string subject = principal?.Identity?.IsAuthenticated == true
            ? principal.FindFirstValue("sub") ?? "system"
            : "system";

        request.Headers.Add("X-Identity-Subject", subject);
    }

    private string GenerateUserTodoSas(string userId)
    {
        return _storage.GenerateBlobSasUri(
            BlobStorageService.UserContainerName(userId), "todos.json");
    }

    private string GenerateOrgUserTodoSas(string orgId, string userId)
    {
        return _storage.GenerateBlobSasUri(
            BlobStorageService.OrgMemberContainerName(userId, orgId), "todos.json");
    }

    private static StreamContent SerializeBody(JsonElement value)
    {
        var stream = new MemoryStream();
        using (var writer = new Corvus.Text.Json.Utf8JsonWriter(stream))
        {
            value.WriteTo(writer);
        }

        stream.Position = 0;
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static async Task<TMutable> ParseResponseBodyAsync<TMutable>(
        HttpResponseMessage response,
        JsonWorkspace workspace,
        CancellationToken ct,
        bool ensureSuccessStatusCode = true)
        where TMutable : struct, Corvus.Text.Json.Internal.IMutableJsonElement<TMutable>
    {
        if (ensureSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var builder = JsonDocumentBuilder<TMutable>.Parse(workspace, stream);
        return builder.RootElement;
    }

    private static ProblemDetails.Source BuildNotFound(JsonWorkspace workspace)
    {
        return ProblemDetails.Build(
            static (ref b) =>
            {
                b.Create(
                    status: 404,
                    type: "urn:todo-app:not-found"u8,
                    title: "Not found"u8);
            });
    }
}
