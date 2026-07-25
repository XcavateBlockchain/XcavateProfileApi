using HotChocolate.Resolvers;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

// HotChocolate.Types also defines Tag; the domain entity is what we mean throughout.
using Tag = XcavateBuckets.Domain.Entities.Tag;

namespace XcavateProfileApi.GraphQL;

/// <summary>
/// Composite entity ids, matching the strings the SubQuery indexer used so existing consumers keep
/// working. Bucket and namespace ids are a bare number because they are globally unique; the pair
/// tables need both halves.
/// </summary>
public static class EntityId
{
    public static string For(Namespace value) => value.NamespaceId.ToString();

    public static string For(Bucket value) => value.BucketId.ToString();

    public static string For(NamespaceManager value) => $"{value.NamespaceId}-{value.Manager}";

    public static string For(BucketAdmin value) => $"{value.BucketId}-{value.SubjectId}";

    public static string For(BucketContributor value) => $"{value.BucketId}-{value.SubjectId}";

    public static string For(BucketViewer value) => $"{value.BucketId}-{value.ViewerId}";

    public static string For(Tag value) => $"{value.BucketId}-{value.TagName}";

    public static string For(TagMessageCount value) => $"{value.BucketId}-{value.TagName}";

    public static string For(Message value) => $"{value.BucketId}-{value.MessageId}";

    /// <summary>
    /// Splits a "&lt;bucketId&gt;-&lt;rest&gt;" id on the first hyphen only, because tag names may
    /// themselves contain hyphens.
    /// </summary>
    public static bool TrySplit(string id, out long bucketId, out string rest)
    {
        bucketId = 0;
        rest = string.Empty;

        var separator = id.IndexOf('-');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(id[..separator], out bucketId))
        {
            return false;
        }

        rest = id[(separator + 1)..];
        return true;
    }
}

public sealed class NamespaceType : ObjectType<Namespace>
{
    protected override void Configure(IObjectTypeDescriptor<Namespace> descriptor)
    {
        descriptor.Name("Namespace");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<Namespace>()));
        descriptor.Field(f => f.NamespaceId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Managers).Type<NonNullType<ListType<NonNullType<NamespaceManagerType>>>>()
            .Resolve(ctx => Load(ctx, db => db.NamespaceManagers
                .Where(m => m.NamespaceId == ctx.Parent<Namespace>().NamespaceId)));
        descriptor.Field(f => f.Buckets).Type<NonNullType<ListType<NonNullType<BucketType>>>>()
            .Resolve(ctx => Load(ctx, db => db.Buckets
                .Where(b => b.NamespaceId == ctx.Parent<Namespace>().NamespaceId)));
    }

    private static Task<List<T>> Load<T>(IResolverContext ctx, Func<BucketDbContext, IQueryable<T>> query)
        => query(ctx.Service<BucketDbContext>()).ToListAsync(ctx.RequestAborted);
}

public sealed class NamespaceManagerType : ObjectType<NamespaceManager>
{
    protected override void Configure(IObjectTypeDescriptor<NamespaceManager> descriptor)
    {
        descriptor.Name("NamespaceManager");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<NamespaceManager>()));
        descriptor.Field(f => f.NamespaceId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Namespace).Type<NonNullType<NamespaceType>>()
            .Resolve(ctx => ctx.Service<BucketDbContext>().Namespaces
                .FirstOrDefaultAsync(n => n.NamespaceId == ctx.Parent<NamespaceManager>().NamespaceId,
                    ctx.RequestAborted));
    }
}

public sealed class BucketType : ObjectType<Bucket>
{
    protected override void Configure(IObjectTypeDescriptor<Bucket> descriptor)
    {
        descriptor.Name("Bucket");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<Bucket>()));
        descriptor.Field(f => f.BucketId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.NamespaceId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.NextMessageId).Ignore();

        // The relation the indexer's schema declared via @derivedFrom but never actually had:
        // Bucket carried only a scalar namespaceId.
        descriptor.Field(f => f.Namespace).Type<NonNullType<NamespaceType>>()
            .Resolve(ctx => ctx.Service<BucketDbContext>().Namespaces
                .FirstOrDefaultAsync(n => n.NamespaceId == ctx.Parent<Bucket>().NamespaceId,
                    ctx.RequestAborted));

        descriptor.Field(f => f.Admins).Type<NonNullType<ListType<NonNullType<BucketAdminType>>>>()
            .Resolve(ctx => Load(ctx, db => db.BucketAdmins
                .Where(a => a.BucketId == ctx.Parent<Bucket>().BucketId)));
        descriptor.Field(f => f.Contributors)
            .Type<NonNullType<ListType<NonNullType<BucketContributorType>>>>()
            .Resolve(ctx => Load(ctx, db => db.BucketContributors
                .Where(c => c.BucketId == ctx.Parent<Bucket>().BucketId)));
        descriptor.Field(f => f.Viewers).Type<NonNullType<ListType<NonNullType<BucketViewerType>>>>()
            .Resolve(ctx => Load(ctx, db => db.BucketViewers
                .Where(v => v.BucketId == ctx.Parent<Bucket>().BucketId)));
        descriptor.Field(f => f.Tags).Type<NonNullType<ListType<NonNullType<TagType>>>>()
            .Resolve(ctx => Load(ctx, db => db.Tags
                .Where(t => t.BucketId == ctx.Parent<Bucket>().BucketId)));
        descriptor.Field(f => f.Messages).Type<NonNullType<ListType<NonNullType<MessageType>>>>()
            .Resolve(ctx => Load(ctx, db => db.Messages
                .Where(m => m.BucketId == ctx.Parent<Bucket>().BucketId)));
    }

    private static Task<List<T>> Load<T>(IResolverContext ctx, Func<BucketDbContext, IQueryable<T>> query)
        => query(ctx.Service<BucketDbContext>()).ToListAsync(ctx.RequestAborted);
}

