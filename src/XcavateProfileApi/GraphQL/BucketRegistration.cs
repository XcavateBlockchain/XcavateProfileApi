using HotChocolate.Execution.Configuration;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.GraphQL.Auth;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// One definition of the bucket wiring, shared by the application host and the tests so the schema
/// under test cannot drift from the schema that ships.
/// </summary>
public static class BucketRegistration
{
    /// <summary>
    /// Registers the domain services and per-request caller context. The caller registers
    /// <see cref="XcavateBuckets.Domain.Data.BucketDbContext"/> separately, because the host uses
    /// PostgreSQL and tests use SQLite.
    /// </summary>
    public static IServiceCollection AddBucketDomain(this IServiceCollection services)
    {
        services.AddSingleton(new BucketOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<InputValidator>();
        services.AddScoped<AuthorizationService>();
        services.AddScoped<NamespaceService>();
        services.AddScoped<BucketService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<TagService>();
        services.AddScoped<MessageService>();

        services.AddScoped<CallerContext>();
        services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());

        return services;
    }

    /// <summary>Registers the GraphQL schema: queries, mutations, entity types and the error filter.</summary>
    public static IRequestExecutorBuilder AddBucketGraphQL(this IServiceCollection services) =>
        services
            .AddGraphQLServer()
            .AddQueryType<BucketQueries>()
            .AddMutationType<BucketMutations>()
            .AddType<BigIntType>()
            .AddType<NamespaceType>()
            .AddType<NamespaceManagerType>()
            .AddType<BucketType>()
            .AddType<BucketAdminType>()
            .AddType<BucketContributorType>()
            .AddType<BucketViewerType>()
            .AddType<TagType>()
            .AddType<TagMessageCountType>()
            .AddType<MessageType>()
            // Every long in the model is an id, so binding globally keeps BigInt consistent.
            .BindRuntimeType<long, BigIntType>()
            .AddFiltering()
            .AddSorting()
            .AddErrorFilter<BucketErrorFilter>()
            // Hot Chocolate 16 enforces query cost by default, and its budget is far too small for
            // the nested reads this schema is meant to serve: the indexer's consumers routinely ask
            // for buckets plus their admins, tags and messages in one request, and every unpaged
            // relation list is costed at an assumed 50 items. Enforcement stays on as a
            // denial-of-service ceiling, just with a limit that ordinary queries fit inside.
            .ModifyCostOptions(options =>
            {
                options.MaxFieldCost = 100_000;
                options.MaxTypeCost = 100_000;
            });
}
