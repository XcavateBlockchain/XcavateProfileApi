using System.Text.Json;

namespace XcavateBuckets.Domain;

/// <summary>
/// Enforces the pallet's input bounds. Every failure raises <see cref="BucketErrorCode.InvalidInput"/>,
/// which the pallet handled structurally through <c>BoundedVec</c> rather than as a runtime error.
/// </summary>
public class InputValidator(BucketOptions options)
{
    private const int Hex32ByteLength = 32;

    public BucketOptions Options { get; } = options;

    /// <summary>Bounds an optional string.</summary>
    public void Text(string? value, int maxLen, string field)
    {
        if (value is not null && value.Length > maxLen)
        {
            throw BucketException.InvalidInput(
                $"'{field}' must be at most {maxLen} characters, but was {value.Length}.");
        }
    }

    /// <summary>Bounds a string that must also be present and non-blank.</summary>
    public void Required(string? value, int maxLen, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BucketException.InvalidInput($"'{field}' is required.");
        }

        Text(value, maxLen, field);
    }

    /// <summary>
    /// Requires a hex string decoding to exactly 32 bytes, with or without a <c>0x</c> prefix.
    /// Used for encryption keys, X25519 viewer keys and content hashes, all <c>[u8; 32]</c> in the
    /// pallet.
    /// </summary>
    public void Hex32Value(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BucketException.InvalidInput($"'{field}' is required.");
        }

        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (hex.Length != Hex32ByteLength * 2)
        {
            throw BucketException.InvalidInput(
                $"'{field}' must be a {Hex32ByteLength}-byte hex string.");
        }

        foreach (var c in hex)
        {
            var isHexDigit = c is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';

            if (!isHexDigit)
            {
                throw BucketException.InvalidInput($"'{field}' must be a hex string.");
            }
        }
    }

    /// <summary>
    /// Validates and canonicalises a properties map. Keys are sorted so the stored JSON matches the
    /// ordering of the pallet's <c>BoundedBTreeMap</c> and stays comparable across writes. Returns
    /// null when there are no properties, so the column stays null rather than holding "{}".
    /// </summary>
    public string? PropertiesJson(IEnumerable<KeyValuePair<string, string>>? properties)
    {
        if (properties is null)
        {
            return null;
        }

        var entries = properties.ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Count > Options.MaxProperties)
        {
            throw BucketException.InvalidInput(
                $"'properties' must have at most {Options.MaxProperties} entries, " +
                $"but had {entries.Count}.");
        }

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in entries)
        {
            Required(key, Options.MaxPropertyKeyLen, "properties.key");
            Text(value, Options.MaxPropertyValueLen, $"properties['{key}']");

            if (!map.TryAdd(key, value))
            {
                throw BucketException.InvalidInput($"'properties' has a duplicate key '{key}'.");
            }
        }

        return JsonSerializer.Serialize(map);
    }
}
