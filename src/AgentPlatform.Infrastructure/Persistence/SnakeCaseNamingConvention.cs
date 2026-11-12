using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgentPlatform.Infrastructure.Persistence;

/// <summary>
/// Приведение имён таблиц/столбцов/ограничений к snake_case, как в DDL.
/// Применяется в конце OnModelCreating, поэтому явные имена индексов
/// (ix_*, ux_*) остаются без изменений.
/// </summary>
internal static class SnakeCaseNamingConvention
{
    public static void Apply(IMutableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (!string.IsNullOrEmpty(columnName))
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            foreach (var key in entityType.GetKeys())
            {
                var name = key.GetName();
                if (!string.IsNullOrEmpty(name))
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var name = foreignKey.GetConstraintName();
                if (!string.IsNullOrEmpty(name))
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entityType.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (!string.IsNullOrEmpty(name))
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>LlmConfig -> llm_config, GraphSignatureHash -> graph_signature_hash.</summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                var needsSeparator = i > 0
                    && (char.IsLower(previous) || char.IsDigit(previous)
                        || (char.IsUpper(previous) && char.IsLower(next)));

                if (needsSeparator)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
