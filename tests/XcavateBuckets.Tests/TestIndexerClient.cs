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
    public SpvCandidacyInfo? SpvCandidacy { get; set; }
    public ListingLawyersInfo? ListingLawyers { get; set; }
    public ListingReviewInfo? ListingReview { get; set; }

    /// <summary>The income webhook's fanout set (the asset's current share holders).</summary>
    public IReadOnlyList<string>? ShareHolderWallets { get; set; } = [];

    /// <summary>The shares webhook's push-copy enrichment (the property asset's name).</summary>
    public string? PropertyAssetName { get; set; }

    public bool ThrowOnQuery { get; set; }

    /// <summary>
    /// Fails ONLY the holder enumeration, so a test can script "the election/announcement resolves
    /// but the holder reconciliation does not" — the mid-grant 502 path <see cref="ThrowOnQuery"/>
    /// (which fails every query, including the announcement's own resolution) cannot reach.
    /// </summary>
    public bool ThrowOnHolders { get; set; }

    public Task<SpvElectionInfo?> GetListingSpvElectionAsync(long listingId, CancellationToken ct)
        => Respond(SpvElection);

    public Task<SpvCandidacyInfo?> GetSpvCandidacyAsync(
        long listingId, int round, string lawyer, CancellationToken ct)
        => Respond(SpvCandidacy);

    public Task<ListingLawyersInfo?> GetListingLawyersAsync(long listingId, CancellationToken ct)
        => Respond(ListingLawyers);

    public Task<ListingReviewInfo?> GetListingLawyerReviewAsync(long listingId, CancellationToken ct)
        => Respond(ListingReview);

    public Task<IReadOnlyList<string>> GetShareHolderWalletsAsync(long assetId, CancellationToken ct)
    {
        if (ThrowOnQuery || ThrowOnHolders)
        {
            throw new IndexerQueryException("the indexer is down (simulated)");
        }

        return Task.FromResult(ShareHolderWallets ?? (IReadOnlyList<string>)[]);
    }

    public Task<string?> GetPropertyAssetNameAsync(long assetId, CancellationToken ct)
        => Respond(PropertyAssetName);

    private Task<T?> Respond<T>(T? value)
        => ThrowOnQuery
            ? throw new IndexerQueryException("the indexer is down (simulated)")
            : Task.FromResult(value);
}
