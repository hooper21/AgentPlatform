using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Policy;

namespace AgentPlatform.Tests.Policy;

/// <summary>
/// Conditional-предикаты: JSON <c>{"field":"amount","op":"gt","value":50000}</c>
/// применяется к аргументам тула серверно (без исполнения кода и промпта).
/// </summary>
public sealed class GatePredicateEvaluatorTests
{
    private static JsonNode Args(string json) => JsonNode.Parse(json)!;

    [Theory]
    [InlineData(60000, true)]
    [InlineData(50001, true)]
    [InlineData(50000, false)]
    [InlineData(49999, false)]
    public void Gt_compares_amount_with_threshold(int amount, bool expected)
    {
        var predicate = new GatePredicate("amount", "gt", 50_000);

        Assert.Equal(expected, GatePredicateEvaluator.Evaluate(predicate, new JsonObject { ["amount"] = amount }));
    }

    [Fact]
    public void Numeric_string_arguments_are_compared_as_numbers()
    {
        var predicate = new GatePredicate("amount", "gt", 50_000);

        Assert.True(GatePredicateEvaluator.Evaluate(predicate, Args("""{"amount":"60000"}""")));
        Assert.False(GatePredicateEvaluator.Evaluate(predicate, Args("""{"amount":"10000"}""")));
    }

    [Fact]
    public void Gte_includes_boundary_and_lt_lte_exclude_it()
    {
        var arguments = Args("""{"amount":50000}""");

        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "gte", 50_000), arguments));
        Assert.False(GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "gt", 50_000), arguments));
        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "lte", 50_000), arguments));
        Assert.False(GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "lt", 50_000), arguments));
    }

    [Fact]
    public void Eq_and_neq_support_strings_and_booleans()
    {
        var arguments = Args("""{"status":"paid","urgent":true}""");

        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("status", "eq", "paid"), arguments));
        Assert.False(GatePredicateEvaluator.Evaluate(new GatePredicate("status", "eq", "Paid"), arguments));
        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("status", "neq", "draft"), arguments));
        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("urgent", "eq", true), arguments));
    }

    [Fact]
    public void In_and_contains_operators_work_with_arrays()
    {
        var arguments = Args("""{"status":"paid","tags":["vip","new"],"comment":"срочный заказ"}""");

        Assert.True(GatePredicateEvaluator.Evaluate(
            new GatePredicate("status", "in", new JsonArray("paid", "shipped")),
            arguments));
        Assert.False(GatePredicateEvaluator.Evaluate(
            new GatePredicate("status", "in", new JsonArray("draft", "cancelled")),
            arguments));

        Assert.True(GatePredicateEvaluator.Evaluate(
            new GatePredicate("tags", "contains", "vip"),
            arguments));
        Assert.True(GatePredicateEvaluator.Evaluate(
            new GatePredicate("comment", "contains", "срочный"),
            arguments));
    }

    [Fact]
    public void Nested_field_and_case_insensitive_keys_are_supported()
    {
        var arguments = Args("""{"Order":{"Amount":75000}}""");

        Assert.True(GatePredicateEvaluator.Evaluate(new GatePredicate("order.amount", "gt", 50_000), arguments));
    }

    [Fact]
    public void Missing_field_means_predicate_is_not_satisfied()
    {
        Assert.False(GatePredicateEvaluator.Evaluate(
            new GatePredicate("amount", "gt", 50_000),
            Args("""{"currency":"RUB"}""")));
    }

    [Fact]
    public void Null_arguments_mean_predicate_is_not_satisfied()
    {
        Assert.False(GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "gt", 50_000), null));
    }

    [Theory]
    [InlineData("regex")]
    [InlineData("")]
    [InlineData("between")]
    public void Unsupported_operator_is_rejected(string op)
    {
        Assert.False(GatePredicateEvaluator.IsValid(new GatePredicate("amount", op, 1)));

        Assert.Throws<GatePredicateException>(() =>
            GatePredicateEvaluator.Evaluate(new GatePredicate("amount", op, 1), Args("""{"amount":100}""")));
    }

    [Fact]
    public void Non_object_arguments_are_rejected()
    {
        Assert.Throws<GatePredicateException>(() =>
            GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "gt", 1), Args("[]")));
    }

    [Fact]
    public void Numeric_comparison_requires_numeric_values()
    {
        Assert.Throws<GatePredicateException>(() =>
            GatePredicateEvaluator.Evaluate(new GatePredicate("amount", "gt", 50_000), Args("""{"amount":"many"}""")));
    }

    [Fact]
    public void Invalid_predicate_is_rejected_by_validator()
    {
        Assert.False(GatePredicateEvaluator.IsValid(null));
        Assert.False(GatePredicateEvaluator.IsValid(new GatePredicate("", "gt", 1)));
    }
}
