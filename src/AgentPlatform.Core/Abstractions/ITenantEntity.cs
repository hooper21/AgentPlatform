namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Сущность, принадлежащая тенанту (столбец tenant_id в БД).
/// Такие сущности доступны только через tenant-фильтрованный репозиторий.
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}
