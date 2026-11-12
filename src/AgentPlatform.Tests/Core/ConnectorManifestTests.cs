using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;

namespace AgentPlatform.Tests.Core;

/// <summary>
/// Контракт коннектора: POCO + JSON Schema как артефакт + валидация при установке.
/// </summary>
public sealed class ConnectorManifestTests
{
    private const string InputSchema =
        """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""";

    [Fact]
    public void Valid_manifest_passes_validation()
    {
        ConnectorManifestValidator.Validate(CreateManifest());
    }

    [Fact]
    public void Manifest_round_trips_as_snake_case_jsonb_document()
    {
        var json = PlatformJson.SerializeDb(CreateManifest());

        Assert.Contains("\"requests_per_window\"", json, StringComparison.Ordinal);
        Assert.Contains("\"window_seconds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"input_schema\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cache_classes\"", json, StringComparison.Ordinal);

        var parsed = ConnectorManifestValidator.ParseAndValidate(json);

        Assert.Equal("echo", parsed.Name);
        Assert.Equal("1.0.0", parsed.Version);
        Assert.Equal(ConnectorAuthType.ApiKey, parsed.Auth.Type);
        Assert.Equal(2, parsed.Tools.Count);
        Assert.Equal(ToolPolicy.Read, parsed.Tools[0].Policy);
        Assert.Equal("echo-read", parsed.Tools[0].CacheClass);
        Assert.Equal(0.01m, parsed.Tools[0].CostPerCall);
        Assert.Equal(60, parsed.Limits.RequestsPerWindow);
        Assert.Equal(60, parsed.Limits.WindowSeconds);
        Assert.Equal(5, parsed.Limits.WeightByEndpoint!["echo_write"]);
        Assert.Equal(60, parsed.CacheClasses!["echo-read"]);
    }

    [Fact]
    public void Unknown_manifest_property_is_rejected()
    {
        var json = PlatformJson.SerializeDb(CreateManifest())
            .Replace("\"limits\":", "\"publisher\":\"unknown\",\"limits\":", StringComparison.Ordinal);

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(json));
    }

    [Fact]
    public void Undeclared_cache_class_is_rejected()
    {
        var manifest = CreateManifest() with
        {
            Tools =
            [
                new ConnectorToolManifest("echo_read", "Read tool", InputSchema, ToolPolicy.Read, "not-declared")
            ]
        };

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Cache_class_on_write_tool_is_rejected()
    {
        var manifest = CreateManifest() with
        {
            Tools =
            [
                new ConnectorToolManifest("echo_write", "Write tool", InputSchema, ToolPolicy.Write, "echo-read")
            ]
        };

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Duplicate_tool_names_are_rejected()
    {
        var manifest = CreateManifest() with
        {
            Tools =
            [
                new ConnectorToolManifest("echo_read", "Read tool", InputSchema, ToolPolicy.Read),
                new ConnectorToolManifest("echo_read", "Read tool again", InputSchema, ToolPolicy.Read)
            ]
        };

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Non_positive_limits_are_rejected()
    {
        var manifest = CreateManifest() with { Limits = new ConnectorLimits(0, 60) };

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Invalid_input_schema_is_rejected()
    {
        var manifest = CreateManifest() with
        {
            Tools = [new ConnectorToolManifest("echo_read", "Read tool", "not-json", ToolPolicy.Read)]
        };

        Assert.Throws<JsonValidationException>(() => ConnectorManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Schema_artifact_is_embedded_and_declares_contract_fields()
    {
        var root = JsonNode.Parse(ConnectorManifestSchema.Json)!.AsObject();
        var properties = root["properties"]!.AsObject();

        Assert.Equal("object", root["type"]!.GetValue<string>());
        Assert.True(properties.ContainsKey("name"));
        Assert.True(properties.ContainsKey("version"));
        Assert.True(properties.ContainsKey("description"));
        Assert.True(properties.ContainsKey("auth"));
        Assert.True(properties.ContainsKey("tools"));
        Assert.True(properties.ContainsKey("limits"));
        Assert.True(properties.ContainsKey("cache_classes"));
    }

    private static ConnectorManifest CreateManifest() => new(
        Name: "echo",
        Version: "1.0.0",
        Description: "Test connector",
        Auth: new ConnectorAuth(ConnectorAuthType.ApiKey, ["api_key"]),
        Tools:
        [
            new ConnectorToolManifest(
                "echo_read",
                "Read tool",
                InputSchema,
                ToolPolicy.Read,
                CacheClass: "echo-read",
                CostPerCall: 0.01m),
            new ConnectorToolManifest("echo_write", "Write tool", InputSchema, ToolPolicy.Write)
        ],
        Limits: new ConnectorLimits(
            RequestsPerWindow: 60,
            WindowSeconds: 60,
            WeightByEndpoint: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["echo_read"] = 1,
                ["echo_write"] = 5
            }),
        CacheClasses: new Dictionary<string, int>(StringComparer.Ordinal) { ["echo-read"] = 60 });
}
