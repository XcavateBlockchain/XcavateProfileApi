# Bucket Pallet GraphQL API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port Substrate `pallet-bucket` into a standalone C# Hot Chocolate GraphQL API that owns its data in PostgreSQL, serves the SubQuery indexer's entity shapes for reads, and gates 20 mutations behind the repo's existing sr25519 header auth.

**Architecture:** A dependency-free domain library (`XcavateBuckets.Domain`) holds EF Core entities and one service per pallet aggregate, each service reproducing the corresponding `functions.rs` rule exactly. The existing `XcavateProfileApi` host mounts `/graphql`, translating GraphQL calls into domain service calls and domain exceptions into GraphQL errors with stable `code` extensions.

**Tech Stack:** .NET 10, Hot Chocolate 16.5.1, EF Core 10 + Npgsql, PostgreSQL 15, NUnit 4.2.2, StrawberryShake 16.5.1.

**Spec:** `docs/superpowers/specs/2026-07-25-bucket-pallet-graphql-api-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- Target framework `net10.0`; `<Nullable>enable</Nullable>`; `<ImplicitUsings>enable</ImplicitUsings>`.
- Package versions, pinned exactly: `HotChocolate.AspNetCore` 16.5.1, `HotChocolate.Data.EntityFramework` 16.5.1, `StrawberryShake.Server` 16.5.1, `Microsoft.EntityFrameworkCore` 10.0.0, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0, `NUnit` 4.2.2, `NUnit3TestAdapter` 4.6.0, `Microsoft.NET.Test.Sdk` 17.12.0.
- `XcavateBuckets.Domain` must NOT reference ASP.NET Core, Hot Chocolate, or `Substrate.NET.API`. Verified by the absence of those `PackageReference` entries.
- SubjectId is the caller's SS58 address verbatim. No DID layer.
- `ViewerId` is a 32-byte hex X25519 public key. `encryptionKey` and `contentHash` are 32-byte hex.
- Every mutation runs inside one `IDbContextTransaction`.
- All ids (`namespaceId`, `bucketId`, `messageId`) are `long` in C# and the `BigInt` GraphQL scalar (string on the wire).
- No fees. No `UNABLE_TO_PAY_FEES` error. No outbound IPFS requests.
- Reads are unauthenticated. Mutations require a valid signature; `force*` mutations additionally require an admin address.

### Verified Hot Chocolate 16 API facts

These were confirmed by building and running a probe against 16.5.1 on net10.0. HC 16 renamed the
scalar API from HC 13/14 — do not use `ParseLiteral`/`ParseValue`/`ParseResult`, they do not exist.

- `ScalarType<TRuntime, TLiteral>` ctor: `base(string name, BindingBehavior bind)`.
- Abstract members to override:
  - `protected override long OnCoerceInputLiteral(StringValueNode valueLiteral)`
  - `protected override long OnCoerceInputValue(JsonElement inputValue, IFeatureProvider context)`
  - `protected override void OnCoerceOutputValue(long runtimeValue, ResultElement resultValue)`
  - `protected override StringValueNode OnValueToLiteral(long runtimeValue)`
- Write a string result with `resultValue.SetStringValue(text, false)`.
- Required usings: `HotChocolate.Execution`, `HotChocolate.Features`, `HotChocolate.Language`, `HotChocolate.Text.Json`.
- `HotChocolate.SerializationException` does **not** exist in 16. Throw `GraphQLException`.
- `totalCount` is NOT emitted by default. Use `[UsePaging(IncludeTotalCount = true)]`.
- Register with `.AddFiltering().AddSorting().AddProjections()` and `.BindRuntimeType<long, BigIntType>()`.
- A `long` field's generated filter input is `LongOperationFilterInput`, even though the output scalar is `BigInt`.
- `[UsePaging]` emits `nodes`, `edges`, `pageInfo`, and `@listSize`/`@cost` directives.

---

## File Structure

**Create — `src/XcavateBuckets.Domain/`**

| File | Responsibility |
|---|---|
| `XcavateBuckets.Domain.csproj` | Class library, EF Core only |
| `Entities/Namespace.cs` | Namespace row |
| `Entities/NamespaceManager.cs` | (namespaceId, manager) pair |
| `Entities/Bucket.cs` | Bucket row, incl. `IsWritable`/`EncryptionKey`/`NextMessageId` |
| `Entities/BucketAdmin.cs` | (bucketId, subjectId) pair |
| `Entities/BucketContributor.cs` | (bucketId, subjectId) pair |
| `Entities/BucketViewer.cs` | (bucketId, viewerId) pair |
| `Entities/Tag.cs` | (bucketId, tagName) pair + creator |
| `Entities/TagMessageCount.cs` | (bucketId, tagName) → count |
| `Entities/Message.cs` | Message row |
| `Data/BucketDbContext.cs` | DbSets, keys, FKs, indexes, `__EFMigrationsHistory_Buckets` |
| `BucketOptions.cs` | Validation limits |
| `BucketErrorCode.cs` | Enum of stable error codes |
| `BucketException.cs` | Domain exception carrying a `BucketErrorCode` |
| `InputValidator.cs` | Length + hex-width checks → `INVALID_INPUT` |
| `Services/AuthorizationService.cs` | `IsManager`/`IsAdmin`/`IsContributor` + `Ensure*` |
| `Services/NamespaceService.cs` | create, addManager, removeManager, forceRemove, forceAddManager |
| `Services/BucketService.cs` | create, pauseWriting, resumeWriting, rotateKey, forceRemove |
| `Services/MembershipService.cs` | admin/contributor/viewer add + remove |
| `Services/TagService.cs` | createTag, forceRemoveTag |
| `Services/MessageService.cs` | write, forceRemoveMessage, tag counters |

**Create — `src/XcavateProfileApi/GraphQL/`**

| File | Responsibility |
|---|---|
| `BigIntType.cs` | The custom scalar |
| `BucketQueries.cs` | 9 plural connections + 4 singular lookups |
| `BucketMutations.cs` | 20 mutations, thin wrappers over domain services |
| `Inputs.cs` | `PropertyInput`, `NamespaceMetadataInput`, `BucketMetadataInput`, `MessageMetadataInput`, `MessageInput` |
| `NodeResolvers.cs` | DataLoader-backed relation resolvers |
| `BucketErrorFilter.cs` | `BucketException` → GraphQL error + `code` extension |
| `Auth/ICallerContext.cs` | Scoped authenticated-caller accessor |
| `Auth/CallerContext.cs` | Implementation |
| `Auth/GraphQLSignatureMiddleware.cs` | Verifies X-\* headers over the raw body |
| `Auth/RequireSignatureAttribute.cs` | Field middleware: rejects anonymous |
| `Auth/RequireAdminAttribute.cs` | Field middleware: rejects non-admin |

**Create — `tests/XcavateBuckets.Tests/`** — one fixture per domain service, SQLite-in-memory backed.

**Modify**
- `src/XcavateProfileApi/XcavateProfileApi.csproj` — add HC packages + domain project reference
- `src/XcavateProfileApi/Program.cs:41-74` — register `BucketDbContext`, GraphQL server, caller context
- `src/XcavateProfileApi/Program.cs:96-129` — migrate `BucketDbContext` alongside `ProfileDbContext`
- `XcavateProfile.sln` — add the two new projects

---

## Task 1: Domain project, entities, DbContext, migration

**Files:**
- Create: `src/XcavateBuckets.Domain/XcavateBuckets.Domain.csproj`, all 9 `Entities/*.cs`, `Data/BucketDbContext.cs`
- Create: `tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`, `tests/XcavateBuckets.Tests/SchemaTests.cs`
- Modify: `XcavateProfile.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `BucketDbContext` with `DbSet<Namespace> Namespaces`, `DbSet<NamespaceManager> NamespaceManagers`, `DbSet<Bucket> Buckets`, `DbSet<BucketAdmin> BucketAdmins`, `DbSet<BucketContributor> BucketContributors`, `DbSet<BucketViewer> BucketViewers`, `DbSet<Tag> Tags`, `DbSet<TagMessageCount> TagMessageCounts`, `DbSet<Message> Messages`.

Entity shapes that later tasks depend on:

```csharp
public class Namespace
{
    public long NamespaceId { get; set; }          // identity PK
    public string? Name { get; set; }
    public string? SchemaUri { get; set; }
    public string? Properties { get; set; }        // jsonb, JSON-encoded map
    public string? Creator { get; set; }           // SS58
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<NamespaceManager> Managers { get; set; } = [];
    public List<Bucket> Buckets { get; set; } = [];
}

public class NamespaceManager
{
    public long NamespaceId { get; set; }
    public Namespace Namespace { get; set; } = null!;
    public string Manager { get; set; } = "";      // SS58
    public DateTime AddedAt { get; set; }
}

public class Bucket
{
    public long BucketId { get; set; }             // identity PK, global
    public long NamespaceId { get; set; }
    public Namespace Namespace { get; set; } = null!;
    public string? Creator { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Properties { get; set; }
    public bool IsWritable { get; set; }           // false on create (Status::Locked)
    public string? EncryptionKey { get; set; }      // 32-byte hex, non-null iff IsWritable
    public long NextMessageId { get; set; }        // per-bucket counter, starts 0
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<BucketAdmin> Admins { get; set; } = [];
    public List<BucketContributor> Contributors { get; set; } = [];
    public List<BucketViewer> Viewers { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<Message> Messages { get; set; } = [];
}

public class BucketAdmin       { public long BucketId; public Bucket Bucket; public string SubjectId; public DateTime AddedAt; }
public class BucketContributor { public long BucketId; public Bucket Bucket; public string SubjectId; public DateTime AddedAt; }
public class BucketViewer      { public long BucketId; public Bucket Bucket; public string ViewerId;  public DateTime AddedAt; }
// (declare these three as real properties, matching NamespaceManager's style)

public class Tag
{
    public long BucketId { get; set; }
    public Bucket Bucket { get; set; } = null!;
    public string TagName { get; set; } = "";
    public string? Creator { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TagMessageCount
{
    public long BucketId { get; set; }
    public Bucket Bucket { get; set; } = null!;
    public string TagName { get; set; } = "";
    public int Count { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Message
{
    public long BucketId { get; set; }
    public Bucket Bucket { get; set; } = null!;
    public long MessageId { get; set; }            // composite PK with BucketId
    public string Contributor { get; set; } = "";  // SS58
    public string? Reference { get; set; }
    public string? Tag { get; set; }
    public string? Description { get; set; }
    public string? ContentType { get; set; }
    public string? ContentHash { get; set; }
    public string? Properties { get; set; }
    public string? IpfsContent { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

`OnModelCreating` requirements:
- Composite keys: `NamespaceManager(NamespaceId, Manager)`, `BucketAdmin(BucketId, SubjectId)`, `BucketContributor(BucketId, SubjectId)`, `BucketViewer(BucketId, ViewerId)`, `Tag(BucketId, TagName)`, `TagMessageCount(BucketId, TagName)`, `Message(BucketId, MessageId)`.
- `Namespace.NamespaceId` and `Bucket.BucketId` are `ValueGeneratedOnAdd()` identity columns.
- All FKs `DeleteBehavior.Restrict` — the pallet's dangling-resource errors are explicit checks, and `Restrict` makes an accidental cascade impossible.
- Indexes matching the indexer's `@index`: `Bucket.NamespaceId`, `NamespaceManager.Manager`, `BucketAdmin.SubjectId`, `BucketContributor.SubjectId`, `BucketViewer.ViewerId`, `Tag.TagName`, `TagMessageCount.TagName`, `Message.MessageId`.
- `Properties` columns mapped `.HasColumnType("jsonb")`.

- [ ] **Step 1: Create the two projects and add them to the solution**

```bash
cd /p/programming/XcavateProfile
dotnet new classlib -o src/XcavateBuckets.Domain -f net10.0
dotnet new nunit    -o tests/XcavateBuckets.Tests -f net10.0
dotnet sln add src/XcavateBuckets.Domain tests/XcavateBuckets.Tests
dotnet add src/XcavateBuckets.Domain package Microsoft.EntityFrameworkCore --version 10.0.0
dotnet add src/XcavateBuckets.Domain package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.0
dotnet add tests/XcavateBuckets.Tests reference src/XcavateBuckets.Domain
dotnet add tests/XcavateBuckets.Tests package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.0
```

- [ ] **Step 2: Write the failing test** — `tests/XcavateBuckets.Tests/SchemaTests.cs`

Proves the model builds, keys are right, and `Restrict` is set everywhere.

```csharp
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;

namespace XcavateBuckets.Tests;

public class SchemaTests
{
    private static BucketDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<BucketDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        return new BucketDbContext(options);
    }

    [Test]
    public void Model_builds_with_expected_composite_keys()
    {
        using var db = NewContext();
        var model = db.Model;

        Assert.That(model.FindEntityType(typeof(Domain.Entities.NamespaceManager))!
            .FindPrimaryKey()!.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "NamespaceId", "Manager" }));

        Assert.That(model.FindEntityType(typeof(Domain.Entities.Message))!
            .FindPrimaryKey()!.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "BucketId", "MessageId" }));

        Assert.That(model.FindEntityType(typeof(Domain.Entities.BucketViewer))!
            .FindPrimaryKey()!.Properties.Select(p => p.Name),
            Is.EquivalentTo(new[] { "BucketId", "ViewerId" }));
    }

    [Test]
    public void All_foreign_keys_restrict_deletes()
    {
        using var db = NewContext();
        var cascading = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.DeleteBehavior != DeleteBehavior.Restrict)
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name}.{fk.GetConstraintName()}")
            .ToList();

        Assert.That(cascading, Is.Empty,
            "pallet parity: dangling-resource rules are explicit checks, never DB cascades");
    }

    [Test]
    public void Schema_can_be_created()
    {
        using var db = NewContext();
        db.Database.OpenConnection();
        Assert.That(db.Database.EnsureCreated(), Is.True);
    }
}
```

- [ ] **Step 3: Run it to make sure it fails**

Run: `dotnet test tests/XcavateBuckets.Tests --filter SchemaTests`
Expected: FAIL — `BucketDbContext` does not exist.

- [ ] **Step 4: Write the entities and DbContext**

Nine entity files as specified above, plus `Data/BucketDbContext.cs` implementing the
`OnModelCreating` requirements listed above.

- [ ] **Step 5: Run the tests and make sure they pass**

Run: `dotnet test tests/XcavateBuckets.Tests --filter SchemaTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Generate the initial migration**

```bash
cd /p/programming/XcavateProfile
dotnet ef migrations add InitBuckets \
  --project src/XcavateBuckets.Domain \
  --startup-project src/XcavateProfileApi \
  --context BucketDbContext
```

Note: this requires Task 9's `Program.cs` registration to resolve `BucketDbContext`. If run before
Task 9, add a temporary `IDesignTimeDbContextFactory<BucketDbContext>` in the domain project and
delete it once Task 9 lands.

- [ ] **Step 7: Commit**

```bash
git add src/XcavateBuckets.Domain tests/XcavateBuckets.Tests XcavateProfile.sln
git commit -m "feat: add bucket domain entities and DbContext"
```

---

## Task 2: Options, error codes, exception, input validation

**Files:**
- Create: `src/XcavateBuckets.Domain/BucketOptions.cs`, `BucketErrorCode.cs`, `BucketException.cs`, `InputValidator.cs`
- Test: `tests/XcavateBuckets.Tests/InputValidatorTests.cs`

**Interfaces:**
- Produces:
  - `BucketOptions` with `int MaxNameLen = 256, MaxUriLen = 512, MaxCategoryLen = 64, MaxProperties = 32, MaxPropertyKeyLen = 64, MaxPropertyValueLen = 512, MaxTagLen = 64, MaxReferenceLen = 512, MaxIpfsContentLen = 1048576`
  - `enum BucketErrorCode` — 19 pallet codes + `InvalidInput`
  - `BucketException(BucketErrorCode code, string message)` with `public BucketErrorCode Code { get; }`, plus static factories `BucketException.NotAdmin()`, `.NotManager()`, `.NotContributor()`, `.UnknownNamespace()`, `.UnknownBucket()`, `.UnknownMessage()`, `.UnknownTag()`, `.BucketIsLocked()`, `.NamespaceAlreadyExists()`, `.LastManagerRemoval()`, `.DanglingBuckets()`, `.DanglingMessages()`, `.DanglingAdmins()`, `.DanglingContributors()`, `.DanglingViewers()`, `.DanglingManagers()`, `.DanglingTags()`, `.ArithmeticOverflow()`, `.ArithmeticUnderflow()`, `.InvalidInput(string detail)`
  - `InputValidator(BucketOptions options)` with `void Text(string? value, int maxLen, string field)`, `void Hex32(string? value, string field)`, `string? PropertiesJson(IEnumerable<KeyValuePair<string,string>>? props)` returning canonical JSON or null

`BucketErrorCode` values map to the spec's SCREAMING_SNAKE strings via
`code.ToString()` converted with a `ToErrorCode()` extension (e.g. `NotAdmin` → `NOT_ADMIN`).

- [ ] **Step 1: Write the failing tests**

```csharp
using XcavateBuckets.Domain;

namespace XcavateBuckets.Tests;

public class InputValidatorTests
{
    private static InputValidator NewValidator() => new(new BucketOptions());

    [Test]
    public void Text_accepts_value_at_the_limit()
    {
        var v = NewValidator();
        Assert.DoesNotThrow(() => v.Text(new string('a', 256), 256, "name"));
    }

    [Test]
    public void Text_rejects_value_over_the_limit()
    {
        var v = NewValidator();
        var ex = Assert.Throws<BucketException>(() => v.Text(new string('a', 257), 256, "name"))!;
        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
        Assert.That(ex.Message, Does.Contain("name"));
    }

    [TestCase("0x" + "ab", false)]                                   // too short
    [TestCase("not-hex-at-all", false)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", true)]
    public void Hex32_enforces_exactly_32_bytes(string value, bool valid)
    {
        var v = NewValidator();
        if (valid) Assert.DoesNotThrow(() => v.Hex32(value, "encryptionKey"));
        else Assert.Throws<BucketException>(() => v.Hex32(value, "encryptionKey"));
    }

    [Test]
    public void PropertiesJson_returns_null_for_no_properties()
        => Assert.That(NewValidator().PropertiesJson(null), Is.Null);

    [Test]
    public void PropertiesJson_serialises_sorted_by_key()
    {
        var json = NewValidator().PropertiesJson(new Dictionary<string, string>
        {
            ["zeta"] = "2", ["alpha"] = "1"
        });
        Assert.That(json, Is.EqualTo("""{"alpha":"1","zeta":"2"}"""));
    }

    [Test]
    public void PropertiesJson_rejects_too_many_properties()
    {
        var many = Enumerable.Range(0, 33).ToDictionary(i => $"k{i}", i => "v");
        var ex = Assert.Throws<BucketException>(() => NewValidator().PropertiesJson(many))!;
        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void ToErrorCode_screaming_snake_cases_the_enum()
        => Assert.That(BucketErrorCode.NotAdmin.ToErrorCode(), Is.EqualTo("NOT_ADMIN"));
}
```

Sorting properties by key is deliberate: the pallet's `BoundedBTreeMap` is ordered, so a
key-sorted JSON encoding keeps the stored value canonical and comparable.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/XcavateBuckets.Tests --filter InputValidatorTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the four files**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/XcavateBuckets.Tests --filter InputValidatorTests`
Expected: PASS (9 test cases)

- [ ] **Step 5: Commit**

```bash
git add src/XcavateBuckets.Domain tests/XcavateBuckets.Tests
git commit -m "feat: add bucket options, error codes and input validation"
```

---

## Task 3: AuthorizationService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/AuthorizationService.cs`
- Test: `tests/XcavateBuckets.Tests/AuthorizationServiceTests.cs`, `tests/XcavateBuckets.Tests/TestDb.cs`

**Interfaces:**
- Consumes: `BucketDbContext`, `BucketException`.
- Produces: `AuthorizationService(BucketDbContext db)` with
  - `Task<bool> IsManagerAsync(long namespaceId, string subject, CancellationToken ct)`
  - `Task<bool> IsAdminAsync(long bucketId, string subject, CancellationToken ct)`
  - `Task<bool> IsContributorAsync(long bucketId, string subject, CancellationToken ct)`
  - `Task<bool> IsViewerAsync(long bucketId, string viewerId, CancellationToken ct)`
  - `Task EnsureIsManagerAsync(...)` → throws `NOT_MANAGER`
  - `Task EnsureIsAdminAsync(...)` → throws `NOT_ADMIN`
  - `Task EnsureIsContributorAsync(...)` → throws `NOT_CONTRIBUTOR`
  - `Task EnsureNamespaceExistsAsync(long namespaceId, ...)` → throws `UNKNOWN_NAMESPACE`
  - `Task<Bucket> GetBucketAsync(long namespaceId, long bucketId, ...)` → throws `UNKNOWN_BUCKET` when the bucket is missing **or belongs to a different namespace**

Also create the shared test helper both this and every later service fixture uses:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XcavateBuckets.Domain.Data;

namespace XcavateBuckets.Tests;

/// One open in-memory SQLite connection per context, disposed with it.
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    public BucketDbContext Db { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Db = new BucketDbContext(new DbContextOptionsBuilder<BucketDbContext>()
            .UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();
    }

    public void Dispose() { Db.Dispose(); _connection.Dispose(); }
}
```

- [ ] **Step 1: Write the failing tests**

Cases: `IsManagerAsync` true/false; `IsAdminAsync` true/false; `IsContributorAsync` true/false;
`EnsureIsManagerAsync` throws `NOT_MANAGER` for a non-manager and does not throw for a manager;
`EnsureIsAdminAsync` throws `NOT_ADMIN`; `EnsureIsContributorAsync` throws `NOT_CONTRIBUTOR`;
`EnsureNamespaceExistsAsync` throws `UNKNOWN_NAMESPACE` for a missing id;
`GetBucketAsync` returns the bucket for a matching namespace, throws `UNKNOWN_BUCKET` for a
missing bucket, and throws `UNKNOWN_BUCKET` when the bucket exists under a *different* namespace
(this last one is the pallet's `Buckets::contains_key(namespace_id, bucket_id)` semantics and is
easy to get wrong).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/XcavateBuckets.Tests --filter AuthorizationServiceTests`
Expected: FAIL — `AuthorizationService` does not exist.

- [ ] **Step 3: Implement `AuthorizationService`**

- [ ] **Step 4: Run to verify they pass**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat: add bucket authorization service"
```

---

## Task 4: NamespaceService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/NamespaceService.cs`
- Test: `tests/XcavateBuckets.Tests/NamespaceServiceTests.cs`

**Interfaces:**
- Consumes: `BucketDbContext`, `AuthorizationService`, `InputValidator`, `TimeProvider`.
- Produces: `NamespaceService(BucketDbContext db, AuthorizationService auth, InputValidator validator, TimeProvider clock)` with
  - `Task<Namespace> CreateAsync(string caller, string name, string? schemaUri, IEnumerable<KeyValuePair<string,string>>? properties, CancellationToken ct)`
  - `Task<NamespaceManager> AddManagerAsync(string caller, long namespaceId, string newManager, CancellationToken ct)`
  - `Task RemoveManagerAsync(string caller, long namespaceId, string oldManager, CancellationToken ct)`
  - `Task ForceAddManagerAsync(long namespaceId, string manager, CancellationToken ct)`
  - `Task ForceRemoveAsync(long namespaceId, CancellationToken ct)`

Rules, from `do_create_namespace` / `do_add_manager` / `do_remove_manager` / `do_delete_namespace`:

| Method | Behaviour |
|---|---|
| `CreateAsync` | validates name/uri/properties; inserts namespace; inserts `caller` as first manager; sets `Creator = caller` |
| `AddManagerAsync` | namespace must exist; caller must be manager; upsert is idempotent (pallet uses `insert`) |
| `RemoveManagerAsync` | namespace must exist; caller must be manager; **at least 2 managers must exist** else `LAST_MANAGER_REMOVAL`; then delete |
| `ForceAddManagerAsync` | namespace must exist; no caller check |
| `ForceRemoveAsync` | `DANGLING_BUCKETS` if any bucket; `DANGLING_MANAGERS` if any manager; else `UNKNOWN_NAMESPACE` if missing, then delete |

Note the ordering in `ForceRemoveAsync`: the pallet checks dangling buckets and managers *before*
looking the namespace up, so a namespace that never existed but somehow has children reports the
dangling error first. Reproduce that order.

- [ ] **Step 1: Write the failing tests**

One test per table row plus: `CreateAsync` makes the caller a manager; `CreateAsync` assigns
increasing ids across two calls; `AddManagerAsync` from a non-manager throws `NOT_MANAGER`;
`AddManagerAsync` on a missing namespace throws `UNKNOWN_NAMESPACE`; `RemoveManagerAsync` with a
single manager throws `LAST_MANAGER_REMOVAL`; `RemoveManagerAsync` with two managers succeeds and
leaves one; `ForceRemoveAsync` with a bucket throws `DANGLING_BUCKETS`; `ForceRemoveAsync` with
only a manager left throws `DANGLING_MANAGERS`; `ForceRemoveAsync` on a clean namespace deletes it.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `NamespaceService`**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add namespace service with pallet parity rules"`

---

## Task 5: BucketService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/BucketService.cs`
- Test: `tests/XcavateBuckets.Tests/BucketServiceTests.cs`

**Interfaces:**
- Produces: `BucketService(BucketDbContext db, AuthorizationService auth, InputValidator validator, TimeProvider clock)` with
  - `Task<Bucket> CreateAsync(string caller, long namespaceId, string name, string category, IEnumerable<KeyValuePair<string,string>>? properties, CancellationToken ct)`
  - `Task<Bucket> PauseWritingAsync(string caller, long namespaceId, long bucketId, CancellationToken ct)`
  - `Task<Bucket> ResumeWritingAsync(string caller, long namespaceId, long bucketId, string newEncryptionKey, CancellationToken ct)`
  - `Task<Bucket> RotateKeyAsync(string caller, long namespaceId, long bucketId, string newEncryptionKey, CancellationToken ct)`
  - `Task ForceRemoveAsync(long namespaceId, long bucketId, CancellationToken ct)`

Rules, from `do_create_bucket` / `do_lock_bucket` / `do_set_key` / `do_delete_bucket`:

| Method | Behaviour |
|---|---|
| `CreateAsync` | namespace must exist; caller must be **manager of the namespace**; new bucket is `IsWritable = false`, `EncryptionKey = null`, `NextMessageId = 0` |
| `PauseWritingAsync` | caller must be admin of bucket; sets `IsWritable = false`; leaves `EncryptionKey` as-is |
| `ResumeWritingAsync` | caller must be admin; validates 32-byte hex; sets `IsWritable = true` + key; **allowed while locked** |
| `RotateKeyAsync` | caller must be admin; validates hex; **throws `BUCKET_IS_LOCKED` when `!IsWritable`**; else sets the new key |
| `ForceRemoveAsync` | in order: `DANGLING_MESSAGES`, `DANGLING_ADMINS`, `DANGLING_CONTRIBUTORS`, `DANGLING_VIEWERS`, `DANGLING_TAGS`; then `UNKNOWN_BUCKET` if missing; then delete |

`ResumeWritingAsync` vs `RotateKeyAsync` is the single most important distinction in this task —
they are the same `do_set_key` call differing only in `allow_locked`.

- [ ] **Step 1: Write the failing tests**

Cases: create by manager succeeds and starts locked; create by non-manager throws `NOT_MANAGER`;
create in a missing namespace throws `UNKNOWN_NAMESPACE`; pause by admin succeeds; pause by
non-admin throws `NOT_ADMIN`; resume on a locked bucket succeeds and sets the key; rotate on a
locked bucket throws `BUCKET_IS_LOCKED`; rotate on a writable bucket replaces the key; rotate with
a 16-byte hex key throws `INVALID_INPUT`; a bucket id from another namespace throws
`UNKNOWN_BUCKET`; force-remove ordering — one test per dangling code; force-remove of a clean
bucket deletes it.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `BucketService`**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add bucket service with lock and key rotation rules"`

---

## Task 6: MembershipService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/MembershipService.cs`
- Test: `tests/XcavateBuckets.Tests/MembershipServiceTests.cs`

**Interfaces:**
- Produces: `MembershipService(BucketDbContext db, AuthorizationService auth, InputValidator validator, TimeProvider clock)` with
  `AddAdminAsync`, `RemoveAdminAsync`, `AddContributorAsync`, `RemoveContributorAsync`,
  `AddViewerAsync`, `RemoveViewerAsync` — each
  `(string caller, long namespaceId, long bucketId, string subjectOrViewer, CancellationToken ct)`.
  Add methods return the created entity; remove methods return `Task`.

The asymmetry that must be preserved:

| Method | Caller must be |
|---|---|
| `AddAdminAsync` / `RemoveAdminAsync` | **manager of the namespace** (`do_add_admin` calls `ensure_is_manager`) |
| `AddContributorAsync` / `RemoveContributorAsync` | **admin of the bucket** |
| `AddViewerAsync` / `RemoveViewerAsync` | **admin of the bucket** |

All six require the bucket to exist in the given namespace first. Viewer ids are validated as
32-byte hex. Removes are idempotent — the pallet's `remove` on an absent key is a no-op, so do
not raise "not found".

- [ ] **Step 1: Write the failing tests**

For each of the six: happy path; wrong-role caller throws the right code (`NOT_MANAGER` for admin
methods, `NOT_ADMIN` for contributor/viewer methods); missing bucket throws `UNKNOWN_BUCKET`.
Plus: an *admin* calling `AddAdminAsync` throws `NOT_MANAGER` (guards against collapsing the two
roles); a *manager* who is not a bucket admin calling `AddContributorAsync` throws `NOT_ADMIN`;
`AddViewerAsync` with a non-hex viewer throws `INVALID_INPUT`; removing an absent member succeeds
silently.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `MembershipService`**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add membership service for admins, contributors and viewers"`

---

## Task 7: TagService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/TagService.cs`
- Test: `tests/XcavateBuckets.Tests/TagServiceTests.cs`

**Interfaces:**
- Produces: `TagService(BucketDbContext db, AuthorizationService auth, InputValidator validator, TimeProvider clock)` with
  - `Task<Tag> CreateAsync(string caller, long bucketId, string newTag, CancellationToken ct)`
  - `Task ForceRemoveAsync(long bucketId, string tag, CancellationToken ct)`

Rules from `do_create_tag` / `do_delete_tag`:

- `CreateAsync` takes **only** `bucketId` — no `namespaceId`, matching `create_tag`'s signature —
  validates the tag against `MaxTagLen`, requires the caller to be an admin of the bucket, and
  inserts the tag with `Creator = caller`. It also creates the matching `TagMessageCount` row with
  `Count = 0`, so `write` and `forceRemoveTag` always have a row to read.
- `ForceRemoveAsync` throws `DANGLING_MESSAGES` when the tag's count is non-zero, `UNKNOWN_TAG`
  when the tag is absent, then deletes both the tag and its count row.

- [ ] **Step 1: Write the failing tests**

Cases: create by admin succeeds, sets `Creator`, and creates a zero `TagMessageCount`; create by
non-admin throws `NOT_ADMIN`; create with an over-long tag throws `INVALID_INPUT`; force-remove of
a tag with count 0 deletes tag and count row; force-remove with count 1 throws
`DANGLING_MESSAGES`; force-remove of an absent tag throws `UNKNOWN_TAG`.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `TagService`**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add tag service with message-count guard"`

---

## Task 8: MessageService

**Files:**
- Create: `src/XcavateBuckets.Domain/Services/MessageService.cs`
- Test: `tests/XcavateBuckets.Tests/MessageServiceTests.cs`

**Interfaces:**
- Produces: `MessageService(BucketDbContext db, AuthorizationService auth, InputValidator validator, TimeProvider clock)` with
  - `Task<Message> WriteAsync(string caller, long namespaceId, long bucketId, MessageWriteRequest request, CancellationToken ct)`
  - `Task ForceRemoveAsync(long bucketId, long messageId, CancellationToken ct)`
- Also produces the request record, so the GraphQL layer has a name to bind to:

```csharp
public sealed record MessageWriteRequest(
    string Reference,
    string? Tag,
    string? IpfsContent,
    string Description,
    string ContentType,
    string ContentHash,
    IEnumerable<KeyValuePair<string, string>>? Properties);
```

Rules from `do_create_message` / `do_remove_message`, in this exact order:

1. Load the bucket for `(namespaceId, bucketId)` → `UNKNOWN_BUCKET`.
2. `BUCKET_IS_LOCKED` when `!IsWritable`.
3. Caller must be a contributor → `NOT_CONTRIBUTOR`.
4. When `Tag` is given: the tag must exist in the bucket → `UNKNOWN_TAG`; then increment its
   `TagMessageCount.Count`, raising `ARITHMETIC_OVERFLOW` past `int.MaxValue`.
5. Assign `MessageId = bucket.NextMessageId`, then increment `bucket.NextMessageId`.
6. Insert the message with `Contributor = caller`.

`ForceRemoveAsync` loads the message (→ `UNKNOWN_MESSAGE`), decrements the tag count when the
message had a tag (→ `ARITHMETIC_UNDERFLOW` below zero), and deletes it. Note the counter is
**not** restored to `NextMessageId` — the pallet never rewinds it, so message ids are never reused.

- [ ] **Step 1: Write the failing tests**

Cases: write by contributor to a writable bucket succeeds with `MessageId = 0`; a second write gets
`MessageId = 1`; write to a locked bucket throws `BUCKET_IS_LOCKED`; write by a non-contributor
throws `NOT_CONTRIBUTOR`; write with an unknown tag throws `UNKNOWN_TAG`; write with a known tag
increments the count to 1; write with a 16-byte `ContentHash` throws `INVALID_INPUT`; write with
over-long `IpfsContent` throws `INVALID_INPUT`; force-remove decrements the tag count back to 0;
force-remove of an absent message throws `UNKNOWN_MESSAGE`; after write-then-force-remove, the next
write gets `MessageId = 1` not 0 (ids are never reused); ordering test — a non-contributor writing
to a *locked* bucket gets `BUCKET_IS_LOCKED`, not `NOT_CONTRIBUTOR`.

That last ordering case matters: the pallet checks writability before contributor status.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement `MessageService`**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add message service with tag counters and id sequencing"`

---

## Task 9: GraphQL read layer

**Files:**
- Create: `src/XcavateProfileApi/GraphQL/BigIntType.cs`, `BucketQueries.cs`, `NodeResolvers.cs`
- Modify: `src/XcavateProfileApi/XcavateProfileApi.csproj`, `src/XcavateProfileApi/Program.cs`
- Test: `tests/XcavateBuckets.Tests/../XcavateProfile.ApiTests/GraphQLSchemaTests.cs`

**Interfaces:**
- Consumes: `BucketDbContext` and all entity types.
- Produces: a registered GraphQL schema at `/graphql`; `BigIntType` bound to `long`.

`BigIntType` — use exactly this, it is the verified HC 16 shape:

```csharp
using System.Globalization;
using System.Text.Json;
using HotChocolate.Execution;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;

namespace XcavateProfileApi.GraphQL;

/// <summary>long in C#, string on the wire — matches SubQuery's BigInt.</summary>
public sealed class BigIntType : ScalarType<long, StringValueNode>
{
    public BigIntType() : base("BigInt", BindingBehavior.Explicit) { }

    protected override long OnCoerceInputLiteral(StringValueNode valueLiteral)
        => long.Parse(valueLiteral.Value, CultureInfo.InvariantCulture);

    protected override long OnCoerceInputValue(JsonElement inputValue, IFeatureProvider context)
        => inputValue.ValueKind switch
        {
            JsonValueKind.String => long.Parse(inputValue.GetString()!, CultureInfo.InvariantCulture),
            JsonValueKind.Number => inputValue.GetInt64(),
            _ => throw new GraphQLException("BigInt must be a string or a number.")
        };

    protected override void OnCoerceOutputValue(long runtimeValue, ResultElement resultValue)
        => resultValue.SetStringValue(runtimeValue.ToString(CultureInfo.InvariantCulture), false);

    protected override StringValueNode OnValueToLiteral(long runtimeValue)
        => new(runtimeValue.ToString(CultureInfo.InvariantCulture));
}
```

`BucketQueries` — 9 plural fields, each attributed
`[UsePaging(IncludeTotalCount = true)] [UseProjection] [UseFiltering] [UseSorting]` and returning
`IQueryable<T>` off `BucketDbContext`; plus singular `namespace(id)`, `bucket(id)`, `message(id)`,
`tag(id)`. The singular lookups take `ID!` strings and parse the composite forms:
`Namespace` and `Bucket` ids are a single number; `Message` is `"{bucketId}-{messageId}"`;
`Tag` is `"{bucketId}-{tagName}"` where `tagName` may itself contain `-`, so split on the **first**
hyphen only.

Each entity also needs an `id` resolver producing the indexer's composite string, and `Tag` needs
`messageCount` resolving from `TagMessageCount`. Put these on `[ObjectType<T>]` extensions in
`NodeResolvers.cs`, with DataLoaders for `Bucket.namespace` and the `bucket` back-references.

Registration in `Program.cs`:

```csharp
builder.Services.AddDbContext<BucketDbContext>(options => options.UseNpgsql(connectionString));

builder.Services
    .AddGraphQLServer()
    .AddQueryType<BucketQueries>()
    .AddMutationType<BucketMutations>()      // Task 11
    .AddTypeExtensionsFromAssembly(typeof(Program).Assembly)
    .AddType<BigIntType>()
    .BindRuntimeType<long, BigIntType>()
    .AddFiltering()
    .AddSorting()
    .AddProjections()
    .AddErrorFilter<BucketErrorFilter>();    // Task 11
```

and `app.MapGraphQL();` next to `app.MapControllers();`. Extend the existing migration retry loop
at `Program.cs:96-129` to migrate `BucketDbContext` too.

- [ ] **Step 1: Write the failing schema test**

A snapshot-style assertion that the printed schema contains the expected shapes. This is the guard
against accidental schema drift.

```csharp
[Test]
public async Task Schema_exposes_bucket_types_with_expected_shape()
{
    var schema = await new ServiceCollection()
        .AddDbContext<BucketDbContext>(o => o.UseInMemoryDatabase("schema-test"))
        .AddGraphQLServer()
        /* ...same registration as Program.cs... */
        .BuildSchemaAsync();

    var sdl = schema.ToString();

    Assert.Multiple(() =>
    {
        Assert.That(sdl, Does.Contain("type Bucket"));
        Assert.That(sdl, Does.Contain("namespaceId: BigInt!"));
        Assert.That(sdl, Does.Contain("isWritable: Boolean!"));
        Assert.That(sdl, Does.Contain("totalCount: Int!"));
        Assert.That(sdl, Does.Contain("nodes: [Bucket!]"));
        // block fields are gone
        Assert.That(sdl, Does.Not.Contain("createdBlock"));
        Assert.That(sdl, Does.Not.Contain("addedBlock"));
        Assert.That(sdl, Does.Not.Contain("updatedBlock"));
    });
}
```

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Add packages, implement the scalar, queries and resolvers, wire up `Program.cs`**

```bash
dotnet add src/XcavateProfileApi package HotChocolate.AspNetCore --version 16.5.1
dotnet add src/XcavateProfileApi package HotChocolate.Data.EntityFramework --version 16.5.1
dotnet add src/XcavateProfileApi reference src/XcavateBuckets.Domain
```

- [ ] **Step 4: Run to verify it passes, and confirm the app still builds**

Run: `dotnet build && dotnet test tests/XcavateProfile.ApiTests --filter GraphQLSchemaTests`

- [ ] **Step 5: Commit** — `git commit -am "feat: expose bucket read model over GraphQL"`

---

## Task 10: Signature middleware and field authorization

**Files:**
- Create: `src/XcavateProfileApi/GraphQL/Auth/ICallerContext.cs`, `CallerContext.cs`, `GraphQLSignatureMiddleware.cs`, `RequireSignatureAttribute.cs`, `RequireAdminAttribute.cs`
- Modify: `src/XcavateProfileApi/Program.cs`
- Test: `tests/XcavateProfile.ApiTests/GraphQLAuthTests.cs`

**Interfaces:**
- Consumes: `ISignatureValidator`, `CryptoHelper`, `EmptyPayloadBody`.
- Produces:
  - `ICallerContext` with `string? Address { get; }`, `bool IsAdmin { get; }`, `bool IsAuthenticated { get; }`, and `string RequireAddress()` throwing `BucketException(Unauthorized)`.
  - `RequireSignatureAttribute` / `RequireAdminAttribute`, both `ObjectFieldDescriptorAttribute`s installing a field middleware.

Middleware behaviour on `POST /graphql`:

1. If any of `X-SS58-Address`, `X-Signature`, `X-Timestamp` is missing → leave the context anonymous and continue. (Queries must still work.)
2. Otherwise buffer and read the body, compute `CryptoHelper.Hash(bodyText)` hex, and verify
   `POST:/graphql:<bodyHash>:<timestamp>` through `ISignatureValidator.ValidateAsync`. Reuse the
   existing `IPayloadBody` seam by passing a small `RawBody : IPayloadBody` whose `Hash()` returns
   the precomputed hex, so the payload string is built by `CryptoHelper.ConstructPayload` exactly
   as the REST path does.
3. On success populate `CallerContext`; on failure leave it anonymous **and record the reason**, so
   `RequireSignature` can report `INVALID_SIGNATURE` or `TIMESTAMP_OUT_OF_RANGE` rather than a bare
   `UNAUTHORIZED`.
4. Rewind the body stream (`Request.Body.Position = 0`) so Hot Chocolate can read it.

Step 4 is essential — without the rewind every mutation request fails to parse.

- [ ] **Step 1: Write the failing tests**

Against a `WebApplicationFactory` host with a test database: an unsigned query succeeds; an
unsigned mutation fails with `UNAUTHORIZED`; a mutation signed with a valid keypair succeeds; a
mutation with a tampered body fails with `INVALID_SIGNATURE`; a mutation with a 10-minute-old
timestamp fails with `TIMESTAMP_OUT_OF_RANGE`; a `force*` mutation from a non-admin address fails
with `FORBIDDEN`; the same from an address in `ADMIN_ADDRESSES` succeeds.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement the middleware, caller context and the two attributes**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: gate GraphQL mutations behind sr25519 signatures"`

