using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Entities;

namespace XcavateBuckets.Domain.Services;

/// <summary>
/// Namespace lifecycle and manager membership. Ports <c>do_create_namespace</c>,
/// <c>do_add_manager</c>, <c>do_remove_manager</c> and <c>do_delete_namespace</c>.
/// </summary>
public class NamespaceService(
    BucketDbContext db,
    AuthorizationService auth,
    InputValidator validator,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a namespace and installs the caller as its first manager, mirroring
    /// <c>do_create_namespace</c> calling <c>do_add_manager</c>.
    /// </summary>
    public async Task<Namespace> CreateAsync(
        string caller,
        string name,
        string? schemaUri,
        IEnumerable<KeyValuePair<string, string>>? properties,
        CancellationToken ct)
    {
        validator.Required(name, validator.Options.MaxNameLen, "name");
        validator.Text(schemaUri, validator.Options.MaxUriLen, "schemaUri");
        var propertiesJson = validator.PropertiesJson(properties);

        var now = clock.GetUtcNow().UtcDateTime;

        var entity = new Namespace
        {
            Name = name,
            SchemaUri = schemaUri,
            Properties = propertiesJson,
            Creator = caller,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Namespaces.Add(entity);
        await db.SaveChangesAsync(ct);

        db.NamespaceManagers.Add(new NamespaceManager
        {
            NamespaceId = entity.NamespaceId,
            Manager = caller,
            AddedAt = now
        });
        await db.SaveChangesAsync(ct);

        return entity;
    }

    /// <summary>Adds a manager. The caller must already be a manager of the namespace.</summary>
    public async Task<NamespaceManager> AddManagerAsync(
        string caller,
        long namespaceId,
        string newManager,
        CancellationToken ct)
    {
        validator.Required(newManager, validator.Options.MaxNameLen, "newManager");
        await auth.EnsureNamespaceExistsAsync(namespaceId, ct);
        await auth.EnsureIsManagerAsync(namespaceId, caller, ct);

        var existing = await db.NamespaceManagers
            .FirstOrDefaultAsync(m => m.NamespaceId == namespaceId && m.Manager == newManager, ct);

        // The pallet uses `insert`, which overwrites rather than failing on a duplicate.
        if (existing is not null)
        {
            return existing;
        }

        var entity = new NamespaceManager
        {
            NamespaceId = namespaceId,
            Manager = newManager,
            AddedAt = clock.GetUtcNow().UtcDateTime
        };

        db.NamespaceManagers.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    /// <summary>
    /// Removes a manager. The pallet counts managers before removing and refuses when only one
    /// exists, so a namespace can never be left unmanaged.
    /// </summary>
    public async Task RemoveManagerAsync(
        string caller,
        long namespaceId,
        string oldManager,
        CancellationToken ct)
    {
        await auth.EnsureNamespaceExistsAsync(namespaceId, ct);
        await auth.EnsureIsManagerAsync(namespaceId, caller, ct);

        var managerCount = await db.NamespaceManagers
            .CountAsync(m => m.NamespaceId == namespaceId, ct);

        if (managerCount <= 1)
        {
            throw BucketException.LastManagerRemoval();
        }

        var entity = await db.NamespaceManagers
            .FirstOrDefaultAsync(m => m.NamespaceId == namespaceId && m.Manager == oldManager, ct);

        // The pallet's `remove` on an absent key is a no-op.
        if (entity is null)
        {
            return;
        }

        db.NamespaceManagers.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds a manager without a caller check. Ports <c>force_add_manager</c>.</summary>
    public async Task ForceAddManagerAsync(long namespaceId, string manager, CancellationToken ct)
    {
        validator.Required(manager, validator.Options.MaxNameLen, "manager");
        await auth.EnsureNamespaceExistsAsync(namespaceId, ct);

        var exists = await db.NamespaceManagers
            .AnyAsync(m => m.NamespaceId == namespaceId && m.Manager == manager, ct);

        if (exists)
        {
            return;
        }

        db.NamespaceManagers.Add(new NamespaceManager
        {
            NamespaceId = namespaceId,
            Manager = manager,
            AddedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a namespace. Ports <c>do_delete_namespace</c>, which checks for dangling children
    /// before looking the namespace up — so the dangling errors win over
    /// <see cref="BucketErrorCode.UnknownNamespace"/>.
    /// </summary>
    public async Task ForceRemoveAsync(long namespaceId, CancellationToken ct)
    {
        if (await db.Buckets.AnyAsync(b => b.NamespaceId == namespaceId, ct))
        {
            throw BucketException.DanglingBuckets();
        }

        if (await db.NamespaceManagers.AnyAsync(m => m.NamespaceId == namespaceId, ct))
        {
            throw BucketException.DanglingManagers();
        }

        var entity = await db.Namespaces
            .FirstOrDefaultAsync(n => n.NamespaceId == namespaceId, ct)
            ?? throw BucketException.UnknownNamespace();

        db.Namespaces.Remove(entity);
        await db.SaveChangesAsync(ct);
    }
}
