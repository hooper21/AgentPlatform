namespace AgentPlatform.Core.Policy;

/// <summary>
/// Некорректный conditional-предикат гейта (<see cref="Contracts.GatePredicate"/>)
/// или аргументы, к которым его нельзя применить. Runtime трактует такое исключение
/// как срабатывание гейта (fail-closed): ошибка конфигурации не снимает подтверждение.
/// </summary>
public sealed class GatePredicateException : Exception
{
    public GatePredicateException(string message)
        : base(message)
    {
    }
}
