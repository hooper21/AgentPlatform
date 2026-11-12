using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.Infrastructure;

/// <summary>
/// Инварианты сохранения: метки времени интерсептором, append-only tool_audit,
/// REQUIRED-валидация gate_payload и rules, tenant_id на вставке.
/// </summary>
public sealed class PersistenceInvariantTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task SaveChanges_sets_created_at_and_updated_at_via_interceptor()
    {
        using var database = TestDatabase.Create();

        var agent = new Agent { TenantId = TenantId, Name = "audited-agent" };
        database.Context.Agents.Add(agent);
        await database.Context.SaveChangesAsync();

        Assert.NotEqual(default, agent.CreatedAt);
        Assert.NotEqual(default, agent.UpdatedAt);
        var createdAt = agent.CreatedAt;

        agent.Name = "audited-agent-renamed";
        await database.Context.SaveChangesAsync();

        Assert.Equal(createdAt, agent.CreatedAt);
        Assert.True(agent.UpdatedAt >= createdAt);
    }

    [Fact]
    public async Task ToolAudit_is_append_only()
    {
        using var database = TestDatabase.Create();

        var entry = new ToolAuditEntry
        {
            TenantId = TenantId,
            SessionId = Guid.NewGuid(),
            ToolName = "echo_read",
            InstallationId = Guid.NewGuid(),
            ArgsMasked = "{}",
            Outcome = "ok",
            TraceId = "trace-1"
        };

        database.Context.ToolAudit.Add(entry);
        await database.Context.SaveChangesAsync();

        entry.Outcome = "error";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Context.SaveChangesAsync());

        Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_with_invalid_gate_payload_is_rejected()
    {
        using var database = TestDatabase.Create();

        database.Context.Approvals.Add(new Approval
        {
            TenantId = TenantId,
            SessionId = Guid.NewGuid(),
            GatePayload = """{"tool":"echo_write"}"""
        });

        await Assert.ThrowsAsync<JsonValidationException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Approval_with_valid_gate_payload_is_saved()
    {
        using var database = TestDatabase.Create();

        database.Context.Approvals.Add(new Approval
        {
            TenantId = TenantId,
            SessionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            GatePayload = """
                {
                  "tool": "echo_write",
                  "args_masked": {"message": "***"},
                  "human_description": "Отправить сообщение",
                  "session_id": "44444444-4444-4444-4444-444444444444",
                  "created_at": "2026-01-01T10:00:00+00:00"
                }
                """
        });

        await database.Context.SaveChangesAsync();
        Assert.Equal(1, await database.Context.Approvals.CountAsync());
    }

    [Fact]
    public async Task AgentVersion_with_unknown_rules_property_is_rejected()
    {
        using var database = TestDatabase.Create();

        database.Context.AgentVersions.Add(new AgentVersion
        {
            AgentId = Guid.NewGuid(),
            Version = 1,
            Status = "draft",
            SystemPrompt = "prompt",
            ModelRef = """{"model":"platform-default"}""",
            EnabledTools = "[]",
            Rules = """{"unknown_key":true}"""
        });

        await Assert.ThrowsAsync<JsonValidationException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_entity_without_tenant_id_is_rejected_on_insert()
    {
        using var database = TestDatabase.Create();

        database.Context.Agents.Add(new Agent { Name = "no-tenant" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Context.SaveChangesAsync());

        Assert.Contains("tenant_id is required", exception.Message, StringComparison.Ordinal);
    }
}