---

## Task 11: GraphQL mutations and error filter

**Files:**
- Create: `src/XcavateProfileApi/GraphQL/BucketMutations.cs`, `Inputs.cs`, `BucketErrorFilter.cs`
- Test: `tests/XcavateProfile.ApiTests/GraphQLMutationTests.cs`

**Interfaces:**
- Consumes: all five domain services, `ICallerContext`, `BucketException`.
- Produces: 20 mutation fields exactly as named in the spec.

Each mutation is a thin wrapper: resolve caller from `ICallerContext.RequireAddress()`, open a
transaction on `BucketDbContext`, call the domain service, `SaveChangesAsync`, commit, return.
Factor the transaction wrapper into one private helper rather than repeating it 20 times.

`BucketErrorFilter` maps `BucketException` to an `IError` with
`.SetCode(ex.Code.ToErrorCode())` and the exception message, and leaves other exceptions untouched.

The 15 role-based mutations carry `[RequireSignature]`; the 5 `force*` mutations carry
`[RequireAdmin]`.

- [ ] **Step 1: Write the failing tests**

One end-to-end happy-path test per mutation, driven through the executor with a signed request,
asserting both the returned payload and the resulting database state. Plus one error-mapping test
per distinct error code, asserting `errors[0].extensions.code`.

