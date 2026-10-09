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
            new { id = listingId }, "listings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new SpvElectionInfo(
                (int)node.Value.GetProperty("spvElectionRound").GetInt64(),
                node.Value.GetProperty("spvElectionExpiry").GetInt64(),
                (int)node.Value.GetProperty("spvElectionCandidateCount").GetInt64(),
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
            new { id = assetId }, "propertyLettings", ct);

        var node = FirstNode(nodes);
        return node is null
            ? null
            : new PropertyLettingInfo(
                node.Value.GetProperty("agent").GetString() ?? string.Empty,
                node.Value.GetProperty("electionExpiry").GetInt64(),
                node.Value.GetProperty("electionRound").GetInt64(),
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
            new { id = assetId }, "proposals", ct);

        var node = FindNode(nodes, n => n.GetProperty("proposalId").GetInt64() == proposalId);
        return node is null
            ? null
            : new ProposalInfo(
                proposalId,
                node.Value.GetProperty("expiry").GetInt64(),
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
            new { id = assetId }, "proposals", ct);

        var node = FindNode(nodes, n => n.GetProperty("id").GetString() == proposalPda);
        return node is null
            ? null
            : new ProposalResultInfo(
                node.Value.GetProperty("proposalId").GetInt64(),
                node.Value.GetProperty("tallyYes").GetInt64(),
                node.Value.GetProperty("tallyNo").GetInt64(),
                node.Value.GetProperty("tallyAbstain").GetInt64(),
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
            new { id = assetId }, "challenges", ct);

        var node = FindNode(nodes, n => n.GetProperty("challengeId").GetInt64() == challengeId);
        return node is null
            ? null
            : new ChallengeInfo(
                challengeId,
                node.Value.GetProperty("expiry").GetInt64(),
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
            new { id = assetId }, "challenges", ct);

        var node = FindNode(nodes, n => n.GetProperty("id").GetString() == challengePda);
        return node is null
            ? null
            : new ChallengeResultInfo(
                node.Value.GetProperty("challengeId").GetInt64(),
                node.Value.GetProperty("tallyYes").GetInt64(),
                node.Value.GetProperty("tallyNo").GetInt64(),
                node.Value.GetProperty("tallyAbstain").GetInt64(),
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
            new { id = assetId }, "propertyAssets", ct);

        return FirstNode(nodes)?.GetProperty("shareAmount").GetInt64();
    }

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

    private static int Bps(JsonElement node, string name) => node.GetProperty(name).GetInt32();

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
