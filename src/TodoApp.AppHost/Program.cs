var builder = DistributedApplication.CreateBuilder(args);

// Shared secret for JWT signing
var jwtKey = builder.AddParameter("jwt-signing-key", secret: true);

// Azurite blob storage emulator — ephemeral, starts empty each run
var storage = builder.AddAzureStorage("storage").RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Session));
var blobs = storage.AddBlobs("blobs");

// Identity Provider (toy IdP — owns account credentials)
var identity = builder.AddProject<Projects.TodoApp_Identity>("identity")
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithEnvironment("Jwt__SigningKey", jwtKey)
    .WithHttpHealthCheck("/health");

// Back-end API (stateless, receives SAS tokens from broker)
var api = builder.AddProject<Projects.TodoApp_Api>("api")
    .WithHttpHealthCheck("/health");

// Web frontend (static file server only)
var webapp = builder.AddProject<Projects.TodoApp_WebApp>("webapp")
    .WithHttpHealthCheck("/health");

// Front-end request broker (GATEWAY — sole external entry point)
var broker = builder.AddProject<Projects.TodoApp_Broker>("broker")
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithReference(api)
    .WaitFor(api)
    .WithReference(identity)
    .WithReference(webapp)
    .WaitFor(webapp)
    .WithEnvironment("Jwt__SigningKey", jwtKey)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// API calls broker for provisioning (internal callback)
api.WithReference(broker);

builder.Build().Run();
