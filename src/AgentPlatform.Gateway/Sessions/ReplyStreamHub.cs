using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentPlatform.Gateway.Contracts;

namespace AgentPlatform.Gateway.Sessions;

/// <summary>
/// Потоки ответов по сессиям для SSE (GET /api/sessions/{sessionId}/stream).
/// <para>
/// Поток — журнал событий: подписчик читает его по индексу, поэтому порядок
/// «POST, затем GET» и «GET, затем POST» работает одинаково (виджет успевает
/// подключиться и после завершения хода — он получит накопленные события).
/// Событие <c>done</c> закрывает поток; завершённые журналы удаляются по TTL.
/// </para>
/// <para>
/// Хранилище — в процессе Gateway (одна реплика). Вынос потока в Redis Pub/Sub
/// для мультиинстансовой установки — отдельная задача (TODO(3.x) contract).
/// </para>
/// </summary>
public sealed class ReplyStreamHub
{
    private readonly ConcurrentDictionary<Guid, SessionStream> _streams = new();
    private readonly TimeSpan _retention;

    /// <summary>Создаёт hub с временем хранения завершённых потоков.</summary>
    /// <param name="retention">Срок хранения завершённого потока (по умолчанию 10 минут).</param>
    public ReplyStreamHub(TimeSpan? retention = null)
    {
        _retention = retention ?? TimeSpan.FromMinutes(10);

        if (_retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }
    }

    /// <summary>
    /// Начинает новый ход в потоке сессии: журнал очищается, поток снова открыт.
    /// Без сброса подписчик, подключившийся между ходами, сразу получил бы done
    /// предыдущего хода и закрыл соединение.
    /// </summary>
    public void StartTurn(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        _streams.GetOrAdd(sessionId, _ => new SessionStream()).Reset();
        PruneExpired();
    }

    /// <summary>Публикует событие хода. Событие <c>done</c> завершает поток.</summary>
    public void Publish(Guid sessionId, ReplyStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        var stream = _streams.GetOrAdd(sessionId, _ => new SessionStream());
        stream.Append(streamEvent);

        if (string.Equals(streamEvent.Type, ReplyStreamEvent.DoneType, StringComparison.Ordinal))
        {
            stream.Complete();
        }

        PruneExpired();
    }

    /// <summary>
    /// Читает события сессии: сначала накопленные, затем новые — до события done
    /// или отмены подписки.
    /// </summary>
    public async IAsyncEnumerable<ReplyStreamEvent> ReadAsync(
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        PruneExpired();

        var stream = _streams.GetOrAdd(sessionId, _ => new SessionStream());
        var index = 0;
        var version = stream.CurrentVersion();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReplyStreamEvent? next;
            Task signal;

            lock (stream.Gate)
            {
                if (version != stream.Version)
                {
                    // Ход начался заново: читаем новый журнал с начала.
                    version = stream.Version;
                    index = 0;
                }

                if (index < stream.Events.Count)
                {
                    next = stream.Events[index];
                    index++;
                    signal = Task.CompletedTask;
                }
                else if (stream.Completed)
                {
                    yield break;
                }
                else
                {
                    next = null;
                    signal = stream.Signal();
                }
            }

            if (next is not null)
            {
                yield return next;
                continue;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void PruneExpired()
    {
        var threshold = DateTimeOffset.UtcNow - _retention;

        foreach (var (sessionId, stream) in _streams)
        {
            if (stream.IsExpired(threshold))
            {
                _streams.TryRemove(sessionId, out _);
            }
        }
    }

    /// <summary>Журнал событий одной сессии.</summary>
    private sealed class SessionStream
    {
        private TaskCompletionSource _signal = NewSignal();

        public object Gate { get; } = new();

        public List<ReplyStreamEvent> Events { get; } = [];

        public bool Completed { get; private set; }

        public DateTimeOffset? CompletedAt { get; private set; }

        /// <summary>Номер хода в потоке: подписчик перечитывает журнал при смене.</summary>
        public int Version { get; private set; }

        /// <summary>Текущая версия журнала (чтение под блокировкой).</summary>
        public int CurrentVersion()
        {
            lock (Gate)
            {
                return Version;
            }
        }

        /// <summary>Сбрасывает журнал для нового хода и будит ожидающих подписчиков.</summary>
        public void Reset()
        {
            lock (Gate)
            {
                Events.Clear();
                Completed = false;
                CompletedAt = null;
                Version++;

                var signal = _signal;
                _signal = NewSignal();
                signal.SetResult();
            }
        }

        /// <summary>Задача, которая завершится при следующем событии.</summary>
        public Task Signal() => _signal.Task;

        public void Append(ReplyStreamEvent streamEvent)
        {
            lock (Gate)
            {
                Events.Add(streamEvent);

                var signal = _signal;
                _signal = NewSignal();
                signal.SetResult();
            }
        }

        public void Complete()
        {
            lock (Gate)
            {
                Completed = true;
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool IsExpired(DateTimeOffset threshold)
        {
            lock (Gate)
            {
                return Completed && CompletedAt is { } completedAt && completedAt < threshold;
            }
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
