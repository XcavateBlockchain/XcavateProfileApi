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

/// <summary>The property's letting seat as the indexer mirrors it (property <c>PropertyLetting</c>).</summary>
public sealed record PropertyLettingInfo(
    string Agent, long ElectionExpiry, long ElectionRound, int ElectionQuorumBps, int GovernanceStrikes);

/// <summary>An open spending proposal (property <c>Proposal</c>).</summary>
public sealed record ProposalInfo(long ProposalId, long Expiry, int QuorumBps, int ThresholdBps);

/// <summary>A finalized proposal with its frozen tallies.</summary>
public sealed record ProposalResultInfo(
    long ProposalId, long TallyYes, long TallyNo, long TallyAbstain, int QuorumBps, int ThresholdBps);

/// <summary>An open challenge against the seated agent (property <c>Challenge</c>).</summary>
public sealed record ChallengeInfo(long ChallengeId, long Expiry, int QuorumBps);

/// <summary>A finalized challenge with its frozen tallies.</summary>
public sealed record ChallengeResultInfo(
    long ChallengeId, long TallyYes, long TallyNo, long TallyAbstain, int QuorumBps);

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

    /// <summary>The property's letting seat row, or null if absent.</summary>
    Task<PropertyLettingInfo?> GetPropertyLettingAsync(long assetId, CancellationToken ct);

    /// <summary>An open proposal by its on-chain id, or null if the asset has no live proposal row.</summary>
    Task<ProposalInfo?> GetOpenProposalAsync(long assetId, long proposalId, CancellationToken ct);

    /// <summary>A finalized proposal found by its PDA (the GraphQL <c>id</c>), or null if absent.</summary>
    Task<ProposalResultInfo?> GetFinalizedProposalAsync(long assetId, string proposalPda, CancellationToken ct);

    /// <summary>An open challenge by its on-chain id, or null if the asset has no live challenge row.</summary>
    Task<ChallengeInfo?> GetOpenChallengeAsync(long assetId, long challengeId, CancellationToken ct);

    /// <summary>A finalized challenge found by its PDA (the GraphQL <c>id</c>), or null if absent.</summary>
    Task<ChallengeResultInfo?> GetFinalizedChallengeAsync(long assetId, string challengePda, CancellationToken ct);

    /// <summary>The property's total share supply (the quorum denominator), or null if absent.</summary>
    Task<long?> GetShareAmountAsync(long assetId, CancellationToken ct);

    /// <summary>
    /// The wallets of the listing's CURRENT stakeholder set (primary-market positions with
    /// shares or an outstanding reservation, plus secondary-market share holdings), deduplicated.
    /// Used by the claim-started webhook to seed the legal bucket's viewers at creation time:
    /// holder events that arrived before the bucket existed are deferred exactly because this
    /// reconciliation covers them.
    /// </summary>
    Task<IReadOnlyList<string>> GetCurrentHolderWalletsAsync(long listingId, CancellationToken ct);

    /// <summary>
    /// The wallets currently HOLDING the asset's shares: the secondary-market
    /// <c>shareHoldings</c> rows that are active and carry a positive amount, deduplicated.
    /// This is the income-distribution fanout set — after claim the holding IS the
    /// income-bearing position, so unlike <see cref="GetCurrentHolderWalletsAsync"/> the
    /// sale-phase investor positions are not part of it.
    /// </summary>
    Task<IReadOnlyList<string>> GetShareHolderWalletsAsync(long assetId, CancellationToken ct);

    /// <summary>
    /// The property asset's display name (marketplace <c>PropertyAsset.name</c>), or null when
    /// the indexer has no row for the asset. Best-effort notification enrichment only.
    /// </summary>
    Task<string?> GetPropertyAssetNameAsync(long assetId, CancellationToken ct);

    /// <summary>
    /// The wallet behind a letting-agent ENTRY PDA (a property-program <c>LettingAgent</c>
    /// account, seeded <c>["agent", wallet]</c>), or null when the indexer has no such row. The
    /// GraphQL <c>lettingAgents</c> connection filters by wallet or region only — never by entry
    /// PDA — so the lookup pages the registry and matches the node's <c>id</c> client-side.
    /// </summary>
    Task<string?> GetLettingAgentWalletAsync(string entryPda, CancellationToken ct);
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

    public async Task<PropertyLettingInfo?> GetPropertyLettingAsync(long assetId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              propertyLettings(assetId: $id, first: 1, offset: 0) {
                nodes { agent electionExpiry electionRound electionQuorumBps governanceStrikes }
              }
            }
            """,
            new { id = I64Var(assetId) }, "propertyLettings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new PropertyLettingInfo(
                node.Value.GetProperty("agent").GetString() ?? string.Empty,
                LongOf(node.Value, "electionExpiry"),
                LongOf(node.Value, "electionRound"),
                Bps(node.Value, "electionQuorumBps"),
                node.Value.GetProperty("governanceStrikes").GetInt32());
    }

    public async Task<ProposalInfo?> GetOpenProposalAsync(long assetId, long proposalId, CancellationToken ct)
    {
        // One live proposal per property on-chain, so the active page is at most one row.
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              proposals(assetId: $id, active: true, first: 10, offset: 0) {
                nodes { proposalId expiry quorumBps thresholdBps }
              }
            }
            """,
            new { id = I64Var(assetId) }, "proposals", ct);

        var node = FindNode(nodes, n => LongOf(n, "proposalId") == proposalId);
        return node is null
            ? null
            : new ProposalInfo(
                proposalId,
                LongOf(node.Value, "expiry"),
                Bps(node.Value, "quorumBps"),
                Bps(node.Value, "thresholdBps"));
    }

    public async Task<ProposalResultInfo?> GetFinalizedProposalAsync(
        long assetId, string proposalPda, CancellationToken ct)
    {
        // A finalized proposal's PDA is closed, so it only shows on the inactive page.
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              proposals(assetId: $id, active: false, first: 50, offset: 0) {
                nodes { id proposalId tallyYes tallyNo tallyAbstain quorumBps thresholdBps }
              }
            }
            """,
            new { id = I64Var(assetId) }, "proposals", ct);

        var node = FindNode(nodes, n => n.GetProperty("id").GetString() == proposalPda);
        return node is null
            ? null
            : new ProposalResultInfo(
                LongOf(node.Value, "proposalId"),
                LongOf(node.Value, "tallyYes"),
                LongOf(node.Value, "tallyNo"),
                LongOf(node.Value, "tallyAbstain"),
                Bps(node.Value, "quorumBps"),
                Bps(node.Value, "thresholdBps"));
    }

    public async Task<ChallengeInfo?> GetOpenChallengeAsync(long assetId, long challengeId, CancellationToken ct)
    {
        // One live challenge per property on-chain, so the active page is at most one row.
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              challenges(assetId: $id, active: true, first: 10, offset: 0) {
                nodes { challengeId expiry quorumBps }
              }
            }
            """,
            new { id = I64Var(assetId) }, "challenges", ct);

        var node = FindNode(nodes, n => LongOf(n, "challengeId") == challengeId);
        return node is null
            ? null
            : new ChallengeInfo(
                challengeId,
                LongOf(node.Value, "expiry"),
                Bps(node.Value, "quorumBps"));
    }

    public async Task<ChallengeResultInfo?> GetFinalizedChallengeAsync(
        long assetId, string challengePda, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              challenges(assetId: $id, active: false, first: 50, offset: 0) {
                nodes { id challengeId tallyYes tallyNo tallyAbstain quorumBps }
              }
            }
            """,
            new { id = I64Var(assetId) }, "challenges", ct);

        var node = FindNode(nodes, n => n.GetProperty("id").GetString() == challengePda);
        return node is null
            ? null
            : new ChallengeResultInfo(
                LongOf(node.Value, "challengeId"),
                LongOf(node.Value, "tallyYes"),
                LongOf(node.Value, "tallyNo"),
                LongOf(node.Value, "tallyAbstain"),
                Bps(node.Value, "quorumBps"));
    }

    public async Task<long?> GetShareAmountAsync(long assetId, CancellationToken ct)
    {
        var nodes = await QueryAsync(
            """
            query($id: I64!) {
              propertyAssets(assetId: $id, first: 1, offset: 0) {
                nodes { shareAmount }
              }
            }
            """,
            new { id = I64Var(assetId) }, "propertyAssets", ct);

        return FirstNode(nodes) is { } shareRow ? LongOf(shareRow, "shareAmount") : null;
    }

    public async Task<IReadOnlyList<string>> GetCurrentHolderWalletsAsync(long listingId, CancellationToken ct)
    {
        var wallets = new HashSet<string>(StringComparer.Ordinal);

        // Primary market: live positions with purchased or still-reserved shares.
        await PageHoldersAsync(
            """
            query($id: I64!, $first: Int!, $offset: Int!) {
              investorPositions(listingId: $id, active: true, first: $first, offset: $offset) {
                nodes { investor shareAmount reservedShareAmount cancelled }
              }
            }
            """,
            "investorPositions", listingId, wallets,
            n => !n.GetProperty("cancelled").GetBoolean()
                && (LongOf(n, "shareAmount") > 0
                    || LongOf(n, "reservedShareAmount") > 0)
                    ? n.GetProperty("investor").GetString()
                    : null,
            ct);

        // Secondary market: live holdings with a balance (buyers have no investor position).
        foreach (var wallet in await GetShareHolderWalletsAsync(listingId, ct))
        {
            wallets.Add(wallet);
        }

        return wallets.ToList();
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

    public async Task<string?> GetLettingAgentWalletAsync(string entryPda, CancellationToken ct)
    {
        // No server-side filter on the entry PDA exists (the resolver's wallet/region args
        // filter other columns), so page the registry and match the id client-side; a closed
        // entry still answers, so a late redelivery of an appointment resolves the same wallet.
        for (var page = 0; page < MaxRegistryPages; page++)
        {
            var nodes = await QueryAsync(
                """
                query($first: Int!, $offset: Int!) {
                  lettingAgents(first: $first, offset: $offset) {
                    nodes { id wallet }
                  }
                }
                """,
                new { first = RegistryPageSize, offset = page * RegistryPageSize },
                "lettingAgents",
                ct);

            var node = FindNode(nodes, n => n.GetProperty("id").GetString() == entryPda);
            if (node is not null)
            {
                return node.Value.GetProperty("wallet").GetString();
            }

            if (nodes is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() < RegistryPageSize)
            {
                return null; // a short page is the last page: no such entry
            }
        }

        return null;
    }

    private const int RegistryPageSize = 500;

    /// <summary>A bound on the registry paging loop (20k entries), not a limit anyone should reach.</summary>
    private const int MaxRegistryPages = 40;

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

    /// <summary>
    /// The property program's pass rule, mirrored from the chain: turnout strictly over the
    /// quorum's share of supply, yes strictly over no, and — proposals only — high-tier votes also
    /// need <c>threshold_bps</c> of the yes/(yes+no) split. Used to turn frozen tallies into the
    /// passed/failed outcome of a result announcement.
    /// </summary>
    public static bool VotePassed(long yes, long no, long abstain, int quorumBps, int? thresholdBps, long shareAmount)
    {
        if (yes <= no)
        {
            return false;
        }

        var turnout = yes + no + abstain;
        if (turnout * 10_000L <= shareAmount * (long)quorumBps)
        {
            return false;
        }

        return thresholdBps is not > 0 || yes * 10_000L >= (yes + no) * (long)thresholdBps.Value;
    }

    /// <summary>The unix-seconds expiry fields the indexer serves, as the ISO-8601 the messenger schema wants.</summary>
    public static string Iso8601(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("O");

    private static int Bps(JsonElement node, string name) => (int)LongOf(node, name);

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
