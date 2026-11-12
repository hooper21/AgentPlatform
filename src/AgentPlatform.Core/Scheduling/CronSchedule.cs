using System.Diagnostics.CodeAnalysis;

namespace AgentPlatform.Core.Scheduling;

/// <summary>
/// Минимальное cron-выражение из пяти полей:
/// <c>минута час день_месяца месяц день_недели</c>.
/// <para>
/// Поддерживаются <c>*</c>, списки (<c>1,15</c>), диапазоны (<c>1-5</c>) и шаги
/// (<c>*/5</c>, <c>10-20/2</c>). День недели: 0 — воскресенье, 7 — тоже воскресенье.
/// Время всегда UTC: планировщик не зависит от часового пояса процесса.
/// </para>
/// <para>
/// Если день месяца и день недели заданы одновременно (оба не <c>*</c>), используется
/// классическое правило cron: совпадение хотя бы по одному из них.
/// </para>
/// </summary>
public sealed class CronSchedule
{
    /// <summary>Глубина поиска следующего запуска: выражения с несуществующей датой не валидны.</summary>
    private const int MaxSearchDays = 366 * 5;

    private readonly CronField _minutes;
    private readonly CronField _hours;
    private readonly CronField _daysOfMonth;
    private readonly CronField _months;
    private readonly CronField _daysOfWeek;

    private CronSchedule(
        string expression,
        CronField minutes,
        CronField hours,
        CronField daysOfMonth,
        CronField months,
        CronField daysOfWeek)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    /// <summary>Нормализованное (без лишних пробелов) выражение.</summary>
    public string Expression { get; }

