using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Authentication;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.BackgroundJobs;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Server.Endpoints;
using CharacterConceptAI.Server.Storage;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

var builder = WebApplication.CreateBuilder(args);

// Enable running as Windows Service when launched via SCM
builder.Host.UseWindowsService();

// Ensure configuration is resolved from application binary directory (critical for Windows Service & daemons)
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// Database configuration
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (builder.Environment.IsEnvironment("Testing"))
    {
        options.UseInMemoryDatabase(builder.Configuration["Testing:DatabaseName"] ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
    }
    else
    {
        options.UseSqlServer(
            connectionString ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection for SQL Server. InMemory is permitted only in Testing."),
            sql => sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null));
    }
});

// Identity & Lockout settings
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 12;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.User.RequireUniqueEmail = true;

    // Lockout protection against brute-force attacks
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<AppDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

// DataProtection key persistence (prevents 401 token invalidation across server restarts)
var keysDir = builder.Configuration["DataProtection:KeyPath"];
if (string.IsNullOrWhiteSpace(keysDir)) keysDir = Path.Combine(AppContext.BaseDirectory, "DataProtectionKeys");
keysDir = Path.GetFullPath(keysDir);
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

// Built-in Identity Bearer Token Authentication
var localNoAuth = builder.Configuration.GetValue<bool>("Auth:LocalNoAuth");
builder.Services.AddAuthentication(IdentityConstants.BearerScheme)
    .AddBearerToken(IdentityConstants.BearerScheme, options =>
    {
        options.BearerTokenExpiration = localNoAuth ? TimeSpan.FromDays(30) : TimeSpan.FromMinutes(15);
        options.RefreshTokenExpiration = TimeSpan.FromDays(30);
    });

builder.Services.AddAuthorization();

// Compress JSON API payloads over LAN (images stay uncompressed to avoid wasted CPU)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json", "application/problem+json", "text/plain"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// Domain Services
builder.Services.AddSingleton<AuthValidationCache>();
builder.Services.AddSingleton<GenerationJobSignal>();
builder.Services.AddSingleton<ILoraCatalogService, LoraCatalogService>();
builder.Services.AddScoped<IProjectAuthorizationService, ProjectAuthorizationService>();
builder.Services.AddSingleton<IMediaStore, LocalMediaStore>();
builder.Services.AddHttpClient<ComfyUiClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["ComfyUI:BaseUrl"] ?? "http://127.0.0.1:8188";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<FakeImageGenerationClient>();
// Transient matches AddHttpClient<ComfyUiClient> (eliminates captive transient HttpClient anti-pattern)
builder.Services.AddTransient<IImageGenerationClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    bool isTest = env.IsEnvironment("Testing");

    var provider = config["ComfyUI:Provider"] ?? "Real";
    if (isTest ||
        provider.Equals("Fake", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Simulated", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<FakeImageGenerationClient>();
    }
    return sp.GetRequiredService<ComfyUiClient>();
});
builder.Services.AddSingleton<IPromptBuilderService, PromptBuilderService>();
builder.Services.AddSingleton<IDepthPoseGalleryService, DepthPoseGalleryService>();
builder.Services.AddSingleton<IOutfitGalleryService, OutfitGalleryService>();
builder.Services.AddHttpClient<IPromptEnhancerService, PromptEnhancerService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(110);
});
builder.Services.AddHostedService<GenerationBackgroundWorker>();

var app = builder.Build();

// Pre-warm pose & outfit catalogs asynchronously in the background right after startup (0ms first-open delay)
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(() =>
    {
        try
        {
            app.Services.GetRequiredService<IDepthPoseGalleryService>().Prewarm();
            app.Services.GetRequiredService<IOutfitGalleryService>().Prewarm();
        }
        catch { }
    });
});

