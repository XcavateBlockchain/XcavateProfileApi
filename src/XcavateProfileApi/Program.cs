using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using XcavateBuckets.Domain;
using XcavateBuckets.Domain.Data;
using XcavateBuckets.Domain.Services;
using XcavateProfileApi.Data;
using XcavateProfileApi.GraphQL;
using XcavateProfileApi.GraphQL.Auth;
using XcavateProfileApi.Middleware;
using XcavateProfileApi.Services;
using XcavateProfileApi.Swagger;

var builder = WebApplication.CreateBuilder(args);

// Load environment variables from .env file
Env.Load(Path.Combine(builder.Environment.ContentRootPath, ".env"));

// Add services to the container
// Keep the Async suffix in action names so CreatedAtAction(nameof(GetProfileAsync), ...) resolves
builder.Services.AddControllers(options => options.SuppressAsyncSuffixInActionNames = false);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
});

// Configure Swagger
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "XcavateProfile API",
        Version = "v1",
        Description = "A Substrate/Polkadot profile registration and management API"
    });

    // Add Swagger filters
    c.OperationFilter<ExcludeSwaggerOperationFilter>();
});

// Configure PostgreSQL database
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<ProfileDbContext>(options =>
    options.UseNpgsql(connectionString));

// Bucket pallet port: same database, separate migrations history so the two contexts stay independent
builder.Services.AddDbContext<BucketDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable(BucketDbContext.MigrationsHistoryTable)));

builder.Services.AddSingleton(new BucketOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InputValidator>();
builder.Services.AddScoped<AuthorizationService>();
builder.Services.AddScoped<NamespaceService>();
builder.Services.AddScoped<BucketService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<MessageService>();

builder.Services.AddScoped<CallerContext>();
builder.Services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());

builder.Services
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
    .BindRuntimeType<long, BigIntType>()
    .AddFiltering()
    .AddSorting()
    .AddErrorFilter<BucketErrorFilter>();

// Configure AWS S3 client for Hetzner Object Storage
builder.Services.AddSingleton<IS3Service, S3Service>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new S3Service(new S3Config
    {
        Endpoint = config["S3_ENDPOINT"] ?? string.Empty,
        Region = config["S3_REGION"] ?? string.Empty,
        AccessKey = config["S3_ACCESS_KEY"] ?? string.Empty,
        SecretKey = config["S3_SECRET_KEY"] ?? string.Empty
    });
});

// Configure admin addresses from environment
builder.Services.AddSingleton<List<string>>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var adminAddresses = config["ADMIN_ADDRESSES"];
    return string.IsNullOrWhiteSpace(adminAddresses)
        ? new List<string>()
        : adminAddresses.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
});

// Register authentication services
builder.Services.Configure<SignatureValidationOptions>(
    builder.Configuration.GetSection("SignatureValidation"));
builder.Services.AddScoped(sp => sp.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<SignatureValidationOptions>>().Value);
builder.Services.AddScoped<ISignatureValidator, SignatureValidator>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "XcavateProfile API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseCors("AllowAll");

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

app.UseAuthorization();

// Must run before MapGraphQL so the caller is resolved by the time resolvers execute.
app.UseMiddleware<GraphQLSignatureMiddleware>();

// Apply migrations on startup with retry
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ProfileDbContext>();
    var bucketContext = scope.ServiceProvider.GetRequiredService<BucketDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var maxRetries = 5;
    var delay = TimeSpan.FromSeconds(2);

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            logger.LogInformation("Applying database migrations (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);
            context.Database.Migrate();
            bucketContext.Database.Migrate();
            logger.LogInformation("Database migrations completed successfully");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database migration attempt {Attempt}/{MaxRetries} failed: {Message}", attempt, maxRetries, ex.Message);

            if (attempt == maxRetries)
            {
                logger.LogError(ex, "Database migration failed after {MaxRetries} attempts", maxRetries);
                throw;
            }

            logger.LogInformation("Waiting {Delay} before retry...", delay);
            Thread.Sleep(delay);
            delay = TimeSpan.FromSeconds(delay.Seconds * 2); // Exponential backoff
        }
    }
}

app.MapControllers();
app.MapGraphQL();

// Add health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithName("HealthCheck");

app.Run();