    /// <summary>
    /// Разбирает cron-выражение. Ошибка разбора возвращается в <paramref name="error"/>
    /// и пригодна для ответа Controller API.
    /// </summary>
    public static bool TryParse(
        string? expression,
        [NotNullWhen(true)] out CronSchedule? schedule,
        out string? error)
    {
        schedule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "cron expression is required";
            return false;
        }

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = $"cron expression must contain 5 fields (minute hour day-of-month month day-of-week), got {fields.Length}";
            return false;
        }

        if (!CronField.TryParse(fields[0], 0, 59, normalizeSunday: false, out var minutes, out error))
        {
            error = $"minute: {error}";
            return false;
        }

        if (!CronField.TryParse(fields[1], 0, 23, normalizeSunday: false, out var hours, out error))
        {
            error = $"hour: {error}";
            return false;
        }

        if (!CronField.TryParse(fields[2], 1, 31, normalizeSunday: false, out var daysOfMonth, out error))
        {
            error = $"day-of-month: {error}";
            return false;
        }

        if (!CronField.TryParse(fields[3], 1, 12, normalizeSunday: false, out var months, out error))
        {
            error = $"month: {error}";
            return false;
        }

        if (!CronField.TryParse(fields[4], 0, 7, normalizeSunday: true, out var daysOfWeek, out error))
        {
            error = $"day-of-week: {error}";
            return false;
        }

        schedule = new CronSchedule(
            string.Join(' ', fields),
            minutes,
            hours,
            daysOfMonth,
            months,
            daysOfWeek);

        return true;
    }

    /// <summary>
    /// true, если после <paramref name="anchor"/> уже наступил момент запуска,
    /// не позже <paramref name="now"/>. Пропущенные запуски не накапливаются:
    /// расписание выполняется один раз, даже если сервис был остановлен.
    /// </summary>
    public bool IsDue(DateTimeOffset anchor, DateTimeOffset now)
        => TryGetNextOccurrence(anchor, out var next) && next <= now.ToUniversalTime();

    /// <summary>
    /// Ближайший момент запуска строго после <paramref name="after"/>.
    /// false — в ближайшие 5 лет запусков нет (например, 30 февраля).
    /// </summary>
    public bool TryGetNextOccurrence(DateTimeOffset after, out DateTimeOffset next)
    {
        var afterUtc = after.ToUniversalTime().UtcDateTime;

        // Окно поиска начинается со следующей минуты: запуски живут на границе минуты.
        var start = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc);
        if (start <= afterUtc)
        {
            start = start.AddMinutes(1);
        }

        var lastDay = start.Date.AddDays(MaxSearchDays);

        for (var day = start.Date; day <= lastDay; day = day.AddDays(1))
        {
            if (!_months.Contains(day.Month) || !MatchesDay(day))
            {
                continue;
            }

            var firstHour = day == start.Date ? start.Hour : 0;
            for (var hour = firstHour; hour < 24; hour++)
            {
                if (!_hours.Contains(hour))
                {
                    continue;
                }

                var firstMinute = day == start.Date && hour == start.Hour ? start.Minute : 0;
                for (var minute = firstMinute; minute < 60; minute++)
                {
                    if (!_minutes.Contains(minute))
                    {
                        continue;
                    }

                    next = new DateTimeOffset(
                        new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Utc));
                    return true;
                }
            }
        }

        next = default;
        return false;
    }

    private bool MatchesDay(DateTime day)
    {
        var dayOfMonthMatches = _daysOfMonth.Contains(day.Day);
        var dayOfWeekMatches = _daysOfWeek.Contains((int)day.DayOfWeek);

        if (_daysOfMonth.IsWildcard)
        {
            return dayOfWeekMatches;
        }

        if (_daysOfWeek.IsWildcard)
        {
            return dayOfMonthMatches;
        }

        return dayOfMonthMatches || dayOfWeekMatches;
    }

    /// <summary>Одно поле cron: набор допустимых значений.</summary>
    private sealed class CronField
    {
        private readonly HashSet<int> _values;

        private CronField(HashSet<int> values, bool isWildcard)
        {
            _values = values;
            IsWildcard = isWildcard;
        }

        /// <summary>
        /// true только для чистого <c>*</c>: <c>*/5</c> считается ограничением
        /// для правила «день месяца ИЛИ день недели».
        /// </summary>
        public bool IsWildcard { get; }

        public bool Contains(int value) => _values.Contains(value);

        public static bool TryParse(
            string token,
            int min,
            int max,
            bool normalizeSunday,
            [NotNullWhen(true)] out CronField? field,
            out string? error)
        {
            field = null;
            error = null;

            var values = new HashSet<int>();
            var isWildcard = string.Equals(token, "*", StringComparison.Ordinal);

            foreach (var part in token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParsePart(part, min, max, normalizeSunday, values, out error))
                {
                    return false;
                }
            }

            if (values.Count == 0)
            {
                error = "field must contain at least one value";
                return false;
            }

            field = new CronField(values, isWildcard);
            return true;
        }

        private static bool TryParsePart(
            string part,
            int min,
            int max,
            bool normalizeSunday,
            HashSet<int> values,
            out string? error)
        {
            error = null;

            var step = 1;
            var rangePart = part;

            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (part.IndexOf('/', slash + 1) >= 0)
                {
                    error = $"invalid step in '{part}'";
                    return false;
                }

                rangePart = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
                {
                    error = $"invalid step in '{part}'";
                    return false;
                }
            }

            int start;
            int end;

            if (string.Equals(rangePart, "*", StringComparison.Ordinal))
            {
                start = min;
                end = max;
            }
            else if (rangePart.Contains('-', StringComparison.Ordinal))
            {
                var bounds = rangePart.Split('-');
                if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                {
                    error = $"invalid range '{part}'";
                    return false;
                }
            }
            else if (!int.TryParse(rangePart, out start))
            {
                error = $"invalid value '{part}'";
                return false;
            }
            else
            {
                // "5/10" — от 5 до максимума поля с шагом 10 (как в cron).
                end = slash >= 0 ? max : start;
            }

            if (start < min || start > max || end < min || end > max || start > end)
            {
                error = $"value '{part}' is out of range {min}..{max}";
                return false;
            }

            for (var value = start; value <= end; value += step)
            {
                values.Add(normalizeSunday && value == 7 ? 0 : value);
            }

            return true;
        }
    }
}
