using XcavateProfileApi.SocketIo;

namespace XcavateBuckets.Tests;

/// <summary>Subscription bookkeeping: who gets a bucket's frames, and cleanup on the way out.</summary>
[TestFixture]
public class SocketIoRegistryTests
{
    /// <summary>Hand-written fake, house style: records what the dispatcher would deliver.</summary>
    private sealed class FakeSubscriber : ISocketIoSubscriber
    {
        public Guid Id { get; } = Guid.NewGuid();

        public List<string> Frames { get; } = [];

        public bool TryEnqueue(string frame)
        {
            Frames.Add(frame);
            return true;
        }
    }

    private BucketSubscriptionRegistry _registry = null!;

    [SetUp]
    public void SetUp() => _registry = new BucketSubscriptionRegistry();

    [Test]
    public void Subscribe_registers_for_that_bucket_only()
    {
        var subscriber = new FakeSubscriber();

        _registry.Subscribe(subscriber, 1);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.SubscribersOf(1), Is.EqualTo(new[] { subscriber }));
            Assert.That(_registry.SubscribersOf(2), Is.Empty);
        });
    }

    [Test]
    public void Subscribing_twice_registers_once()
    {
        var subscriber = new FakeSubscriber();

        _registry.Subscribe(subscriber, 1);
        _registry.Subscribe(subscriber, 1);

        Assert.That(_registry.SubscribersOf(1), Has.Count.EqualTo(1));
    }

    [Test]
    public void Unsubscribe_removes_only_that_bucket()
    {
        var subscriber = new FakeSubscriber();
        _registry.Subscribe(subscriber, 1);
        _registry.Subscribe(subscriber, 2);

        _registry.Unsubscribe(subscriber, 1);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.SubscribersOf(1), Is.Empty);
            Assert.That(_registry.SubscribersOf(2), Is.EqualTo(new[] { subscriber }));
        });
    }

    [Test]
    public void Disconnect_removes_every_subscription()
    {
        var leaving = new FakeSubscriber();
        var staying = new FakeSubscriber();
        _registry.Subscribe(leaving, 1);
        _registry.Subscribe(leaving, 2);
        _registry.Subscribe(staying, 1);

        _registry.Disconnect(leaving);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.SubscribersOf(1), Is.EqualTo(new[] { staying }));
            Assert.That(_registry.SubscribersOf(2), Is.Empty);
        });
    }

    [Test]
    public void Disconnect_of_an_unknown_subscriber_is_a_no_op() =>
        Assert.DoesNotThrow(() => _registry.Disconnect(new FakeSubscriber()));
}
