using Dapper;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MmProtect.LicenseServer.Data;
using MmProtect.LicenseServer.Models;
using MmProtect.LicenseServer.Security;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

// Register Dapper DateTime handler so SQLite TEXT dates map to DateTime correctly.
SqlMapper.AddTypeHandler(new DapperDateTimeHandler());

var builder = WebApplication.CreateBuilder(args);

var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider", "mysql")!
                        .ToLowerInvariant();

if (dbProvider == "sqlite")
    builder.Services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
else
    builder.Services.AddSingleton<IDbConnectionFactory, MySqlConnectionFactory>();

builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddEndpointsApiExplorer();

// ── Reverse-Proxy / ForwardedHeaders ──────────────────────────────────────
// Reads X-Forwarded-For and X-Forwarded-Proto only from explicitly trusted
// proxy IPs/networks. Disabled by default — set ReverseProxy:Enabled = true
// when the server sits behind nginx/Apache/HAProxy.
var proxySection = builder.Configuration.GetSection("ReverseProxy");
bool proxyEnabled = proxySection.GetValue<bool>("Enabled", false);

if (proxyEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only explicitly configured sources — reject all others by default.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        // Single proxy IPs (e.g. "127.0.0.1", "::1", "10.0.0.1")
        var trustedProxies = proxySection.GetSection("TrustedProxies")
                                         .Get<string[]>() ?? [];
        foreach (var raw in trustedProxies)
        {
            if (IPAddress.TryParse(raw.Trim(), out var ip))
                options.KnownProxies.Add(ip);
        }

        // CIDR networks (e.g. "10.0.0.0/8", "172.16.0.0/12")
        var trustedNetworks = proxySection.GetSection("TrustedNetworks")
                                          .Get<string[]>() ?? [];
        foreach (var cidr in trustedNetworks)
        {
            var slash = cidr.IndexOf('/');
            if (slash > 0
                && IPAddress.TryParse(cidr[..slash].Trim(), out var prefix)
                && int.TryParse(cidr[(slash + 1)..].Trim(), out var length))
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
            }
        }

        // How many hops of X-Forwarded-For to trust (1 = only the last proxy)
        options.ForwardLimit = proxySection.GetValue<int>("ForwardLimit", 1);
    });
}

// ── Rate Limiting (brute-force protection on /runtime/lease) ─────────────
// Fixed-window limiter per client IP. Toggled via RateLimiting:Enabled.
// Applies only to the runtime lease endpoint (encoder API uses API-key auth).
const string RateLimitPolicyLease = "lease";

builder.Services.AddRateLimiter(options =>
{
    var rlSection = builder.Configuration.GetSection("RateLimiting");
    bool rlEnabled = rlSection.GetValue<bool>("Enabled", true);

    if (!rlEnabled)
    {
        // Disabled — unconditionally permit all requests
        options.AddPolicy(RateLimitPolicyLease, _ =>
            RateLimitPartition.GetNoLimiter("*"));
        return;
    }

    int permitLimit   = rlSection.GetValue("LeaseEndpoint:PermitLimit", 10);
    int windowSeconds = rlSection.GetValue("LeaseEndpoint:WindowSeconds", 60);
    int queueLimit    = rlSection.GetValue("LeaseEndpoint:QueueLimit", 0);

    options.AddPolicy<string>(RateLimitPolicyLease, context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit            = permitLimit,
                Window                 = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit             = queueLimit,
                QueueProcessingOrder   = QueueProcessingOrder.OldestFirst,
                AutoReplenishment      = true
            });
    });

    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.Headers["Retry-After"] =
            windowSeconds.ToString();
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            ErrorDto.Create("RATE_LIMITED", "Too many lease requests. Try again later."),
            cancellationToken: ct);
    };
});

var app = builder.Build();

// ForwardedHeaders MUST run before any middleware that reads the IP.
if (proxyEnabled)
    app.UseForwardedHeaders();

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    timeUtc = DateTimeOffset.UtcNow
}));

