namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>Сущность с меткой создания (заполняется EF-интерсептором, не триггером).</summary>
public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Сущность с меткой изменения (заполняется EF-интерсептором, не триггером).</summary>
public interface IHasUpdatedAt
{
    DateTimeOffset UpdatedAt { get; set; }
}
