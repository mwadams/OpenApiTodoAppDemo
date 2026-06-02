using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

Console.WriteLine("Creating app host...");
var appHost = await DistributedApplicationTestingBuilder
    .CreateAsync<Projects.TodoApp_AppHost>(cts.Token);

Console.WriteLine("Building...");
var app = await appHost.BuildAsync(cts.Token);

Console.WriteLine("Starting...");
await app.StartAsync(cts.Token);
Console.WriteLine("Started!");

// List all resources and their endpoints
var model = app.Services.GetRequiredService<Aspire.Hosting.ApplicationModel.DistributedApplicationModel>();
foreach (var resource in model.Resources)
{
    Console.WriteLine($"Resource: {resource.Name} ({resource.GetType().Name})");
    if (resource is Aspire.Hosting.ApplicationModel.IResourceWithEndpoints ewp)
    {
        foreach (var ep in ewp.GetEndpoints())
        {
            try { Console.WriteLine($"  Endpoint: {ep.EndpointName} -> {ep.Url}"); }
            catch (Exception ex) { Console.WriteLine($"  Endpoint: {ep.EndpointName} -> ERROR: {ex.Message}"); }
        }
    }
}

Console.WriteLine("Stopping...");
await app.StopAsync();
await app.DisposeAsync();
Console.WriteLine("Done.");
