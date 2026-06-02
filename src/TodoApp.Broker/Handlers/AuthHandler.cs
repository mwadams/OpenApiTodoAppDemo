using System.Security.Claims;
using Corvus.Text.Json;
using TodoApp.Broker.Frontend;
using TodoApp.Broker.Frontend.Models;
using TodoApp.Broker.Services;

namespace TodoApp.Broker.Handlers;

/// <summary>
/// Handles authentication endpoints by proxying to the Identity service
/// and managing session cookies.
/// </summary>
public sealed class AuthHandler : IApiAuthHandler
{
    private readonly HttpClient _identityClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthHandler> _logger;

    public AuthHandler(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor, ILogger<AuthHandler> logger)
    {
        _identityClient = httpClientFactory.CreateClient("Identity");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async ValueTask<RegisterResult> HandleRegisterAsync(
        RegisterParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var body = new
        {
            email = (string)parameters.Body.Email,
            displayName = parameters.Body.DisplayName.IsUndefined() ? null : (string?)parameters.Body.DisplayName,
            password = (string)parameters.Body.Password,
        };

        using var response = await _identityClient.PostAsJsonAsync("/register", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return RegisterResult.Default(
                (int)response.StatusCode,
                ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:registration-failed"u8,
                    status: (int)response.StatusCode)),
                workspace);
        }

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        string cookieValue = $"{ApiEndpointRegistration.SecuritySchemes.CookieAuthKeyName}={result!.Token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=86400";

        return RegisterResult.Ok(
            workspace,
            setCookie: cookieValue);
    }

    public async ValueTask<LoginResult> HandleLoginAsync(
        LoginParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var body = new
        {
            email = (string)parameters.Body.Email,
            password = (string)parameters.Body.Password,
        };

        using var response = await _identityClient.PostAsJsonAsync("/login", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return LoginResult.Default(
                401,
                ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:invalid-credentials"u8,
                    status: 401)),
                workspace);
        }

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        string cookieValue = $"{ApiEndpointRegistration.SecuritySchemes.CookieAuthKeyName}={result!.Token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=86400";

        return LoginResult.Ok(
            workspace,
            setCookie: cookieValue);
    }

    public ValueTask<LogoutResult> HandleLogoutAsync(
        LogoutParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string cookieValue = $"{ApiEndpointRegistration.SecuritySchemes.CookieAuthKeyName}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0";
        return ValueTask.FromResult(
            LogoutResult.Ok(
                workspace,
                setCookie: cookieValue));
    }

    public ValueTask<GetAuthenticatedUserResult> HandleGetAuthenticatedUserAsync(
        GetAuthenticatedUserParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                GetAuthenticatedUserResult.Unauthorized(
                    body: ProblemDetails.Build((ref b) => b.Create(
                        type: "urn:todo-app:not-authenticated"u8,
                        status: 401)),
                    workspace: workspace));
        }

        return ValueTask.FromResult(
            GetAuthenticatedUserResult.Ok(
                body: AuthUser.Build((ref b) => b.Create(
                    sub: principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? "",
                    email: principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email") ?? "",
                    name: principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue("name") ?? "")),
                workspace: workspace));
    }

    /// <summary>
    /// Gets the authenticated subject from the current HttpContext, or null if not authenticated.
    /// </summary>
    internal string? GetAuthenticatedSubject()
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
    }

    private record TokenResponse(string Token);
}
