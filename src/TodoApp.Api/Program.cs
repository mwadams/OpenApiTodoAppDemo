using Corvus.Text.Json.OpenApi;
using Corvus.Text.Json.OpenApi.HttpTransport;
using TodoApp.Api.BrokerClient;
using TodoApp.Api.Handlers;

var builder = WebApplication.CreateBuilder(args);

// Health checks for Aspire orchestration
builder.Services.AddHealthChecks();

// Register services
builder.Services.AddSingleton<BlobTodoStore>();
builder.Services.AddSingleton<DirectoryStore>();

// HttpClient for calling the request broker
builder.Services.AddHttpClient("Broker", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["services:broker:http:0"]
        ?? builder.Configuration["Services:Broker:Url"]
        ?? "http://localhost:5200");
});

// Register the typed provisioning client backed by the HttpClient transport
builder.Services.AddSingleton<IApiProvisioningClient>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("Broker");
    var transport = new HttpClientTransport(httpClient);
    return new ApiProvisioningClient(transport);
});

var app = builder.Build();

// Create handler instances
var blobStore = app.Services.GetRequiredService<BlobTodoStore>();
var directoryStore = app.Services.GetRequiredService<DirectoryStore>();
var provisioningClient = app.Services.GetRequiredService<IApiProvisioningClient>();

var userHandler = new UserHandler(provisioningClient, directoryStore);
var orgHandler = new OrganizationHandler(provisioningClient, directoryStore);
var todoHandler = new TodoHandler(blobStore);
var callbackHandler = new CallbackHandler(blobStore);

// Wire generated endpoints from both specs
TodoApp.Api.Server.ApiEndpointRegistration.MapApiEndpoints(app, todoHandler, callbackHandler);
TodoApp.Api.Directory.ApiEndpointRegistration.MapApiEndpoints(app, userHandler, orgHandler);

// Manual endpoint: lookup user by email in the catalog
app.MapGet("/users/lookup", async (string email, HttpContext context, DirectoryStore dirStore) =>
{
    string? sasUrl = context.Request.Headers["X-Storage-Sas-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(sasUrl))
    {
        return Results.BadRequest(new { error = "Missing X-Storage-Sas-Token header" });
    }

    using var workspace = Corvus.Text.Json.JsonWorkspace.CreateUnrented();
    var (dirBuilder, _) = await dirStore.ReadMutableAsync(sasUrl, workspace, context.RequestAborted);
    var directory = dirBuilder.RootElement;

    foreach (var user in directory.Users.EnumerateArray())
    {
        if (((string)user.Email).Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { id = (string)user.Id, displayName = (string)user.DisplayName, email = (string)user.Email });
        }
    }

    return Results.NotFound(new { error = "User not found" });
});

// Manual endpoint: list members of an organization
app.MapGet("/organizations/{orgId}/members", async (string orgId, HttpContext context, DirectoryStore dirStore) =>
{
    string? sasUrl = context.Request.Headers["X-Storage-Sas-Token"].FirstOrDefault();
    if (string.IsNullOrEmpty(sasUrl))
    {
        return Results.BadRequest(new { error = "Missing X-Storage-Sas-Token header" });
    }

    using var workspace = Corvus.Text.Json.JsonWorkspace.CreateUnrented();
    var (dirBuilder, _) = await dirStore.ReadMutableAsync(sasUrl, workspace, context.RequestAborted);
    var directory = dirBuilder.RootElement;

    // Build a userId→displayName lookup
    var userNames = new Dictionary<string, (string DisplayName, string Email)>();
    foreach (var user in directory.Users.EnumerateArray())
    {
        userNames[(string)user.Id] = ((string)user.DisplayName, (string)user.Email);
    }

    // Find all memberships for this org
    var members = new List<object>();
    foreach (var m in directory.Memberships.EnumerateArray())
    {
        if (m.OrgId.ValueEquals(orgId))
        {
            string mUserId = (string)m.UserId;
            var (displayName, email) = userNames.GetValueOrDefault(mUserId, ("Unknown", ""));
            members.Add(new { userId = mUserId, displayName, email, role = (string)m.Role });
        }
    }

    return Results.Ok(members);
});

