using AgentPlatform.Gateway.Contracts;
using AgentPlatform.Gateway.Sessions;

namespace AgentPlatform.Tests.Gateway;

/// <summary>
/// SSE-поток сессии: подписчик получает журнал событий целиком (в том числе после
/// завершения хода) и поток закрывается событием done.
/// </summary>
public sealed class ReplyStreamHubTests
{
    [Fact]
    public async Task Late_subscriber_receives_replayed_events_until_done()
    {
        var hub = new ReplyStreamHub();
        var sessionId = Guid.NewGuid();

        hub.Publish(sessionId, ReplyStreamEvent.Processing("active"));
        hub.Publish(sessionId, ReplyStreamEvent.Text("Готово"));
        hub.Publish(sessionId, ReplyStreamEvent.Done("active", "completed", "Готово"));

        var events = new List<ReplyStreamEvent>();

        await foreach (var streamEvent in hub.ReadAsync(sessionId))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(
            [ReplyStreamEvent.StatusType, ReplyStreamEvent.DeltaType, ReplyStreamEvent.DoneType],
            events.Select(streamEvent => streamEvent.Type));
        Assert.Equal("Готово", events[1].Delta);
        Assert.Equal("completed", events[2].Outcome);
    }

    [Fact]
    public async Task Subscriber_waits_for_new_events_and_ends_with_done()
    {
        var hub = new ReplyStreamHub();
        var sessionId = Guid.NewGuid();
        var events = new List<ReplyStreamEvent>();

        var reader = Task.Run(async () =>
        {
            await foreach (var streamEvent in hub.ReadAsync(sessionId))
            {
                events.Add(streamEvent);
            }
        });

        hub.Publish(sessionId, ReplyStreamEvent.Text("первый чанк"));
        hub.Publish(sessionId, ReplyStreamEvent.Done("active", "completed", "первый чанк"));

        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([ReplyStreamEvent.DeltaType, ReplyStreamEvent.DoneType], events.Select(streamEvent => streamEvent.Type));
    }

    [Fact]
    public async Task New_turn_resets_the_stream_of_the_previous_turn()
    {
        var hub = new ReplyStreamHub();
        var sessionId = Guid.NewGuid();

        hub.StartTurn(sessionId);
        hub.Publish(sessionId, ReplyStreamEvent.Text("старый ответ"));
        hub.Publish(sessionId, ReplyStreamEvent.Done("active", "completed", "старый ответ"));

        hub.StartTurn(sessionId);
        hub.Publish(sessionId, ReplyStreamEvent.Text("новый ответ"));
        hub.Publish(sessionId, ReplyStreamEvent.Done("active", "completed", "новый ответ"));

        var events = await ReadAllAsync(hub, sessionId);

        Assert.Equal([ReplyStreamEvent.DeltaType, ReplyStreamEvent.DoneType], events.Select(streamEvent => streamEvent.Type));
        Assert.Equal("новый ответ", events[0].Delta);
    }

    private static async Task<IReadOnlyList<ReplyStreamEvent>> ReadAllAsync(ReplyStreamHub hub, Guid sessionId)
    {
        var events = new List<ReplyStreamEvent>();

        await foreach (var streamEvent in hub.ReadAsync(sessionId))
        {
            events.Add(streamEvent);
        }

        return events;
    }
}
