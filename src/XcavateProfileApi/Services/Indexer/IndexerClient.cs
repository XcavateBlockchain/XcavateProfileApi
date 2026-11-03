using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XcavateProfileApi.Services.Indexer;

/// <summary>Where the indexer's GraphQL API lives.</summary>
public sealed class IndexerOptions
{
    public const string DefaultBaseUrl = "https://indexer-devnet.xcavate.io";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
}

/// <summary>A listing's SPV-lawyer election as the indexer mirrors it (marketplace <c>Listing</c>).</summary>
public sealed record SpvElectionInfo(int Round, long Expiry, int CandidateCount, string SpvLawyer);

/// <summary>One lawyer's candidacy in an exact listing and election round.</summary>
public sealed record SpvCandidacyInfo(string Id, int Round, string Lawyer);

/// <summary>The two lawyer wallets assigned to a marketplace listing.</summary>
public sealed record ListingLawyersInfo(string SpvLawyer, string DeveloperLawyer);

/// <summary>The two assigned lawyers' document verdicts and the listing's legal-review state.</summary>
public sealed record ListingReviewInfo(
    string SpvLawyer,
    string SpvDocumentStatus,
    string SpvDocumentsHash,
    string DeveloperLawyer,
    string DeveloperDocumentStatus,
    string DeveloperDocumentsHash,
    bool SecondAttempt,
    string Status,
    long LegalDeadline);

/// <summary>Thrown when the indexer cannot answer a resolution query (down, erroring, or the row is missing).</summary>
public sealed class IndexerQueryException : Exception
{
    public IndexerQueryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The read side of the webhook flow: the indexer's governance webhooks carry only
/// instruction-derivable evidence (ids, PDAs, args — the mapper is pure), so the display facts of a
/// vote announcement — rounds, expiries, tallies, outcomes — are resolved here from the mirror the
/// indexer itself maintains. Consistency is free: the event row and the account rows it reports on
/// commit in the same indexer transaction, so by the time a webhook arrives the GraphQL answers
/// already describe the post-transaction state.
/// </summary>
public interface IIndexerClient
{
    /// <summary>The listing's SPV-lawyer election counters + assignment, or null if the listing is absent.</summary>
    Task<SpvElectionInfo?> GetListingSpvElectionAsync(long listingId, CancellationToken ct);

    /// <summary>A lawyer's candidacy in an exact listing and round, including closed history.</summary>
    Task<SpvCandidacyInfo?> GetSpvCandidacyAsync(
        long listingId, int round, string lawyer, CancellationToken ct);

    /// <summary>Both lawyer wallets assigned to the listing, or null if the listing is absent.</summary>
    Task<ListingLawyersInfo?> GetListingLawyersAsync(long listingId, CancellationToken ct);

    /// <summary>The listing's complete two-lawyer document-review state, or null if absent.</summary>
    Task<ListingReviewInfo?> GetListingLawyerReviewAsync(long listingId, CancellationToken ct);

    /// <summary>
    /// The wallets currently HOLDING the asset's shares: the secondary-market
    /// <c>shareHoldings</c> rows that are active and carry a positive amount, deduplicated.
    /// This is the income-distribution fanout set and the lawyer-bucket holder set: after
    /// claim the holding is the income-bearing position; sale-phase reservations are excluded.
    /// </summary>
    Task<IReadOnlyList<string>> GetShareHolderWalletsAsync(long assetId, CancellationToken ct);

    /// <summary>
    /// The property asset's display name (marketplace <c>PropertyAsset.name</c>), or null when
    /// the indexer has no row for the asset. Best-effort notification enrichment only.
    /// </summary>
    Task<string?> GetPropertyAssetNameAsync(long assetId, CancellationToken ct);
}

/// <summary>
/// Minimal hand-rolled GraphQL POST client for the indexer (juniper + axum). No auth: the read API
/// is public. Failures surface as <see cref="IndexerQueryException"/> so the webhook layer can turn
/// them into a retryable 5xx instead of silently posting an empty announcement.
/// </summary>
public class IndexerClient(
    IHttpClientFactory httpFactory,
    IndexerOptions options,
    ILogger<IndexerClient> logger) : IIndexerClient
{
    public const string HttpClientName = "indexer-graphql";

    private readonly Uri _graphqlUri = new(new Uri(options.BaseUrl.TrimEnd('/') + "/"), "graphql");

    public async Task<SpvElectionInfo?> GetListingSpvElectionAsync(long listingId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              listings(listingId: $id, first: 1, offset: 0) {
                nodes { spvElectionExpiry spvElectionRound spvElectionCandidateCount spvLawyer }
              }
            }
            """,
            new { id = I64Var(listingId) }, "listings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new SpvElectionInfo(
                (int)LongOf(node.Value, "spvElectionRound"),
                LongOf(node.Value, "spvElectionExpiry"),
                (int)LongOf(node.Value, "spvElectionCandidateCount"),
                node.Value.GetProperty("spvLawyer").GetString() ?? string.Empty);
    }

    public async Task<SpvCandidacyInfo?> GetSpvCandidacyAsync(
        long listingId, int round, string lawyer, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($listingId: I64!, $round: I64!, $lawyer: String!) {
              lawyerCandidacies(listingId: $listingId, round: $round, lawyer: $lawyer, first: 1, offset: 0) {
                nodes { id round lawyer }
              }
            }
            """,
            new { listingId = I64Var(listingId), round = I64Var(round), lawyer },
            "lawyerCandidacies", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new SpvCandidacyInfo(
                node.Value.GetProperty("id").GetString()!,
                (int)LongOf(node.Value, "round"),
                node.Value.GetProperty("lawyer").GetString()!);
    }

