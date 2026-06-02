using TodoApp.Identity;
using TodoApp.Identity.Handlers;
using TodoApp.Identity.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

// Azurite connection via Aspire integration
builder.AddAzureBlobServiceClient("blobs");
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<IApiDefaultHandler, IdentityHandler>();

var app = builder.Build();

// Ensure the identity container exists on startup
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<AccountStore>();
    await store.EnsureContainerExistsAsync();
}

app.MapHealthChecks("/health");

// Register generated OpenAPI endpoints
var handler = app.Services.GetRequiredService<IApiDefaultHandler>();
app.MapApiEndpoints(handler);

app.Run();