Sequence for the happy-path suite, since the mutations are order-dependent:
`createNamespace` → `createBucket` → `addAdmin` → `addContributor` → `createTag` →
`resumeWriting` → `write` → `addViewer` → `rotateKey` → `pauseWriting` → `addManager` →
`removeManager` → removals → `force*` teardown in dangling-safe order.

- [ ] **Step 2: Run to verify it fails**
- [ ] **Step 3: Implement the inputs, mutations and error filter**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Commit** — `git commit -am "feat: add bucket GraphQL mutations with pallet error mapping"`

---

## Task 12: StrawberryShake client

**Files:**
- Create: `src/XcavateProfileApiClient/GraphQL/.graphqlrc.json`, `schema.graphql`, `Operations.graphql`, `SigningHttpMessageHandler.cs`
- Modify: `src/XcavateProfileApiClient/XcavateProfileApiClient.csproj`
- Test: `tests/XcavateProfile.ApiTests/BucketClientTests.cs`

**Interfaces:**
- Produces: generated `IXcavateBucketsClient`, plus
  `SigningHttpMessageHandler(string ss58Address, byte[] secretKey)` which hashes the outgoing body
  and sets the three X-\* headers.

The handler is the client-side mirror of Task 10's middleware: read the serialized body, compute
`CryptoHelper.Hash`, build the payload via `CryptoHelper.ConstructPayload("POST", "/graphql", …)`,
sign with `CryptoHelper.SignAsync`, attach headers.

