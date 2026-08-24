using System.Collections.Concurrent;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// A connection the registry can deliver pre-encoded frames to. Narrow on purpose: the
/// broadcast side never sees websockets, which keeps fan-out unit-testable with fakes.
/// </summary>
public interface ISocketIoSubscriber
{
    Guid Id { get; }

    /// <summary>Queues one frame for delivery. False when the connection is too slow and dropped.</summary>
    bool TryEnqueue(string frame);
}

/// <summary>
/// Which connections are subscribed to which buckets. Mutations are rare (subscribe, unsubscribe,
/// disconnect) and serialize on a lock; <see cref="SubscribersOf"/> is the hot path and reads a
/// concurrent map without locking.
/// </summary>
public sealed class BucketSubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, ISocketIoSubscriber>> _byBucket = new();
    private readonly Dictionary<Guid, HashSet<long>> _byConnection = new();

    public void Subscribe(ISocketIoSubscriber subscriber, long bucketId)
    {
        lock (_gate)
        {
            _byBucket.GetOrAdd(bucketId, _ => new ConcurrentDictionary<Guid, ISocketIoSubscriber>())
                [subscriber.Id] = subscriber;

            if (!_byConnection.TryGetValue(subscriber.Id, out var buckets))
            {
                buckets = new HashSet<long>();
                _byConnection[subscriber.Id] = buckets;
            }

            buckets.Add(bucketId);
        }
    }

    public void Unsubscribe(ISocketIoSubscriber subscriber, long bucketId)
    {
        lock (_gate)
        {
            RemoveFromBucket(subscriber.Id, bucketId);

            if (_byConnection.TryGetValue(subscriber.Id, out var buckets))
            {
                buckets.Remove(bucketId);
                if (buckets.Count == 0)
                {
                    _byConnection.Remove(subscriber.Id);
                }
            }
        }
    }

    /// <summary>Drops every subscription the connection holds. Idempotent.</summary>
    public void Disconnect(ISocketIoSubscriber subscriber)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(subscriber.Id, out var buckets))
            {
                return;
            }

            foreach (var bucketId in buckets)
            {
                RemoveFromBucket(subscriber.Id, bucketId);
            }
        }
    }

    /// <summary>A snapshot of the bucket's current subscribers; empty when there are none.</summary>
    public IReadOnlyCollection<ISocketIoSubscriber> SubscribersOf(long bucketId) =>
        _byBucket.TryGetValue(bucketId, out var subscribers)
            ? subscribers.Values.ToArray()
            : [];

    private void RemoveFromBucket(Guid connectionId, long bucketId)
    {
        if (_byBucket.TryGetValue(bucketId, out var subscribers))
        {
            subscribers.TryRemove(connectionId, out _);
            if (subscribers.IsEmpty)
            {
                _byBucket.TryRemove(bucketId, out _);
            }
        }
    }
}
