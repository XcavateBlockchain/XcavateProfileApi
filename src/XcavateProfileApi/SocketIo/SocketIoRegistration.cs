using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// Wiring for the realtime bucket-message endpoint, shared by the host and the test fixtures the
/// same way <see cref="GraphQL.BucketRegistration"/> is.
/// </summary>
public static class SocketIoRegistration
{
    /// <summary>
    /// Registers the Socket.IO services. <see cref="SocketIoOptions"/> is TryAdd'd, so a host or
    /// test may register its own instance first (e.g. short heartbeats in tests).
    /// <see cref="SocketIoBucketNotifier"/> is registered as itself; the host composes it into the
    /// <see cref="XcavateBuckets.Domain.Services.IBucketNotifier"/> registration.
    /// </summary>
    public static IServiceCollection AddBucketSocketIo(this IServiceCollection services)
    {
        services.TryAddSingleton(new SocketIoOptions());
        services.AddSingleton<BucketSubscriptionRegistry>();
        services.AddSingleton<SocketIoBroadcastQueue>();
        services.AddHostedService<SocketIoBroadcastDispatcher>();
        services.AddSingleton<SocketIoConnectionHandler>();
        services.AddScoped<SocketIoBucketNotifier>();
        return services;
    }

    /// <summary>
    /// Mounts the endpoint on <see cref="SocketIoOptions.Path"/>. Call after
    /// <c>UseWebSockets()</c>; the branch short-circuits, so it never shadows other routes.
    /// </summary>
    public static IApplicationBuilder UseBucketSocketIo(this IApplicationBuilder app) =>
        app.Map(SocketIoOptions.Path, branch => branch.Run(context =>
            context.RequestServices
                .GetRequiredService<SocketIoConnectionHandler>()
                .HandleAsync(context)));
}