// Database migration / ensure created and Admin bootstrap
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational() && app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
    {
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Automatic migration is restricted to an explicitly enabled development database. Apply reviewed deployment SQL separately.");
        await db.Database.MigrateAsync();
    }
    else if (!db.Database.IsRelational())
    {
        db.Database.EnsureCreated();
    }

    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await SecureAdminBootstrap.InitializeAsync(app.Services, app.Configuration, logger);
            break;
        }
        catch (Exception ex) when (attempt < 10 && db.Database.IsRelational() && (ex is SqlException or TimeoutException))
        {
            logger.LogWarning(ex, "SQL Server chưa sẵn sàng khi khởi động (lần {Attempt}/10). Thử lại sau 2 giây...", attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:SeedDemoData"))
    {
        await DemoProjectSeed.EnsureDemoDataAsync(app.Services, logger);
    }

    // Idempotently ensure high-impact SQL Server composite indexes (zero EF migration drift)
    if (db.Database.IsSqlServer())
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_GenerationJobs_Project_Status_RequestedAt' AND object_id = OBJECT_ID('dbo.GenerationJobs'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_GenerationJobs_Project_Status_RequestedAt] ON [dbo].[GenerationJobs] ([ProjectId], [Status], [RequestedAt] DESC);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_GenerationJobs_User_ActiveStatus' AND object_id = OBJECT_ID('dbo.GenerationJobs'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_GenerationJobs_User_ActiveStatus] ON [dbo].[GenerationJobs] ([RequestedByUserId], [Status]) WHERE [Status] IN (0, 1);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_CharacterConcepts_Project_Status_UpdatedAt' AND object_id = OBJECT_ID('dbo.CharacterConcepts'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_CharacterConcepts_Project_Status_UpdatedAt] ON [dbo].[CharacterConcepts] ([ProjectId], [Status], [UpdatedAt] DESC);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_ConceptVersions_Concept_Status' AND object_id = OBJECT_ID('dbo.ConceptVersions'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_ConceptVersions_Concept_Status] ON [dbo].[ConceptVersions] ([ConceptId], [Status]);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_Characters_Project_Status_Name' AND object_id = OBJECT_ID('dbo.Characters'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_Characters_Project_Status_Name] ON [dbo].[Characters] ([ProjectId], [Status], [Name]);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Perf_AspNetUsers_IsActive_CreatedAt' AND object_id = OBJECT_ID('dbo.AspNetUsers'))
                    CREATE NONCLUSTERED INDEX [IX_Perf_AspNetUsers_IsActive_CreatedAt] ON [dbo].[AspNetUsers] ([IsActive], [CreatedAt]);
                """);
        }
        catch (Exception idxEx)
        {
            logger.LogWarning(idxEx, "Không thể tạo các chỉ mục SQL Server tối ưu (bỏ qua an toàn).");
        }
    }
}

app.UseResponseCompression();
app.UseAuthentication();
// Proprietary bearer tickets are self-contained. Recheck the current account and
// security stamp via short-TTL AuthValidationCache (invalidated immediately on admin mutations),
// eliminating 5-6 redundant Identity SQL round-trips per media/API request.
var securityStampClaimType = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
var authCache = app.Services.GetRequiredService<AuthValidationCache>();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = context.User.FindFirstValue(securityStampClaimType);
        if (!string.IsNullOrEmpty(uid) && authCache.TryGetValidPrincipal(uid, stamp, out var cachedPrincipal))
        {
            context.User = cachedPrincipal;
        }
        else
        {
            var signIn = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
            var user = await signIn.ValidateSecurityStampAsync(context.User);
            if (user is null || !user.IsActive)
            {
                if (!string.IsNullOrEmpty(uid)) authCache.InvalidateUser(uid);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var refreshedPrincipal = await signIn.CreateUserPrincipalAsync(user);
            if (!string.IsNullOrEmpty(user.SecurityStamp))
                authCache.Set(user.Id, user.SecurityStamp, refreshedPrincipal);
            context.User = refreshedPrincipal;
        }
    }
    await next(context);
});
app.UseAuthorization();

var api = app.MapGroup("/api/v1");

// Health
api.MapGet("/health", (IConfiguration config) => Results.Ok(new
{
    status = "Healthy",
    desktopContractVersion = "uiux-v1",
    time = DateTimeOffset.UtcNow,
    provider = config["ComfyUI:Provider"] ?? "Real"
}));

// Auth & Admin Route Groups
api.MapGroup("/auth").MapAuthEndpoints();
api.MapGroup("/admin").MapAdminEndpoints();

api.MapGet("/generation-capabilities", (IImageGenerationClient provider, IConfiguration config) =>
{
    var enabled = provider.IsSimulated || config.GetValue<bool>("ComfyUI:Img2ImgQualified");
    return Results.Ok(new GenerationCapabilitiesDto(
        provider.IsSimulated ? "SIMULATED" : "ComfyUI",
        provider.IsSimulated, enabled,
        provider.IsSimulated ? "SIMULATED" : enabled ? "QUALIFIED_BY_SERVER_CONFIGURATION" : "REAL_AI_UNVERIFIED",
        !provider.IsSimulated && config.GetValue<bool>("ComfyUI:UpscaleQualified"),
        provider.IsSimulated || config.GetValue<bool>("ComfyUI:FullQualityQualified")));
}).RequireAuthorization();

// Projects, Scripts & Characters (Authorized)
var projectGroup = api.MapGroup("/projects").RequireAuthorization();
projectGroup.MapProjectsEndpoints();
projectGroup.MapScriptsEndpoints();
projectGroup.MapCharactersEndpoints();
projectGroup.MapGenerationsEndpoints();
projectGroup.MapConceptsEndpoints();
projectGroup.MapUpscaleEndpoints();
api.MapStudioEnhancementEndpoints(projectGroup);

// Media streaming with Project Guard (uses OS async SendFile via Results.File(fullPath, ...))
projectGroup.MapGet("/{projectId:guid}/media/{assetId:guid}", async (
    Guid projectId,
    Guid assetId,
    ClaimsPrincipal principal,
    IProjectAuthorizationService auth,
    AppDbContext db,
    IMediaStore mediaStore,
    HttpContext context,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null) return Results.Unauthorized();

    var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true, ct: ct);
    if (!authResult.IsAuthorized)
    {
        return authResult.Status == ProjectAccessStatus.NotFound
            ? Results.NotFound()
            : Results.Problem(authResult.ErrorMessage, statusCode: 403);
    }

    var asset = await db.ImageAssets
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.Id == assetId && a.ProjectId == projectId, ct);
    if (asset is null) return Results.NotFound();

    var etag = !string.IsNullOrWhiteSpace(asset.Sha256)
        ? $"\"{asset.Sha256}\""
        : $"\"{asset.Id:N}-{asset.SizeBytes}\"";

    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ETag = etag;

    if (context.Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), etag, StringComparison.Ordinal)))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    string fullPath;
    try
    {
        fullPath = mediaStore.GetFullPath(asset.StorageKey);
    }
    catch (ArgumentException)
    {
        return Results.NotFound();
    }
    if (!File.Exists(fullPath)) return Results.NotFound();

    return Results.File(fullPath, asset.ContentType, enableRangeProcessing: true);
});

app.Run();

public partial class Program { }