- [ ] **Step 1: Export the server schema for codegen**

```bash
dotnet run --project src/XcavateProfileApi -- schema export --output src/XcavateProfileApiClient/GraphQL/schema.graphql
```

If that command is unavailable, fetch it from a running server with
`dotnet graphql download http://localhost:5000/graphql -f src/XcavateProfileApiClient/GraphQL/schema.graphql`.

- [ ] **Step 2: Write the failing test** — a round-trip creating a namespace through the generated client.
- [ ] **Step 3: Run to verify it fails**
- [ ] **Step 4: Add StrawberryShake, define operations, implement the handler**

```bash
dotnet add src/XcavateProfileApiClient package StrawberryShake.Server --version 16.5.1
```

- [ ] **Step 5: Run to verify it passes**
- [ ] **Step 6: Commit** — `git commit -am "feat: add StrawberryShake bucket client with request signing"`

---

## Task 13: End-to-end tests against the docker stack

**Files:**
- Create: `tests/XcavateProfile.ApiTests/BucketE2ETests.cs`
- Modify: `run_e2e_tests.sh`

**Interfaces:**
- Consumes: the generated client from Task 12, `TestMnemonics` from the existing test project.

Mirrors the existing `ProfileApiTests` style: point at the docker-compose stack, use
`TestMnemonics.AdminMnemonic` for `force*` coverage, and drive the full lifecycle —
create namespace, create bucket, grant roles, create tag, unlock, write a message, read it back
through a nested GraphQL query, then tear down through the `force*` mutations.