// Manual endpoint: remove a member from an organization (admin-only)
app.MapDelete("/organizations/{orgId}/members/{userId}", async (string orgId, string userId, HttpContext context, DirectoryStore dirStore) =>
{
    string? sasUrl = context.Request.Headers["X-Storage-Sas-Token"].FirstOrDefault();
    string? requestingSub = context.Request.Headers["X-Identity-Subject"].FirstOrDefault();
    if (string.IsNullOrEmpty(sasUrl) || string.IsNullOrEmpty(requestingSub))
    {
        return Results.BadRequest(new { error = "Missing required headers" });
    }

    using var workspace = Corvus.Text.Json.JsonWorkspace.CreateUnrented();
    var (dirBuilder, etag) = await dirStore.ReadMutableAsync(sasUrl, workspace, context.RequestAborted);
    var directory = dirBuilder.RootElement;

    // Verify requester is admin of this org
    bool requesterIsAdmin = false;
    bool membershipExists = false;
    foreach (var m in directory.Memberships.EnumerateArray())
    {
        if (m.OrgId.ValueEquals(orgId))
        {
            if (m.UserId.ValueEquals(requestingSub) && m.Role.ValueEquals("admin"u8))
            {
                requesterIsAdmin = true;
            }
            if (m.UserId.ValueEquals(userId))
            {
                membershipExists = true;
            }
        }
    }

    if (!requesterIsAdmin)
    {
        return Results.Json(new { type = "urn:todo-app:forbidden", status = 403, title = "Only organization admins can remove members" }, statusCode: 403);
    }

    if (!membershipExists)
    {
        return Results.NotFound(new { type = "urn:todo-app:not-found", status = 404, title = "Membership not found" });
    }

    // Prevent admin from removing themselves if they are the last admin
    if (requestingSub == userId)
    {
        int adminCount = 0;
        foreach (var m in directory.Memberships.EnumerateArray())
        {
            if (m.OrgId.ValueEquals(orgId) && m.Role.ValueEquals("admin"u8)) adminCount++;
        }

        if (adminCount <= 1)
        {
            return Results.Json(new { type = "urn:todo-app:forbidden", status = 403, title = "Cannot remove the last admin from an organization" }, statusCode: 403);
        }
    }

    // Rebuild directory without the target membership
    var updatedBuilder = TodoApp.Api.DirectoryStorage.Directory.CreateBuilder(workspace, (ref b) =>
    {
        b.Create(
            users: TodoApp.Api.DirectoryStorage.Directory.DirectoryUserArray.Build((ref ab) =>
            {
                foreach (var existing in directory.Users.EnumerateArray()) ab.AddItem((TodoApp.Api.DirectoryStorage.Directory.DirectoryUser)existing);
            }),
            organizations: TodoApp.Api.DirectoryStorage.Directory.DirectoryOrganizationArray.Build((ref ab) =>
            {
                foreach (var existing in directory.Organizations.EnumerateArray()) ab.AddItem((TodoApp.Api.DirectoryStorage.Directory.DirectoryOrganization)existing);
            }),
            memberships: TodoApp.Api.DirectoryStorage.Directory.DirectoryMembershipArray.Build((ref ab) =>
            {
                foreach (var existing in directory.Memberships.EnumerateArray())
                {
                    // Skip the membership being removed
                    if (existing.OrgId.ValueEquals(orgId) && existing.UserId.ValueEquals(userId)) continue;
                    ab.AddItem((TodoApp.Api.DirectoryStorage.Directory.DirectoryMembership)existing);
                }
            }));
    });

    await dirStore.WriteAsync(sasUrl, updatedBuilder, etag, context.RequestAborted);
    return Results.NoContent();
});

app.MapHealthChecks("/health");

app.Run();
