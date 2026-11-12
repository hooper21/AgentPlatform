using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>Общие значения по умолчанию, повторяющие DDL задачи 1.1.</summary>
internal static class DbDefaults
{
    public const string JsonObject = "'{}'::jsonb";
    public const string JsonArray = "'[]'::jsonb";
    public const string Timestamp = "now()";

    /// <summary>jsonb-столбец с дефолтом.</summary>
    public static PropertyBuilder<string> IsJsonb(this PropertyBuilder<string> property, string? defaultSql = DbDefaults.JsonObject)
    {
        property.HasColumnType("jsonb").IsRequired();
        if (defaultSql is not null)
        {
            property.HasDefaultValueSql(defaultSql);
        }

        return property;
    }

    /// <summary>timestamptz-столбец с дефолтом now().</summary>
    public static PropertyBuilder<DateTimeOffset> IsTimestamp(this PropertyBuilder<DateTimeOffset> property)
        => property.HasColumnType("timestamptz").HasDefaultValueSql(DbDefaults.Timestamp);

    /// <summary>nullable timestamptz.</summary>
    public static PropertyBuilder<DateTimeOffset?> IsNullableTimestamp(this PropertyBuilder<DateTimeOffset?> property)
        => property.HasColumnType("timestamptz");
}