    public async Task<ListingLawyersInfo?> GetListingLawyersAsync(long listingId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              listings(listingId: $id, first: 1, offset: 0) {
                nodes { spvLawyer developerLawyer }
              }
            }
            """,
            new { id = I64Var(listingId) }, "listings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new ListingLawyersInfo(
                node.Value.GetProperty("spvLawyer").GetString() ?? string.Empty,
                node.Value.GetProperty("developerLawyer").GetString() ?? string.Empty);
    }

    public async Task<ListingReviewInfo?> GetListingLawyerReviewAsync(long listingId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              listings(listingId: $id, first: 1, offset: 0) {
                nodes {
                  spvLawyer spvLawyerDocStatus spvLawyerDocumentsHash
                  developerLawyer developerLawyerDocStatus developerLawyerDocumentsHash
                  secondAttempt status legalDeadline
                }
              }
            }
            """,
            new { id = I64Var(listingId) }, "listings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new ListingReviewInfo(
                node.Value.GetProperty("spvLawyer").GetString() ?? string.Empty,
                node.Value.GetProperty("spvLawyerDocStatus").GetString() ?? string.Empty,
                node.Value.GetProperty("spvLawyerDocumentsHash").GetString() ?? string.Empty,
                node.Value.GetProperty("developerLawyer").GetString() ?? string.Empty,
                node.Value.GetProperty("developerLawyerDocStatus").GetString() ?? string.Empty,
                node.Value.GetProperty("developerLawyerDocumentsHash").GetString() ?? string.Empty,
                node.Value.GetProperty("secondAttempt").GetBoolean(),
                node.Value.GetProperty("status").GetString() ?? string.Empty,
                LongOf(node.Value, "legalDeadline"));
    }

    public async Task<IReadOnlyList<string>> GetShareHolderWalletsAsync(long assetId, CancellationToken ct)
    {
        var wallets = new HashSet<string>(StringComparer.Ordinal);

        await PageHoldersAsync(
            """
            query($id: I64!, $first: Int!, $offset: Int!) {
              shareHoldings(assetId: $id, active: true, first: $first, offset: $offset) {
                nodes { owner amount }
              }
            }
            """,
            "shareHoldings", assetId, wallets,
            n => LongOf(n, "amount") > 0
                ? n.GetProperty("owner").GetString()
                : null,
            ct);

        return wallets.ToList();
    }

    public async Task<string?> GetPropertyAssetNameAsync(long assetId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              propertyAssets(assetId: $id, first: 1, offset: 0) {
                nodes { name }
              }
            }
            """,
            new { id = I64Var(assetId) }, "propertyAssets", ct);

        return FirstNode(nodes) is { } asset
            ? asset.GetProperty("name").GetString()
            : null;
    }

    /// <summary>One paged holder-enumeration query, folding qualifying wallets into the set.</summary>
    private async Task PageHoldersAsync(
        string query,
        string rootField,
        long id,
        HashSet<string> wallets,
        Func<JsonElement, string?> extract,
        CancellationToken ct)
    {
        for (var page = 0; page < MaxHolderPages; page++)
        {
            var nodes = await QueryAsync(
                query,
                new { id = I64Var(id), first = HolderPageSize, offset = page * HolderPageSize },
                rootField,
                ct);

            var count = 0;
            if (nodes is { ValueKind: JsonValueKind.Array } arr)
            {
                foreach (var node in arr.EnumerateArray())
                {
                    count++;
                    var wallet = extract(node);
                    if (!string.IsNullOrWhiteSpace(wallet))
                    {
                        wallets.Add(wallet);
                    }
                }
            }

            if (count < HolderPageSize)
            {
                return; // a short page is the last page
            }
        }
    }

    private const int HolderPageSize = 500;

    /// <summary>A bound on the paging loop (20k wallets), not a limit anyone should reach.</summary>
    private const int MaxHolderPages = 40;

    /// <summary>The unix-seconds expiry fields the indexer serves, as the ISO-8601 the messenger schema wants.</summary>
    public static string Iso8601(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("O");

    /// <summary>The wire form of an I64 variable: juniper serves and accepts it as a STRING.</summary>
    private static string I64Var(long value) => value.ToString();

    /// <summary>
    /// Reads an I64 field, which the indexer serves as a JSON STRING ("123") — accepting a bare
    /// number too, so the parse is robust against either scalar flavor.
    /// </summary>
    private static long LongOf(JsonElement node, string name)
    {
        var value = node.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!)
            : value.GetInt64();
    }

    private static JsonElement? FirstNode(JsonElement? nodes) =>
        nodes is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0] : null;

    private static JsonElement? FindNode(JsonElement? nodes, Func<JsonElement, bool> match)
    {
        if (nodes is not { ValueKind: JsonValueKind.Array } arr)
        {
            return null;
        }

        foreach (var node in arr.EnumerateArray())
        {
            if (match(node))
            {
                return node;
            }
        }

        return null;
    }

    private async Task<JsonElement?> QueryAsync(
        string query, object variables, string rootField, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _graphqlUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(new { query, variables });

            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new IndexerQueryException(
                    $"Indexer GraphQL answered {(int)response.StatusCode} for '{rootField}'.");
            }

            root = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)).Clone();
        }
        catch (IndexerQueryException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Indexer GraphQL query for {Field} failed", rootField);
            throw new IndexerQueryException($"Indexer GraphQL query for '{rootField}' failed.", ex);
        }

        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new IndexerQueryException(
                $"Indexer GraphQL reported errors for '{rootField}': {errors[0]}.");
        }

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty(rootField, out var connection)
            || !connection.TryGetProperty("nodes", out var nodes))
        {
            throw new IndexerQueryException(
                $"Indexer GraphQL answered without a '{rootField}.nodes' field.");
        }

        return nodes;
    }
}
