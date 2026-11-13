using System.Globalization;
using System.Text;

namespace AgentPlatform.Knowledge.Search;

/// <summary>Форматирование вектора в литерал pgvector: <c>[0.1,0.2,...]</c>.</summary>
internal static class VectorLiteral
{
    public static string Format(ReadOnlySpan<float> vector)
    {
        var builder = new StringBuilder(vector.Length * 10);
        builder.Append('[');

        for (var index = 0; index < vector.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[index].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }
}