var encoder = app.MapGroup("/api/v1/encoder");
encoder.AddEndpointFilter<ApiKeyEndpointFilter>();

encoder.MapPost("/customers/upsert", async (CustomerUpsertRequest request, IDbConnectionFactory db) =>
{
    var uid = "cust_" + Ids.NewId();
    await using var conn = await db.OpenAsync();

    var upsertSql = db.IsSqlite
        ? """
          INSERT INTO customers (customer_uid, external_customer_ref, name, email, notes)
          VALUES (@Uid, @ExternalCustomerRef, @Name, @Email, @Notes)
          ON CONFLICT(external_customer_ref) DO UPDATE SET
              name = excluded.name, email = excluded.email, notes = excluded.notes;
          """
        : """
          INSERT INTO customers (customer_uid, external_customer_ref, name, email, notes)
          VALUES (@Uid, @ExternalCustomerRef, @Name, @Email, @Notes)
          ON DUPLICATE KEY UPDATE name = VALUES(name), email = VALUES(email), notes = VALUES(notes), updated_at = CURRENT_TIMESTAMP;
          """;

    await conn.ExecuteAsync(upsertSql,
        new { Uid = uid, request.ExternalCustomerRef, request.Name, request.Email, request.Notes });

    var customerUid = await conn.ExecuteScalarAsync<string>(
        "SELECT customer_uid FROM customers WHERE external_customer_ref = @ExternalCustomerRef",
        new { request.ExternalCustomerRef });

    return Results.Ok(new CustomerUpsertResponse(customerUid, customerUid == uid));
});

encoder.MapPost("/projects/upsert", async (ProjectUpsertRequest request, IDbConnectionFactory db) =>
{
    var uid = "proj_" + Ids.NewId();
    await using var conn = await db.OpenAsync();

    var upsertSql = db.IsSqlite
        ? """
          INSERT INTO projects (project_uid, project_key, name, php_min_version, description)
          VALUES (@Uid, @ProjectKey, @Name, @PhpMinVersion, @Description)
          ON CONFLICT(project_key) DO UPDATE SET
              name = excluded.name, php_min_version = excluded.php_min_version, description = excluded.description;
          """
        : """
          INSERT INTO projects (project_uid, project_key, name, php_min_version, description)
          VALUES (@Uid, @ProjectKey, @Name, @PhpMinVersion, @Description)
          ON DUPLICATE KEY UPDATE name = VALUES(name), php_min_version = VALUES(php_min_version), description = VALUES(description), updated_at = CURRENT_TIMESTAMP;
          """;

    await conn.ExecuteAsync(upsertSql,
        new { Uid = uid, request.ProjectKey, request.Name, request.PhpMinVersion, request.Description });

    var projectUid = await conn.ExecuteScalarAsync<string>(
        "SELECT project_uid FROM projects WHERE project_key = @ProjectKey",
        new { request.ProjectKey });

    return Results.Ok(new ProjectUpsertResponse(projectUid, projectUid == uid));
});

