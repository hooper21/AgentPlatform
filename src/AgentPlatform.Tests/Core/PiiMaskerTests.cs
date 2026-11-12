using System.Text.Json.Nodes;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Tests.Core;

/// <summary>
/// Маскирование ПДн в аргументах тулов: в approvals/карточку попадают только
/// маскированные значения, исходные аргументы не изменяются.
/// </summary>
public sealed class PiiMaskerTests
{
    [Fact]
    public void Mask_replaces_sensitive_values_recursively()
    {
        var arguments = JsonNode.Parse(
            """
            {
              "message": "отправить счёт",
              "email": "client@example.com",
              "contact": {"phone": "+7 999 000-00-00", "address": "Москва"},
              "items": [{"card": "4111111111111111"}]
            }
            """)!;

        var masked = PiiMasker.Mask(arguments)!;

        Assert.Equal("отправить счёт", masked["message"]!.GetValue<string>());
        Assert.Equal(PiiMasker.MaskedValue, masked["email"]!.GetValue<string>());
        Assert.Equal(PiiMasker.MaskedValue, masked["contact"]!["phone"]!.GetValue<string>());
        Assert.Equal(PiiMasker.MaskedValue, masked["contact"]!["address"]!.GetValue<string>());
        Assert.Equal(PiiMasker.MaskedValue, masked["items"]![0]!["card"]!.GetValue<string>());

        // Исходные аргументы не изменены: они уходят в тул и в чекпоинт.
        Assert.Equal("client@example.com", arguments["email"]!.GetValue<string>());
    }

    [Fact]
    public void MaskJson_keeps_non_sensitive_fields_and_hides_invalid_json()
    {
        Assert.Equal(
            """{"message":"hi","email":"***"}""",
            PiiMasker.MaskJson("""{"message":"hi","email":"a@b.c"}"""));

        Assert.Equal("{}", PiiMasker.MaskJson("not-json"));
        Assert.Equal("{}", PiiMasker.MaskJson(null));
    }
}
