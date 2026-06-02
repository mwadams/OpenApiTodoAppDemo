using System.Text;
using Corvus.Text.Json.OpenApi.HttpTransport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TodoApp.Broker.ApiClient;
using TodoApp.Broker.Handlers;
using TodoApp.Broker.Services;

var builder = WebApplication.CreateBuilder(args);

// Health checks for Aspire orchestration
builder.Services.AddHealthChecks();

// Authentication: validate JWT from the todo-session cookie
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var signingKey = builder.Configuration["Jwt:SigningKey"] ?? "default-dev-key-for-testing-only!!";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "todo-app-identity",
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
        };

        // Don't map JWT claims to long-form Microsoft claim types
        options.MapInboundClaims = false;

        // Read the token from the cookie instead of the Authorization header
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(
                    TodoApp.Broker.Frontend.ApiEndpointRegistration.SecuritySchemes.CookieAuthKeyName,
                    out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// Azurite connection via Aspire integration
builder.AddAzureBlobServiceClient("blobs");
builder.Services.AddSingleton<BlobStorageService>();

// HTTP clients for calling the back-end API
var apiBaseUrl = builder.Configuration["services:api:http:0"]
    ?? builder.Configuration["ApiBaseUrl"]
    ?? "http://localhost:5100";
builder.Services.AddHttpClient("BackendApi", c => c.BaseAddress = new Uri(apiBaseUrl));

// HTTP client for calling the Identity service
var identityBaseUrl = builder.Configuration["services:identity:http:0"]
    ?? builder.Configuration["IdentityBaseUrl"]
    ?? "http://localhost:5300";
builder.Services.AddHttpClient("Identity", c => c.BaseAddress = new Uri(identityBaseUrl));

// HTTP client for reverse-proxying static content from the WebApp
var webappBaseUrl = builder.Configuration["services:webapp:http:0"]
    ?? builder.Configuration["WebAppBaseUrl"]
    ?? "http://localhost:5200";
builder.Services.AddHttpClient("WebApp", c => c.BaseAddress = new Uri(webappBaseUrl));

// Typed callback client (for provisioning notifications)
builder.Services.AddSingleton<IApiCallbacksClient>(sp =>
    new ApiCallbacksClient(new HttpClientTransport(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("BackendApi"))));

// Provisioning ticket tracker (broker-owned blob storage)
builder.Services.AddSingleton<ProvisioningTracker>();

// Handlers
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TodoApp.Broker.Frontend.IApiAuthHandler, AuthHandler>();
builder.Services.AddSingleton<TodoApp.Broker.Frontend.IApiGatewayHandler, GatewayHandler>();
builder.Services.AddSingleton<TodoApp.Broker.Runtime.IApiProvisioningHandler, ProvisioningHandler>();
builder.Services.AddSingleton<TodoApp.Broker.Frontend.IApiProxyHandler, ProxyHandler>();

var app = builder.Build();

// Authentication & Authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Ensure the registry container/blob exist on startup
var storage = app.Services.GetRequiredService<BlobStorageService>();
await storage.EnsureRegistryExistsAsync(CancellationToken.None);

// Register generated API endpoints (frontend + runtime specs)
var authHandler = app.Services.GetRequiredService<TodoApp.Broker.Frontend.IApiAuthHandler>();
var gatewayHandler = app.Services.GetRequiredService<TodoApp.Broker.Frontend.IApiGatewayHandler>();
var proxyHandler = app.Services.GetRequiredService<TodoApp.Broker.Frontend.IApiProxyHandler>();
var provisioningHandler = app.Services.GetRequiredService<TodoApp.Broker.Runtime.IApiProvisioningHandler>();

TodoApp.Broker.Frontend.ApiEndpointRegistration.MapApiEndpoints(app, authHandler, gatewayHandler, proxyHandler);
TodoApp.Broker.Runtime.ApiEndpointRegistration.MapApiEndpoints(app, provisioningHandler);

app.MapHealthChecks("/health");

// Manual endpoint: lookup user by email in catalog (for "Add Member" UX)
app.MapGet("/api/users/lookup", async (string email, BlobStorageService blobStorage, IHttpClientFactory httpClientFactory) =>
{
    string catalogSas = blobStorage.GenerateBlobSasUri("catalog", "catalog.json");
    var apiClient = httpClientFactory.CreateClient("BackendApi");

    using var request = new HttpRequestMessage(HttpMethod.Get, $"/users/lookup?email={Uri.EscapeDataString(email)}");
    request.Headers.Add("X-Storage-Sas-Token", catalogSas);

    using var response = await apiClient.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        return Results.NotFound(new { error = "User not found" });
    }

    var body = await response.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json");
}).RequireAuthorization();

// Manual endpoint: list members of an organization (proxies to API)
app.MapGet("/api/organizations/{orgId}/members", async (string orgId, BlobStorageService blobStorage, IHttpClientFactory httpClientFactory) =>
{
    string catalogSas = blobStorage.GenerateBlobSasUri("catalog", "catalog.json");
    var apiClient = httpClientFactory.CreateClient("BackendApi");

    using var request = new HttpRequestMessage(HttpMethod.Get, $"/organizations/{orgId}/members");
    request.Headers.Add("X-Storage-Sas-Token", catalogSas);

    using var response = await apiClient.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

// Manual endpoint: remove member from organization (admin-only, proxies to API)
app.MapDelete("/api/organizations/{orgId}/members/{userId}", async (string orgId, string userId, HttpContext context, BlobStorageService blobStorage, IHttpClientFactory httpClientFactory) =>
{
    string catalogSas = blobStorage.GenerateBlobSasUri("catalog", "catalog.json");
    var apiClient = httpClientFactory.CreateClient("BackendApi");

    var principal = context.User;
    string subject = principal?.Identity?.IsAuthenticated == true
        ? (principal.FindFirst("sub")?.Value ?? "system")
        : "system";

    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/organizations/{orgId}/members/{userId}");
    request.Headers.Add("X-Storage-Sas-Token", catalogSas);
    request.Headers.Add("X-Identity-Subject", subject);

    using var response = await apiClient.SendAsync(request, context.RequestAborted);
    if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
    {
        return Results.NoContent();
    }

    var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

// Reverse proxy: forward all unmatched requests to the WebApp (static content)
// Use explicit catch-all pattern instead of default MapFallback which has a :nonfile
// constraint that rejects paths with file extensions (.css, .js, etc.)
app.MapFallback("{*path}", async (HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("WebApp");
    var path = context.Request.Path + context.Request.QueryString;

    using var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), path);
    using var response = await client.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

app.Run();
