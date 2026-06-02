using System.Security.Claims;
using Corvus.Text.Json;
using TodoApp.Broker.Frontend;
using TodoApp.Broker.Frontend.Models;
using TodoApp.Broker.Services;

namespace TodoApp.Broker.Handlers;

/// <summary>
/// Handles gateway endpoints: reads identity from the authenticated principal,
/// generates SAS tokens, and proxies to the API.
/// </summary>
public sealed class GatewayHandler : IApiGatewayHandler
{
    private readonly BlobStorageService _storage;
    private readonly HttpClient _apiClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<GatewayHandler> _logger;

    public GatewayHandler(
        BlobStorageService storage,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<GatewayHandler> logger)
    {
        _storage = storage;
        _apiClient = httpClientFactory.CreateClient("BackendApi");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async ValueTask<ProxyGetCurrentUserResult> HandleProxyGetCurrentUserAsync(
        ProxyGetCurrentUserParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ProxyGetCurrentUserResult.Unauthorized(
                body: ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:not-authenticated"u8, status: 401)),
                workspace: workspace);
        }

        string sub = principal.FindFirstValue("sub") ?? "";
        string catalogSas = _storage.GenerateBlobSasUri("catalog", "catalog.json");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("X-Storage-Sas-Token", catalogSas);
        request.Headers.Add("X-Identity-Subject", sub);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ProxyGetCurrentUserResult.Default(
                (int)response.StatusCode,
                ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:user-not-found"u8, status: (int)response.StatusCode)),
                workspace);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(responseBytes);
        var root = doc.RootElement;

        string id = root.GetProperty("id").GetString() ?? sub;
        string displayName = root.GetProperty("displayName").GetString() ?? "";
        string email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";

        return ProxyGetCurrentUserResult.Ok(
            body: UserProfile.Build((ref b) => b.Create(
                displayName: displayName,
                id: id,
                organizations: OrganizationMembershipList.Build((ref ab) => { }),
                email: email)),
            workspace: workspace);
    }

    public async ValueTask<ProxyListCurrentUserOrganizationsResult> HandleProxyListCurrentUserOrganizationsAsync(
        ProxyListCurrentUserOrganizationsParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ProxyListCurrentUserOrganizationsResult.Unauthorized(
                body: ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:not-authenticated"u8, status: 401)),
                workspace: workspace);
        }

        string sub = principal.FindFirstValue("sub") ?? "";
        string catalogSas = _storage.GenerateBlobSasUri("catalog", "catalog.json");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/organizations");
        request.Headers.Add("X-Storage-Sas-Token", catalogSas);
        request.Headers.Add("X-Identity-Subject", sub);

        using var response = await _apiClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ProxyListCurrentUserOrganizationsResult.Ok(
                body: OrganizationMembershipList.Build((ref b) => { }),
                workspace: workspace);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var builder = JsonDocumentBuilder<OrganizationMembershipList.Mutable>.Parse(
            workspace, responseBytes.AsSpan());
        return ProxyListCurrentUserOrganizationsResult.Ok(
            body: (OrganizationMembershipList)builder.RootElement, workspace: workspace);
    }
}
