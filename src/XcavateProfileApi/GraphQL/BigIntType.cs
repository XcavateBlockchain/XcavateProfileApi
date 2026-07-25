using System.Globalization;
using System.Text.Json;
using HotChocolate.Execution;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// A 64-bit integer that travels as a string, matching the <c>BigInt</c> scalar SubQuery generated
/// for the indexer. Ids are <c>u128</c> on chain, so clients already expect the string form.
/// </summary>
public sealed class BigIntType : ScalarType<long, StringValueNode>
{
    public BigIntType() : base("BigInt", BindingBehavior.Explicit)
    {
        Description = "A 64-bit integer serialized as a string, matching SubQuery's BigInt scalar.";
    }

    protected override long OnCoerceInputLiteral(StringValueNode valueLiteral)
        => Parse(valueLiteral.Value);

    protected override long OnCoerceInputValue(JsonElement inputValue, IFeatureProvider context)
        => inputValue.ValueKind switch
        {
            // Numbers are accepted as well as strings, so hand-written queries that pass a bare
            // integer literal still work.
            JsonValueKind.String => Parse(inputValue.GetString()),
            JsonValueKind.Number => inputValue.GetInt64(),
            _ => throw new GraphQLException("BigInt must be a string or a number.")
        };

    protected override void OnCoerceOutputValue(long runtimeValue, ResultElement resultValue)
        => resultValue.SetStringValue(Format(runtimeValue), false);

    protected override StringValueNode OnValueToLiteral(long runtimeValue)
        => new(Format(runtimeValue));

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long Parse(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new GraphQLException($"'{value}' is not a valid BigInt.");
        }

        return parsed;
    }
}
