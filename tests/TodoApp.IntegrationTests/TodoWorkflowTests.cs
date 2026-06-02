using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TodoApp.IntegrationTests;

/// <summary>
/// End-to-end integration tests for the ToDo application.
/// Uses Aspire testing to spin up the full application stack
/// (Azurite, Broker, API, Identity) and tests through the broker's public endpoints.
/// </summary>
[TestClass]
public sealed class TodoWorkflowTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static DistributedApplication? _app;
    private static HttpClient? _client;

    [ClassInitialize]
    public static async Task SetUp(TestContext _)
    {
        var cts = new CancellationTokenSource(DefaultTimeout);

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.TodoApp_AppHost>(cts.Token);

        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
        });

        _app = await appHost.BuildAsync(cts.Token);
        await _app.StartAsync(cts.Token);

        // Wait for all services to be healthy before creating the client
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("api", cts.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("broker", cts.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("identity", cts.Token);

        _client = _app.CreateHttpClient("broker");
    }

    [ClassCleanup]
    public static async Task TearDown()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ─── Helper: create an authenticated client with session cookie ───────

    /// <summary>
    /// Registers a new user via the Identity service and returns an HttpClient
    /// with the session cookie set for authenticated requests.
    /// </summary>
    private static async Task<(HttpClient Client, string Sub)> CreateAuthenticatedClientAsync(
        string email, string displayName, string password = "Test1234!", bool createDomainUser = true)
    {
        // HttpClientHandler with CookieContainer automatically handles Set-Cookie headers
        var handler = new HttpClientHandler { UseCookies = true };
        var baseAddress = _client!.BaseAddress!;
        var client = new HttpClient(handler) { BaseAddress = baseAddress };

        // Register via /auth/register — this sets the session cookie automatically
        var registerResponse = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            displayName,
            password
        });

        Assert.AreEqual(HttpStatusCode.OK, registerResponse.StatusCode,
            $"Registration failed: {await registerResponse.Content.ReadAsStringAsync()}");

        // The CookieContainer should have captured the Set-Cookie header.
        // Now call /auth/me to verify authentication and get the sub.
        var meResponse = await client.GetAsync("/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, meResponse.StatusCode,
            $"/auth/me failed after registration: {await meResponse.Content.ReadAsStringAsync()}");
        var me = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sub = me.GetProperty("sub").GetString()!;

        if (createDomainUser)
        {
            await CreateDomainUserAsync(client, email, displayName);
        }

        return (client, sub);
    }

    private static async Task<(HttpStatusCode StatusCode, JsonElement Body)> CreateDomainUserAsync(
        HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/users", new { displayName, email });
        Assert.IsTrue(
            response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.OK,
            $"Create domain user failed with {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("provisioningTicket", out var ticketProperty))
        {
            string? ticket = ticketProperty.GetString();
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                await WaitForProvisioningTicketAsync(ticket, client);
            }
        }

        return (response.StatusCode, body);
    }

    // ─── Unauthenticated tests (direct proxy to API) ─────────────────────

    [TestMethod]
    public async Task CreateUser_ReturnsAccepted()
    {
        var (client, sub) = await CreateAuthenticatedClientAsync(
            "alice@test.com", "Alice", createDomainUser: false);
        try
        {
            var (statusCode, body) = await CreateDomainUserAsync(client, "alice@test.com", "Alice");

            Assert.AreEqual(HttpStatusCode.Accepted, statusCode);
            Assert.IsTrue(body.TryGetProperty("id", out var id));
            Assert.AreEqual(sub, id.GetString());

            // Verify provisioning ticket is present
            Assert.IsTrue(body.TryGetProperty("provisioningTicket", out var ticket));
            Assert.IsFalse(string.IsNullOrEmpty(ticket.GetString()));
        }
        finally
        {
            client.Dispose();
        }
    }

    [TestMethod]
    public async Task CreateOrganization_ReturnsAccepted()
    {
        var response = await _client!.PostAsJsonAsync("/api/organizations", new { name = "Acme Corp" });

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected Accepted but got {response.StatusCode}. Body: {errorBody}");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("id", out var id));
        Assert.IsFalse(string.IsNullOrEmpty(id.GetString()));

        // Verify provisioning ticket is present
        Assert.IsTrue(body.TryGetProperty("provisioningTicket", out var ticket));
        Assert.IsFalse(string.IsNullOrEmpty(ticket.GetString()));
    }

    [TestMethod]
    public async Task UserTodoWorkflow_CreateListUpdateDelete()
    {
        var (client, userId) = await CreateAuthenticatedClientAsync("bob@test.com", "Bob");
        try
        {
            // List todos — should be empty
            var listResponse = await client.GetAsync($"/api/users/{userId}/todos");
            Assert.AreEqual(HttpStatusCode.OK, listResponse.StatusCode);
            var todos = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(0, todos.GetArrayLength());

            // Create a todo
            var todoResponse = await client.PostAsJsonAsync($"/api/users/{userId}/todos", new
            {
                title = "Buy groceries",
                description = "Milk, eggs, bread",
                priority = "high",
                tags = new[] { "shopping" }
            });
            Assert.AreEqual(HttpStatusCode.Created, todoResponse.StatusCode);
            var createdTodo = await todoResponse.Content.ReadFromJsonAsync<JsonElement>();
            var todoId = createdTodo.GetProperty("id").GetString()!;
            Assert.AreEqual("Buy groceries", createdTodo.GetProperty("title").GetString());
            Assert.AreEqual("pending", createdTodo.GetProperty("status").GetString());

            // Get the specific todo
            var getResponse = await client.GetAsync($"/api/users/{userId}/todos/{todoId}");
            if (getResponse.StatusCode != HttpStatusCode.OK)
            {
                var errBody = await getResponse.Content.ReadAsStringAsync();
                Assert.Fail($"GET todo returned {getResponse.StatusCode}: {errBody[..Math.Min(errBody.Length, 500)]}");
            }

            // Update the todo
            var updateResponse = await client.PutAsJsonAsync($"/api/users/{userId}/todos/{todoId}", new
            {
                title = "Buy groceries",
                status = "done",
                priority = "high"
            });
            Assert.AreEqual(HttpStatusCode.OK, updateResponse.StatusCode);
            var updatedTodo = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("done", updatedTodo.GetProperty("status").GetString());

            // Delete the todo
            var deleteResponse = await client.DeleteAsync($"/api/users/{userId}/todos/{todoId}");
            Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            // Verify deleted
            var verifyResponse = await client.GetAsync($"/api/users/{userId}/todos/{todoId}");
            Assert.AreEqual(HttpStatusCode.NotFound, verifyResponse.StatusCode);
        }
        finally
        {
            client.Dispose();
        }
    }

    [TestMethod]
    public async Task OrgTodoWorkflow_CreateOrgAddMemberManageTodos()
    {
        var (client, userId) = await CreateAuthenticatedClientAsync("carol@test.com", "Carol");
        try
        {

            // Create organization
            var orgResponse = await client.PostAsJsonAsync("/api/organizations", new { name = "Dev Team" });
            var orgBody = await orgResponse.Content.ReadFromJsonAsync<JsonElement>();
            var orgId = orgBody.GetProperty("id").GetString()!;
            var orgTicket = orgBody.GetProperty("provisioningTicket").GetString()!;
            await WaitForProvisioningTicketAsync(orgTicket, client);

            // Add member (triggers org-user provisioning)
            var memberResponse = await client.PostAsJsonAsync($"/api/organizations/{orgId}/members", new { userId });
            if (!memberResponse.IsSuccessStatusCode)
            {
                var memberErr = await memberResponse.Content.ReadAsStringAsync();
                Assert.Fail($"Add member returned {memberResponse.StatusCode}: {memberErr[..Math.Min(memberErr.Length, 500)]}");
            }
            var memberBody = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
            var memberTicket = memberBody.GetProperty("provisioningTicket").GetString()!;
            await WaitForProvisioningTicketAsync(memberTicket, client);

            // Create org todo
            var todoResponse = await client.PostAsJsonAsync($"/api/organizations/{orgId}/users/{userId}/todos", new
            {
                title = "Deploy v2",
                priority = "critical"
            });
            Assert.AreEqual(HttpStatusCode.Created, todoResponse.StatusCode);

            // List org todos
            var listResponse = await client.GetAsync($"/api/organizations/{orgId}/users/{userId}/todos");
            Assert.AreEqual(HttpStatusCode.OK, listResponse.StatusCode);
            var todos = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(1, todos.GetArrayLength());
        }
        finally
        {
            client.Dispose();
        }
    }

    [TestMethod]
    public async Task GetTodos_NonExistentUser_ReturnsEmptyList()
    {
        var response = await _client!.GetAsync("/api/users/00000000-0000-0000-0000-000000000000/todos");

        // The API returns an empty list even for unprovisioned users
        // (blob doesn't exist → handler returns empty TodoList)
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Authenticated directory tests (via gateway) ─────────────────────

    [TestMethod]
    public async Task AuthenticatedFlow_SignUp_GetProfile_ListOrgs()
    {
        // Register and get authenticated client
        var (client, sub) = await CreateAuthenticatedClientAsync(
            "dave@test.com", "Dave");

        try
        {
            // Get profile
            var profileResponse = await client.GetAsync("/api/me");
            Assert.AreEqual(HttpStatusCode.OK, profileResponse.StatusCode);
            var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("Dave", profile.GetProperty("displayName").GetString());
            Assert.AreEqual(sub, profile.GetProperty("id").GetString());

            // List organizations — should be empty initially
            var orgsResponse = await client.GetAsync("/api/me/organizations");
            Assert.AreEqual(HttpStatusCode.OK, orgsResponse.StatusCode);
            var orgs = await orgsResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(0, orgs.GetArrayLength());
        }
        finally
        {
            client.Dispose();
        }
    }

    [TestMethod]
    public async Task AuthenticatedFlow_CreateOrg_AddSelf_ListOrgsUpdated()
    {
        // Register and get authenticated client
        var (client, sub) = await CreateAuthenticatedClientAsync(
            "eve@test.com", "Eve");

        try
        {
            // Create organization via authenticated proxy
            var orgResponse = await client.PostAsJsonAsync("/api/organizations", new { name = "Eve's Team" });
            Assert.IsTrue(orgResponse.IsSuccessStatusCode,
                $"Create org failed: {await orgResponse.Content.ReadAsStringAsync()}");
            var orgBody = await orgResponse.Content.ReadFromJsonAsync<JsonElement>();
            var orgId = orgBody.GetProperty("id").GetString()!;
            var orgTicket = orgBody.GetProperty("provisioningTicket").GetString()!;

            // Wait for org provisioning
            await WaitForProvisioningTicketAsync(orgTicket, client);

            // Add self as member
            var memberResponse = await client.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = sub, role = "admin" });
            Assert.IsTrue(memberResponse.IsSuccessStatusCode,
                $"Add member failed: {await memberResponse.Content.ReadAsStringAsync()}");
            var memberBody = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
            var memberTicket = memberBody.GetProperty("provisioningTicket").GetString()!;

            // Wait for member provisioning
            await WaitForProvisioningTicketAsync(memberTicket, client);

            // List organizations — should now contain our org
            var orgsResponse = await client.GetAsync("/api/me/organizations");
            Assert.AreEqual(HttpStatusCode.OK, orgsResponse.StatusCode);
            var orgs = await orgsResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(orgs.GetArrayLength() >= 1,
                $"Expected at least 1 org but got {orgs.GetArrayLength()}");

            // Verify the org name is present
            bool foundOrg = false;
            foreach (var org in orgs.EnumerateArray())
            {
                if (org.GetProperty("orgName").GetString() == "Eve's Team")
                {
                    foundOrg = true;
                    Assert.AreEqual("admin", org.GetProperty("role").GetString());
                    break;
                }
            }
            Assert.IsTrue(foundOrg, "Expected to find 'Eve's Team' in organizations list");
        }
        finally
        {
            client.Dispose();
        }
    }

    [TestMethod]
    public async Task Unauthenticated_GetCurrentUser_Returns401()
    {
        var response = await _client!.GetAsync("/api/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task AdminOnly_NonAdminCannotAddMember()
    {
        // Create two authenticated users
        var (adminClient, adminSub) = await CreateAuthenticatedClientAsync(
            "frank-admin@test.com", "Frank");
        var (memberClient, memberSub) = await CreateAuthenticatedClientAsync(
            "grace-member@test.com", "Grace");

        try
        {
            // Admin creates org and adds self as admin (bootstrap — no existing members)
            var orgResponse = await adminClient.PostAsJsonAsync("/api/organizations", new { name = "Frank's Org" });
            Assert.IsTrue(orgResponse.IsSuccessStatusCode);
            var orgBody = await orgResponse.Content.ReadFromJsonAsync<JsonElement>();
            var orgId = orgBody.GetProperty("id").GetString()!;
            await WaitForProvisioningTicketAsync(orgBody.GetProperty("provisioningTicket").GetString()!, adminClient);

            var selfMemberResponse = await adminClient.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = adminSub, role = "admin" });
            Assert.IsTrue(selfMemberResponse.IsSuccessStatusCode,
                $"Bootstrap add-self failed: {await selfMemberResponse.Content.ReadAsStringAsync()}");
            await WaitForProvisioningTicketAsync(
                (await selfMemberResponse.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("provisioningTicket").GetString()!, adminClient);

            // Admin adds Grace as regular member
            var addGraceResponse = await adminClient.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = memberSub, role = "member" });
            Assert.IsTrue(addGraceResponse.IsSuccessStatusCode,
                $"Admin adding member failed: {await addGraceResponse.Content.ReadAsStringAsync()}");
            await WaitForProvisioningTicketAsync(
                (await addGraceResponse.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("provisioningTicket").GetString()!, adminClient);

            // Create a third user
            var (_, thirdSub) = await CreateAuthenticatedClientAsync("hal-third@test.com", "Hal");

            // Grace (regular member) tries to add Hal — should be rejected with 403
            var graceAddsHal = await memberClient.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = thirdSub });
            Assert.AreEqual(HttpStatusCode.Forbidden, graceAddsHal.StatusCode,
                "Non-admin should not be able to add members");
        }
        finally
        {
            adminClient.Dispose();
            memberClient.Dispose();
        }
    }

    [TestMethod]
    public async Task RemoveMember_AdminCanRemoveMember()
    {
        // Create admin and member
        var (adminClient, adminSub) = await CreateAuthenticatedClientAsync(
            "ivan-admin@test.com", "Ivan");
        var (memberClient, memberSub) = await CreateAuthenticatedClientAsync(
            "judy-member@test.com", "Judy");

        try
        {
            // Create org, add admin
            var orgResponse = await adminClient.PostAsJsonAsync("/api/organizations", new { name = "Ivan's Org" });
            var orgBody = await orgResponse.Content.ReadFromJsonAsync<JsonElement>();
            var orgId = orgBody.GetProperty("id").GetString()!;
            await WaitForProvisioningTicketAsync(orgBody.GetProperty("provisioningTicket").GetString()!, adminClient);

            var selfAdd = await adminClient.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = adminSub, role = "admin" });
            await WaitForProvisioningTicketAsync(
                (await selfAdd.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("provisioningTicket").GetString()!, adminClient);

            // Add Judy
            var addJudy = await adminClient.PostAsJsonAsync(
                $"/api/organizations/{orgId}/members", new { userId = memberSub, role = "member" });
            await WaitForProvisioningTicketAsync(
                (await addJudy.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("provisioningTicket").GetString()!, adminClient);

            // Judy tries to remove herself — should fail (not admin)
            var judyRemovesSelf = await memberClient.DeleteAsync(
                $"/api/organizations/{orgId}/members/{memberSub}");
            Assert.AreEqual(HttpStatusCode.Forbidden, judyRemovesSelf.StatusCode,
                "Non-admin should not be able to remove members");

            // Ivan removes Judy — should succeed
            var ivanRemovesJudy = await adminClient.DeleteAsync(
                $"/api/organizations/{orgId}/members/{memberSub}");
            Assert.AreEqual(HttpStatusCode.NoContent, ivanRemovesJudy.StatusCode,
                $"Admin remove member failed: {await ivanRemovesJudy.Content.ReadAsStringAsync()}");

            // Verify Judy is no longer in Ivan's org
            var judyOrgs = await memberClient.GetAsync("/api/me/organizations");
            var judyOrgsList = await judyOrgs.Content.ReadFromJsonAsync<JsonElement>();
            bool judyInOrg = false;
            foreach (var org in judyOrgsList.EnumerateArray())
            {
                if (org.GetProperty("orgId").GetString() == orgId) judyInOrg = true;
            }
            Assert.IsFalse(judyInOrg, "Judy should no longer be in Ivan's org after removal");
        }
        finally
        {
            adminClient.Dispose();
            memberClient.Dispose();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the provisioning status endpoint using the ticket ID until it
    /// reports "ready" or "failed", respecting the Retry-After header hint.
    /// </summary>
    private static Task WaitForProvisioningTicketAsync(string ticket, int maxWaitSeconds = 15)
        => WaitForProvisioningTicketAsync(ticket, _client!, maxWaitSeconds);

    private static async Task WaitForProvisioningTicketAsync(string ticket, HttpClient client, int maxWaitSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/provisioning/status/{ticket}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                $"Status endpoint returned {response.StatusCode} for ticket {ticket}");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var status = body.GetProperty("status").GetString();

            if (status == "ready")
            {
                return;
            }

            if (status == "failed")
            {
                var message = body.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
                Assert.Fail($"Provisioning failed for ticket {ticket}: {message}");
            }

            // Respect the Retry-After header or body hint
            int delayMs = 500;
            if (response.Headers.TryGetValues("Retry-After", out var retryValues) &&
                int.TryParse(retryValues.FirstOrDefault(), out int retrySeconds))
            {
                delayMs = retrySeconds * 1000;
            }
            else if (body.TryGetProperty("retryAfterSeconds", out var retryProp) &&
                     retryProp.TryGetInt32(out int bodyRetry))
            {
                delayMs = bodyRetry * 1000;
            }

            await Task.Delay(delayMs);
        }

        Assert.Fail($"Provisioning did not complete within {maxWaitSeconds}s for ticket {ticket}");
    }
}