public sealed class BucketAdminType : ObjectType<BucketAdmin>
{
    protected override void Configure(IObjectTypeDescriptor<BucketAdmin> descriptor)
    {
        descriptor.Name("BucketAdmin");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<BucketAdmin>()));
        descriptor.Field(f => f.BucketId).Name("bucketIdNumber").Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketOf(ctx, ctx.Parent<BucketAdmin>().BucketId));
    }

    internal static Task<Bucket?> BucketOf(IResolverContext ctx, long bucketId)
        => ctx.Service<BucketDbContext>().Buckets
            .FirstOrDefaultAsync(b => b.BucketId == bucketId, ctx.RequestAborted);
}

public sealed class BucketContributorType : ObjectType<BucketContributor>
{
    protected override void Configure(IObjectTypeDescriptor<BucketContributor> descriptor)
    {
        descriptor.Name("BucketContributor");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<BucketContributor>()));
        descriptor.Field(f => f.BucketId).Name("bucketIdNumber").Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketAdminType.BucketOf(ctx, ctx.Parent<BucketContributor>().BucketId));
    }
}

public sealed class BucketViewerType : ObjectType<BucketViewer>
{
    protected override void Configure(IObjectTypeDescriptor<BucketViewer> descriptor)
    {
        descriptor.Name("BucketViewer");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<BucketViewer>()));
        descriptor.Field(f => f.BucketId).Name("bucketIdNumber").Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketAdminType.BucketOf(ctx, ctx.Parent<BucketViewer>().BucketId));
    }
}

public sealed class TagType : ObjectType<Tag>
{
    protected override void Configure(IObjectTypeDescriptor<Tag> descriptor)
    {
        descriptor.Name("Tag");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<Tag>()));
        descriptor.Field(f => f.BucketId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketAdminType.BucketOf(ctx, ctx.Parent<Tag>().BucketId));

        // Denormalised in the indexer; here it reads through to the counter table.
        descriptor.Field("messageCount").Type<IntType>()
            .Resolve(async ctx =>
            {
                var tag = ctx.Parent<Tag>();
                var counter = await ctx.Service<BucketDbContext>().TagMessageCounts
                    .FirstOrDefaultAsync(
                        c => c.BucketId == tag.BucketId && c.TagName == tag.TagName,
                        ctx.RequestAborted);
                return counter?.Count ?? 0;
            });
    }
}

public sealed class TagMessageCountType : ObjectType<TagMessageCount>
{
    protected override void Configure(IObjectTypeDescriptor<TagMessageCount> descriptor)
    {
        descriptor.Name("TagMessageCount");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<TagMessageCount>()));
        descriptor.Field(f => f.BucketId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketAdminType.BucketOf(ctx, ctx.Parent<TagMessageCount>().BucketId));
    }
}

public sealed class MessageType : ObjectType<Message>
{
    protected override void Configure(IObjectTypeDescriptor<Message> descriptor)
    {
        descriptor.Name("Message");
        descriptor.Field("id").Type<NonNullType<IdType>>()
            .Resolve(ctx => EntityId.For(ctx.Parent<Message>()));
        descriptor.Field(f => f.MessageId).Type<NonNullType<BigIntType>>();
        descriptor.Field(f => f.BucketId).Ignore();

        // The indexer exposed the same value twice; kept so existing queries keep resolving.
        descriptor.Field("messageIdNumber").Type<NonNullType<BigIntType>>()
            .Resolve(ctx => ctx.Parent<Message>().MessageId);

        descriptor.Field(f => f.Bucket).Type<NonNullType<BucketType>>()
            .Resolve(ctx => BucketAdminType.BucketOf(ctx, ctx.Parent<Message>().BucketId));
    }
}