Include one test asserting a nested read returns related rows in a single request
(`buckets { nodes { messages { id } admins { subjectId } } }`), which is the DataLoader regression
guard.

- [ ] **Step 1: Write the failing E2E test**
- [ ] **Step 2: Run against the stack to verify it fails**

Run: `docker compose up -d && dotnet test tests/XcavateProfile.ApiTests --filter BucketE2ETests`

- [ ] **Step 3: Fix whatever integration gaps it surfaces**
- [ ] **Step 4: Run the whole suite**

Run: `dotnet test`
Expected: all green, including the pre-existing profile tests.

- [ ] **Step 5: Commit** — `git commit -am "test: add bucket GraphQL end-to-end coverage"`

---

## Self-Review

**Spec coverage:** All 9 entities → Task 1. Options/validation → Task 2. All 20 mutations and every
row of the spec's authorization table → Tasks 3–8 (rules) and 11 (exposure). Query surface with
`nodes`/`totalCount`/`pageInfo`/`where`/`order` → Task 9. BigInt unification → Task 9. Auth →
Task 10. Error codes → Tasks 2 and 11. `ipfsContent` stored at write time → Task 8's
`MessageWriteRequest.IpfsContent`. Three test layers → Tasks 3–8, 9–11, 13. StrawberryShake
client → Task 12.

**Type consistency:** `MessageWriteRequest` is defined in Task 8 and consumed in Task 11.
`BucketErrorCode.ToErrorCode()` is defined in Task 2 and consumed in Tasks 2 and 11.
`ICallerContext.RequireAddress()` is defined in Task 10 and consumed in Task 11. `TestDb` is
defined in Task 3 and reused in Tasks 4–8. `BigIntType` is defined in Task 9 and referenced in the
Task 9 registration only. Service constructor signatures are identical across Tasks 4–8
(`db, auth, validator, clock`).

**Known ordering dependency:** Task 1 Step 6 (migration) needs Task 9's `Program.cs` registration.
The step documents the design-time-factory workaround, so Task 1 can still complete standalone.
