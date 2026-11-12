using System.Text.Json;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.LlmGateway;

/// <summary>
/// Демонстрационный клиент модели: детерминированные вызовы echo_write/echo_read
/// для гейт-ревью фазы 1 и защита от повторного вызова после результата/отказа.
/// </summary>
public sealed class DemoChatClientTests
{
    private static readonly AIFunctionDeclaration WriteTool = CreateTool(DemoChatClient.WriteTool);
    private static readonly AIFunctionDeclaration ReadTool = CreateTool(DemoChatClient.ReadTool);

    [Fact]
    public async Task Write_command_requests_echo_write_tool()
    {
        var client = new DemoChatClient();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "system"), new ChatMessage(ChatRole.User, "write: привет")],
            new ChatOptions { Tools = [WriteTool, ReadTool] });

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal(DemoChatClient.WriteTool, call.Name);
        Assert.Equal("привет", call.Arguments!["message"]);
    }

    [Fact]
    public async Task Read_command_requests_echo_read_tool()
    {
        var client = new DemoChatClient();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "read: проверка")],
            new ChatOptions { Tools = [WriteTool, ReadTool] });

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal(DemoChatClient.ReadTool, call.Name);
        Assert.Equal("проверка", call.Arguments!["message"]);
    }

    [Fact]
    public async Task Tool_result_is_reported_and_not_called_again()
    {
        var client = new DemoChatClient();

        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "write: привет"),
                new ChatMessage(ChatRole.Tool, "echo(write): привет")
            ],
            new ChatOptions { Tools = [WriteTool] });

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("Готово: echo(write): привет", response.Text);
    }

    [Fact]
    public async Task Rejection_message_does_not_trigger_tool_again()
    {
        var client = new DemoChatClient();

        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "write: привет"),
                new ChatMessage(ChatRole.System, "Действие echo_write отклонено (operator).")
            ],
            new ChatOptions { Tools = [WriteTool] });

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.NotNull(response.Text);
    }

    [Fact]
    public async Task Plain_message_returns_text_reply()
    {
        var client = new DemoChatClient();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "привет")],
            new ChatOptions { Tools = [WriteTool, ReadTool] });

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("Принято: привет", response.Text, StringComparison.Ordinal);
    }

    private static AIFunctionDeclaration CreateTool(string name)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string"}}}""");

        return AIFunctionFactory.CreateDeclaration(name, $"test tool {name}", schema.RootElement.Clone());
    }
}
