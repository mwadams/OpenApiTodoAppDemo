using Corvus.Text.Json;

namespace TodoApp.Identity.Storage;

/// <summary>
/// The root identity store document. Contains all registered user accounts.
/// Generated from Schemas/identity-storage.json via the Corvus source generator.
/// </summary>
[JsonSchemaTypeGenerator("../Schemas/identity-storage.json")]
internal readonly partial struct AccountDirectory;
