using XcavateProfileApi.Services.Indexer;

namespace XcavateBuckets.Tests;

/// <summary>
/// The webhook tests' <see cref="IIndexerClient"/>: each resolution method answers from a settable
/// property (null = "the indexer has no row", which the vote-state handlers turn into a retryable
/// 502), and <see cref="ThrowOnQuery"/> simulates an unreachable/erroring indexer. Registered by
/// <see cref="WebhooksHost"/> and exposed as <see cref="WebhooksHost.Indexer"/>.
/// </summary>
public sealed class TestIndexerClient : IIndexerClient
{
    public SpvElectionInfo? SpvElection { get; set; }
    public PropertyLettingInfo? PropertyLetting { get; set; }
    public ProposalInfo? OpenProposal { get; set; }
    public ProposalResultInfo? FinalizedProposal { get; set; }
    public ChallengeInfo? OpenChallenge { get; set; }
    public ChallengeResultInfo? FinalizedChallenge { get; set; }
    public long? ShareAmount { get; set; } = 1_000;
    public bool ThrowOnQuery { get; set; }

    public Task<SpvElectionInfo?> GetListingSpvElectionAsync(long listingId, CancellationToken ct)
        => Respond(SpvElection);

    public Task<PropertyLettingInfo?> GetPropertyLettingAsync(long assetId, CancellationToken ct)
        => Respond(PropertyLetting);

    public Task<ProposalInfo?> GetOpenProposalAsync(long assetId, long proposalId, CancellationToken ct)
        => Respond(OpenProposal);

    public Task<ProposalResultInfo?> GetFinalizedProposalAsync(
        long assetId, string proposalPda, CancellationToken ct)
        => Respond(FinalizedProposal);

    public Task<ChallengeInfo?> GetOpenChallengeAsync(long assetId, long challengeId, CancellationToken ct)
        => Respond(OpenChallenge);

    public Task<ChallengeResultInfo?> GetFinalizedChallengeAsync(
        long assetId, string challengePda, CancellationToken ct)
        => Respond(FinalizedChallenge);

    public Task<long?> GetShareAmountAsync(long assetId, CancellationToken ct)
        => Respond(ShareAmount);

    private Task<T?> Respond<T>(T? value)
        => ThrowOnQuery
            ? throw new IndexerQueryException("the indexer is down (simulated)")
            : Task.FromResult(value);
}
