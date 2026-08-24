using XcavateBuckets.Domain.Entities;

namespace XcavateProfileApi.SocketIo;

/// <summary>
/// Wire shape of the <c>message</c> event, serialized camelCase. Field names and encodings match
/// the GraphQL <c>Message</c> type — ids are strings (the <c>BigInt</c> scalar is a string on the
/// wire), <c>id</c> is the composite <c>"{bucketId}-{messageId}"</c>, timestamps are ISO-8601
/// UTC — plus an explicit <c>bucketId</c> so a client subscribed to several buckets can demux.
/// </summary>
public sealed record BucketMessageEvent(
    string Id,
    string BucketId,
    string MessageId,
    string MessageIdNumber,
    string Contributor,
    string? Reference,
    string? Tag,
    string? Description,
    string? ContentType,
    string? ContentHash,
    string? Properties,
    string? IpfsContent,
    string CreatedAt)
{
    public static BucketMessageEvent From(Message message) => new(
        Id: $"{message.BucketId}-{message.MessageId}",
        BucketId: message.BucketId.ToString(),
        MessageId: message.MessageId.ToString(),
        MessageIdNumber: message.MessageId.ToString(),
        Contributor: message.Contributor,
        Reference: message.Reference,
        Tag: message.Tag,
        Description: message.Description,
        ContentType: message.ContentType,
        ContentHash: message.ContentHash,
        Properties: message.Properties,
        IpfsContent: message.IpfsContent,
        CreatedAt: DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc).ToString("o"));
}
