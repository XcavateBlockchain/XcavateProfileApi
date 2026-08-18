using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace XcavateProfileApi.Data;

/// <summary>
/// Maps a small object graph — a role list, a permission map — to a single JSON text column.
/// </summary>
/// <remarks>
/// A text column rather than <c>jsonb</c> or EF's owned-entity <c>ToJson</c>, because the same model
/// is created on PostgreSQL in production and on SQLite in the test suite, and this is the one
/// mapping both providers spell identically. Nothing queries inside these values; they are read and
/// written whole with the row.
/// <para>
/// The enums inside carry their own <see cref="System.Text.Json.Serialization.JsonConverterAttribute"/>,
/// so the strings stored here are the same ones that go over the wire regardless of the options used.
/// </para>
/// </remarks>
internal static class JsonColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ValueConverter<T?, string?> Converter<T>()
        where T : class =>
        new(
            model => Serialize(model),
            column => Deserialize<T>(column));

    /// <summary>
    /// Change tracking needs value equality, or an edit inside the object graph — one role added to
    /// the list, one permission flipped — is invisible to <c>SaveChanges</c> and silently discarded.
    /// Comparing the serialized form gets that for any shape mapped through here.
    /// </summary>
    public static ValueComparer<T?> Comparer<T>()
        where T : class =>
        new(
            (left, right) => Serialize(left) == Serialize(right),
            // Spelled out rather than with ?., which an expression tree cannot contain.
            value => value == null ? 0 : Serialize(value)!.GetHashCode(StringComparison.Ordinal),
            value => Deserialize<T>(Serialize(value)));

    private static string? Serialize<T>(T? value)
        where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    private static T? Deserialize<T>(string? column)
        where T : class =>
        string.IsNullOrEmpty(column) ? null : JsonSerializer.Deserialize<T>(column, Options);
}