encoder.MapPost("/licenses/upsert", async (LicenseUpsertRequest request, IDbConnectionFactory db) =>
{
    var uid = "lic_" + Ids.NewId();
    await using var conn = await db.OpenAsync();

    var customerDbId = await DbLookup.CustomerIdAsync(conn, request.CustomerId);
    var projectDbId = await DbLookup.ProjectIdAsync(conn, request.ProjectId);

    var upsertSql = db.IsSqlite
        ? """
          INSERT INTO licenses
              (license_uid, customer_id, project_id, license_key, valid_from, valid_until, max_activations, features, status)
          VALUES
              (@Uid, @CustomerDbId, @ProjectDbId, @LicenseKey, @ValidFrom, @ValidUntil, @MaxActivations, @FeaturesJson, 'active')
          ON CONFLICT(license_key) DO UPDATE SET
              valid_from = excluded.valid_from, valid_until = excluded.valid_until,
              max_activations = excluded.max_activations, features = excluded.features;
          """
        : """
          INSERT INTO licenses
              (license_uid, customer_id, project_id, license_key, valid_from, valid_until, max_activations, features, status)
          VALUES
              (@Uid, @CustomerDbId, @ProjectDbId, @LicenseKey, @ValidFrom, @ValidUntil, @MaxActivations, @FeaturesJson, 'active')
          ON DUPLICATE KEY UPDATE
              valid_from = VALUES(valid_from), valid_until = VALUES(valid_until),
              max_activations = VALUES(max_activations), features = VALUES(features), updated_at = CURRENT_TIMESTAMP;
          """;

    await conn.ExecuteAsync(upsertSql, new
    {
        Uid = uid,
        CustomerDbId = customerDbId,
        ProjectDbId = projectDbId,
        request.LicenseKey,
        ValidFrom = request.ValidFrom.UtcDateTime,
        ValidUntil = request.ValidUntil?.UtcDateTime,
        request.MaxActivations,
        FeaturesJson = JsonCanonical.Serialize(request.Features ?? [])
    });

    var licenseUid = await conn.ExecuteScalarAsync<string>(
        "SELECT license_uid FROM licenses WHERE license_key = @LicenseKey",
        new { request.LicenseKey });

    return Results.Ok(new LicenseUpsertResponse(licenseUid, licenseUid == uid));
});

