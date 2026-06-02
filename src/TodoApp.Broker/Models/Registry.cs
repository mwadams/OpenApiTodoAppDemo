using Corvus.Text.Json;

namespace TodoApp.Broker.Models;

[JsonSchemaTypeGenerator("../Schemas/registry-schema.json#/$defs/Registry")]
public readonly partial struct Registry;
