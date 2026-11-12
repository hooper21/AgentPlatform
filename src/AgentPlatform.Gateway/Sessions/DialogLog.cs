using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Persistence.Entities;

namespace AgentPlatform.Gateway.Sessions;

/// <summary>
/// Журнал сообщений диалога (таблица messages) — то, что читают кабинет тенанта и
/// панель оператора. Состояние для модели хранит Runtime в чекпоинтах; здесь
/// фиксируется история для людей и аудита.
/// <para>
/// Свободный текст визитера сохраняется как есть (маскирование ПДн в тексте —
/// требование к ingest/LLM-слою, а не к журналу диалога; аргументы тулов
/// маскируются Policy Engine до записи в approvals).
/// </para>
/// </summary>
public sealed class DialogLog(ITenantScopedRepository<Message> messages)
{
    private readonly ITenantScopedRepository<Message> _messages =
        messages ?? throw new ArgumentNullException(nameof(messages));

    /// <summary>Добавляет сообщение в журнал сессии.</summary>
    public async Task AppendAsync(
        Guid sessionId,
        MessageRole role,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // Ход мог остановиться на гейте: ответа ассистента ещё нет.
            return;
        }

        var entity = new Message
        {
            SessionId = sessionId,
            Role = role.ToDbValue(),
            Content = content,
            Ts = DateTimeOffset.UtcNow
        };

        await _messages.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _messages.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