encoder.MapPost("/builds/start", async (BuildStartRequest request, IDbConnectionFactory db, CryptoService crypto) =>
{
    var buildUid = "build_" + Ids.NewId();
    var keyUid = "key_" + Ids.NewId();
    var buildKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    await using var conn = await db.OpenAsync();

    var customerDbId = await DbLookup.CustomerIdAsync(conn, request.CustomerId);
    var projectDbId = await DbLookup.ProjectIdAsync(conn, request.ProjectId);
    var licenseDbId = await DbLookup.LicenseIdAsync(conn, request.LicenseId);

    await conn.ExecuteAsync("""
        INSERT INTO crypto_keys (key_uid, key_type, algorithm, encrypted_secret_key)
        VALUES (@KeyUid, 'build', 'AES-256-GCM', @EncryptedSecretKey);
        """, new { KeyUid = keyUid, EncryptedSecretKey = crypto.ProtectForDemoOnly(buildKey) });

    var keyDbId = await conn.ExecuteScalarAsync<long>(
        "SELECT id FROM crypto_keys WHERE key_uid = @KeyUid", new { KeyUid = keyUid });

    await conn.ExecuteAsync("""
        INSERT INTO builds
            (build_uid, customer_id, project_id, license_id, key_id, version, source_revision, encoder_version)
        VALUES
            (@BuildUid, @CustomerDbId, @ProjectDbId, @LicenseDbId, @KeyDbId, @Version, @SourceRevision, @EncoderVersion);
        """, new
    {
        BuildUid = buildUid,
        CustomerDbId = customerDbId,
        ProjectDbId = projectDbId,
        LicenseDbId = licenseDbId,
        KeyDbId = keyDbId,
        request.Version,
        request.SourceRevision,
        request.EncoderVersion
    });

    return Results.Ok(new BuildStartResponse(buildUid, keyUid, buildKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
});

encoder.MapPost("/builds/{buildId}/files", async (string buildId, BuildFilesRequest request, IDbConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    var buildDbId = await DbLookup.BuildIdAsync(conn, buildId);

    var upsertSql = db.IsSqlite
        ? """
          INSERT INTO build_files
              (build_id, file_uid, relative_path, path_hash, plain_hash, cipher_hash, algorithm, kdf)
          VALUES
              (@BuildDbId, @FileId, @RelativePath, @PathHash, @PlainHash, @CipherHash, @Algorithm, @Kdf)
          ON CONFLICT(build_id, file_uid) DO UPDATE SET
              relative_path = excluded.relative_path, path_hash = excluded.path_hash,
              plain_hash = excluded.plain_hash, cipher_hash = excluded.cipher_hash,
              algorithm = excluded.algorithm, kdf = excluded.kdf;
          """
        : """
          INSERT INTO build_files
              (build_id, file_uid, relative_path, path_hash, plain_hash, cipher_hash, algorithm, kdf)
          VALUES
              (@BuildDbId, @FileId, @RelativePath, @PathHash, @PlainHash, @CipherHash, @Algorithm, @Kdf)
          ON DUPLICATE KEY UPDATE
              relative_path = VALUES(relative_path), path_hash = VALUES(path_hash),
              plain_hash = VALUES(plain_hash), cipher_hash = VALUES(cipher_hash),
              algorithm = VALUES(algorithm), kdf = VALUES(kdf);
          """;

    foreach (var file in request.Files)
    {
        await conn.ExecuteAsync(upsertSql, new
        {
            BuildDbId = buildDbId,
            file.FileId,
            file.RelativePath,
            file.PathHash,
            file.PlainHash,
            file.CipherHash,
            file.Algorithm,
            file.Kdf
        });
    }

    await conn.ExecuteAsync(
        "UPDATE builds SET file_count = @FileCount, status = 'files_registered' WHERE id = @BuildDbId",
        new { FileCount = request.Files.Count, BuildDbId = buildDbId });

    return Results.Ok(new { accepted = request.Files.Count, rejected = 0 });
});

encoder.MapPost("/builds/{buildId}/manifest/sign", async (string buildId, ManifestSignRequest request, IDbConnectionFactory db, CryptoService crypto) =>
{
    await using var conn = await db.OpenAsync();
    var signature = crypto.SignForDemoOnly(request.ManifestHash);

    await conn.ExecuteAsync("""
        UPDATE builds
        SET manifest_hash = @ManifestHash,
            manifest_signature = @Signature,
            file_count = @FileCount,
            status = 'signed',
            signed_at = CURRENT_TIMESTAMP
        WHERE build_uid = @BuildId;
        """, new { BuildId = buildId, request.ManifestHash, Signature = signature, request.FileCount });

    return Results.Ok(new ManifestSignResponse(signature, "dev-demo-key", DateTimeOffset.UtcNow));
});

app.MapPost("/api/v1/runtime/lease", async (RuntimeLeaseRequest request, IDbConnectionFactory db, CryptoService crypto, IConfiguration config) =>
{
    await using var conn = await db.OpenAsync();

    var row = await conn.QuerySingleOrDefaultAsync<LeaseQueryRow>("""
        SELECT
            l.id               AS LicenseDbId,
            b.id               AS BuildDbId,
            k.encrypted_secret_key AS EncryptedSecretKey,
            l.status           AS LicenseStatus,
            l.valid_until      AS ValidUntil,
            l.max_activations  AS MaxActivations
        FROM licenses l
        JOIN builds b ON b.license_id = l.id
        JOIN crypto_keys k ON b.key_id = k.id
        WHERE l.license_uid = @LicenseId
          AND b.build_uid = @BuildId
          AND b.manifest_hash = @ManifestHash
        LIMIT 1;
        """, new { request.LicenseId, request.BuildId, request.ManifestHash });

    if (row is null)
        return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "License, build or manifest invalid."));

    if (row.LicenseStatus != "active")
        return Results.BadRequest(ErrorDto.Create("LICENSE_REVOKED", "License is not active."));

    if (row.ValidUntil.HasValue && row.ValidUntil.Value < DateTime.UtcNow)
        return Results.BadRequest(ErrorDto.Create("LICENSE_EXPIRED", "License is expired."));

    var activationUid = "act_" + Ids.NewId();

    var activationUpsertSql = db.IsSqlite
        ? """
          INSERT INTO license_activations (activation_uid, license_id, machine_fingerprint, last_seen_at)
          VALUES (@ActivationUid, @LicenseDbId, @MachineFingerprint, CURRENT_TIMESTAMP)
          ON CONFLICT(license_id, machine_fingerprint) DO UPDATE SET last_seen_at = CURRENT_TIMESTAMP;
          """
        : """
          INSERT INTO license_activations (activation_uid, license_id, machine_fingerprint, last_seen_at)
          VALUES (@ActivationUid, @LicenseDbId, @MachineFingerprint, CURRENT_TIMESTAMP)
          ON DUPLICATE KEY UPDATE last_seen_at = CURRENT_TIMESTAMP;
          """;

    await conn.ExecuteAsync(activationUpsertSql,
        new { ActivationUid = activationUid, LicenseDbId = row.LicenseDbId, request.MachineFingerprint });

    var activationDbId = await conn.ExecuteScalarAsync<long>("""
        SELECT id FROM license_activations
        WHERE license_id = @LicenseDbId AND machine_fingerprint = @MachineFingerprint
        """, new { LicenseDbId = row.LicenseDbId, request.MachineFingerprint });

    var activeCount = await conn.ExecuteScalarAsync<int>("""
        SELECT COUNT(*) FROM license_activations
        WHERE license_id = @LicenseDbId AND status = 'active'
        """, new { LicenseDbId = row.LicenseDbId });

    if (activeCount > (int)row.MaxActivations)
        return Results.BadRequest(ErrorDto.Create("ACTIVATION_LIMIT_REACHED", "Activation limit reached."));

    var ttl = config.GetValue<int>("Security:LeaseTtlMinutes", 1440);
    var graceDays = config.GetValue<int>("Security:GracePeriodDays", 7);
    var issuedAt = DateTimeOffset.UtcNow;
    var expiresAt = issuedAt.AddMinutes(ttl);
    var graceUntil = expiresAt.AddDays(graceDays);
    var leaseUid = "lease_" + Ids.NewId();

    await conn.ExecuteAsync("""
        INSERT INTO runtime_leases
            (lease_uid, license_id, build_id, activation_id, nonce, issued_at, expires_at, grace_until)
        VALUES
            (@LeaseUid, @LicenseDbId, @BuildDbId, @ActivationDbId, @Nonce, @IssuedAt, @ExpiresAt, @GraceUntil);
        """, new
    {
        LeaseUid = leaseUid,
        LicenseDbId = row.LicenseDbId,
        BuildDbId = row.BuildDbId,
        ActivationDbId = activationDbId,
        request.Nonce,
        IssuedAt = issuedAt.UtcDateTime,
        ExpiresAt = expiresAt.UtcDateTime,
        GraceUntil = graceUntil.UtcDateTime
    });

    var runtimeKey = crypto.UnprotectForDemoOnly(row.EncryptedSecretKey);
    var signature = crypto.SignForDemoOnly($"{leaseUid}:{request.BuildId}:{request.MachineFingerprint}:{expiresAt:O}");

    return Results.Ok(new RuntimeLeaseResponse(
        "MMENC-LEASE-1",
        leaseUid,
        request.ProjectId,
        request.CustomerId,
        request.LicenseId,
        request.BuildId,
        "runtime-key",
        runtimeKey,
        issuedAt,
        expiresAt,
        graceUntil,
        signature));
}).RequireRateLimiting(RateLimitPolicyLease);

app.Run();

// Typed query result for the runtime lease endpoint.
// Must be a class with property setters (not a record) so Dapper uses property-level
// type handlers (e.g. DapperDateTimeHandler) rather than constructor injection.
file sealed class LeaseQueryRow
{
    public long LicenseDbId { get; set; }
    public long BuildDbId { get; set; }
    public string EncryptedSecretKey { get; set; } = "";
    public string LicenseStatus { get; set; } = "";
    public DateTime? ValidUntil { get; set; }
    public long MaxActivations { get; set; }
}

// Dapper type handler so SQLite TEXT datetime columns map to DateTime correctly.
file sealed class DapperDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override void SetValue(System.Data.IDbDataParameter parameter, DateTime value)
        => parameter.Value = value.ToString("yyyy-MM-dd HH:mm:ss");

    public override DateTime Parse(object value)
        => DateTime.Parse(value.ToString()!, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}

public partial class Program { }
