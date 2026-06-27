using Dapper;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MmProtect.LicenseServer.Data;
using MmProtect.LicenseServer.Models;
using MmProtect.LicenseServer.Security;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
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
builder.Services.AddSingleton<AuditService>();
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

// ── Startup Security Warnings ─────────────────────────────────────────────
if (!app.Services.GetRequiredService<CryptoService>().HasKeyEncryptionKey)
    app.Logger.LogWarning(
        "[mmprotect] Security:KeyEncryptionKey not configured — build keys stored unencrypted (DEV ONLY)");
if (!app.Services.GetRequiredService<CryptoService>().HasSigningKey)
    app.Logger.LogWarning(
        "[mmprotect] Security:SigningPrivateKeyFile not configured — lease signatures use HMAC-SHA256 (DEV ONLY)");
var adminKeys = app.Configuration.GetSection("Security:AdminApiKeys").Get<string[]>() ?? [];
if (adminKeys.Length == 0 || adminKeys.All(k => k.Contains("change-me", StringComparison.OrdinalIgnoreCase)))
    app.Logger.LogWarning(
        "[mmprotect] Security:AdminApiKeys not configured or uses default key — set before production use");

// ForwardedHeaders MUST run before any middleware that reads the IP.
if (proxyEnabled)
    app.UseForwardedHeaders();

// Serve the embedded Vue Admin UI from wwwroot/admin/
app.UseStaticFiles();

app.UseRateLimiter();

/* ── Correlation ID ─────────────────────────────────────────────────────── */
app.Use(async (ctx, next) =>
{
    var requestId = ctx.Request.Headers["X-Request-ID"].FirstOrDefault()
                    ?? ctx.TraceIdentifier;
    ctx.Response.Headers["X-Request-ID"] = requestId;
    await next(ctx);
});

app.MapGet("/health", async (IDbConnectionFactory db) =>
{
    string dbStatus;
    try
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteScalarAsync<int>("SELECT 1");
        dbStatus = "ok";
    }
    catch { dbStatus = "error"; }

    var payload = new
    {
        status   = dbStatus == "ok" ? "ok" : "degraded",
        version  = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
        timeUtc  = DateTimeOffset.UtcNow,
        database = dbStatus
    };
    return dbStatus == "ok" ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
});

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

    return Results.Ok(new CustomerUpsertResponse(customerUid!, customerUid == uid));
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

    return Results.Ok(new ProjectUpsertResponse(projectUid!, projectUid == uid));
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
              (license_uid, customer_id, project_id, license_key, valid_from, valid_until, max_activations, features, constraints, status)
          VALUES
              (@Uid, @CustomerDbId, @ProjectDbId, @LicenseKey, @ValidFrom, @ValidUntil, @MaxActivations, @FeaturesJson, @ConstraintsJson, 'active')
          ON CONFLICT(license_key) DO UPDATE SET
              valid_from = excluded.valid_from, valid_until = excluded.valid_until,
              max_activations = excluded.max_activations, features = excluded.features, constraints = excluded.constraints;
          """
        : """
          INSERT INTO licenses
              (license_uid, customer_id, project_id, license_key, valid_from, valid_until, max_activations, features, constraints, status)
          VALUES
              (@Uid, @CustomerDbId, @ProjectDbId, @LicenseKey, @ValidFrom, @ValidUntil, @MaxActivations, @FeaturesJson, @ConstraintsJson, 'active')
          ON DUPLICATE KEY UPDATE
              valid_from = VALUES(valid_from), valid_until = VALUES(valid_until),
              max_activations = VALUES(max_activations), features = VALUES(features), constraints = VALUES(constraints), updated_at = CURRENT_TIMESTAMP;
          """;

    var constraintsJson = request.Constraints is null ? null
        : JsonSerializer.Serialize(request.Constraints, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    await conn.ExecuteAsync(upsertSql, new
    {
        Uid = uid,
        CustomerDbId = customerDbId,
        ProjectDbId = projectDbId,
        request.LicenseKey,
        ValidFrom = request.ValidFrom.UtcDateTime,
        ValidUntil = request.ValidUntil?.UtcDateTime,
        request.MaxActivations,
        FeaturesJson = JsonCanonical.Serialize(request.Features ?? []),
        ConstraintsJson = constraintsJson
    });

    var licenseUid = await conn.ExecuteScalarAsync<string>(
        "SELECT license_uid FROM licenses WHERE license_key = @LicenseKey",
        new { request.LicenseKey });

    return Results.Ok(new LicenseUpsertResponse(licenseUid!, licenseUid == uid));
});

encoder.MapPost("/builds/start", async (BuildStartRequest request, IDbConnectionFactory db, CryptoService crypto, AuditService audit, HttpContext httpContext) =>
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
        """, new { KeyUid = keyUid, EncryptedSecretKey = crypto.ProtectBuildKey(buildKey) });

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

    await audit.LogAsync("encoder", "build_started", "build", buildUid,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { buildId = buildUid, licenseId = request.LicenseId, version = request.Version });

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

encoder.MapPost("/builds/{buildId}/manifest/sign", async (string buildId, ManifestSignRequest request, IDbConnectionFactory db, CryptoService crypto, AuditService audit, HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();
    var signature = crypto.SignLease(request.ManifestHash);

    await conn.ExecuteAsync("""
        UPDATE builds
        SET manifest_hash = @ManifestHash,
            manifest_signature = @Signature,
            file_count = @FileCount,
            status = 'signed',
            signed_at = CURRENT_TIMESTAMP
        WHERE build_uid = @BuildId;
        """, new { BuildId = buildId, request.ManifestHash, Signature = signature, request.FileCount });

    await audit.LogAsync("encoder", "build_signed", "build", buildId,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { buildId, fileCount = request.FileCount });

    return Results.Ok(new ManifestSignResponse(signature, "dev-demo-key", DateTimeOffset.UtcNow));
});

app.MapPost("/api/v1/runtime/lease", async (
    RuntimeLeaseRequest request,
    IDbConnectionFactory db,
    CryptoService crypto,
    AuditService audit,
    HttpContext httpContext,
    IConfiguration config) =>
{
    await using var conn = await db.OpenAsync();
    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

    var row = await conn.QuerySingleOrDefaultAsync<LeaseQueryRow>("""
        SELECT
            l.id               AS LicenseDbId,
            l.license_uid      AS LicenseUid,
            b.id               AS BuildDbId,
            b.build_uid        AS BuildUid,
            b.status           AS BuildStatus,
            k.encrypted_secret_key AS EncryptedSecretKey,
            l.status           AS LicenseStatus,
            l.valid_from       AS ValidFrom,
            l.valid_until      AS ValidUntil,
            l.max_activations  AS MaxActivations,
            l.features         AS FeaturesJson,
            l.constraints      AS ConstraintsJson
        FROM licenses l
        JOIN builds b ON b.license_id = l.id
        JOIN crypto_keys k ON b.key_id = k.id
        WHERE l.license_uid = @LicenseId
          AND b.build_uid = @BuildId
          AND b.manifest_hash = @ManifestHash
        LIMIT 1;
        """, new { request.LicenseId, request.BuildId, request.ManifestHash });

    if (row is null)
    {
        await audit.LogAsync("loader", "lease_denied", "license", request.LicenseId, clientIp,
            new { reason = "LEASE_DENIED", buildId = request.BuildId });
        return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "License, build or manifest invalid."));
    }

    /* Build revocation: check builds.status column */
    if (row.BuildStatus == "revoked")
    {
        await audit.LogAsync("loader", "lease_denied", "build", row.BuildUid, clientIp,
            new { reason = "LEASE_DENIED", detail = "build_revoked" });
        return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "Build has been revoked."));
    }

    /* Revocations table: license or build explicitly revoked */
    var revokedCount = await conn.ExecuteScalarAsync<int>("""
        SELECT COUNT(*) FROM revocations
        WHERE (entity_type = 'license' AND entity_uid = @LicenseUid)
           OR (entity_type = 'build'   AND entity_uid = @BuildUid)
        """, new { LicenseUid = row.LicenseUid, BuildUid = row.BuildUid });

    if (revokedCount > 0)
    {
        await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
            new { reason = "LEASE_DENIED", detail = "revocations_table_hit" });
        return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "License or build has been revoked."));
    }

    /* License status */
    if (row.LicenseStatus != "active")
    {
        await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
            new { reason = "LICENSE_REVOKED", status = row.LicenseStatus });
        return Results.BadRequest(ErrorDto.Create("LICENSE_REVOKED", "License is not active."));
    }

    /* License expiry */
    if (row.ValidUntil.HasValue && row.ValidUntil.Value < DateTime.UtcNow)
    {
        await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
            new { reason = "LICENSE_EXPIRED" });
        return Results.BadRequest(ErrorDto.Create("LICENSE_EXPIRED", "License is expired."));
    }

    /* License validity start (validFrom) */
    if (row.ValidFrom.HasValue && row.ValidFrom.Value > DateTime.UtcNow)
    {
        await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
            new { reason = "LICENSE_NOT_YET_VALID" });
        return Results.BadRequest(ErrorDto.Create("LICENSE_NOT_YET_VALID", "License is not yet valid."));
    }

    /* License constraints: hostname / domain / IP binding (only validated when sent by loader) */
    if (!string.IsNullOrEmpty(row.ConstraintsJson))
    {
        try
        {
            var constraints = JsonSerializer.Deserialize<LicenseConstraints>(
                row.ConstraintsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (constraints?.AllowedHostnames?.Length > 0 && !string.IsNullOrEmpty(request.Hostname))
            {
                if (!constraints.AllowedHostnames.Any(h =>
                    string.Equals(h.TrimStart('*', '.'), request.Hostname, StringComparison.OrdinalIgnoreCase) ||
                    (h.StartsWith("*.") && request.Hostname.EndsWith(h[1..], StringComparison.OrdinalIgnoreCase))))
                {
                    await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
                        new { reason = "HOSTNAME_NOT_ALLOWED", hostname = request.Hostname });
                    return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "Hostname not permitted by license."));
                }
            }

            if (constraints?.AllowedDomains?.Length > 0 && !string.IsNullOrEmpty(request.Domain))
            {
                if (!constraints.AllowedDomains.Any(d =>
                    string.Equals(d, request.Domain, StringComparison.OrdinalIgnoreCase)))
                {
                    await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
                        new { reason = "DOMAIN_NOT_ALLOWED", domain = request.Domain });
                    return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "Domain not permitted by license."));
                }
            }

            if (constraints?.AllowedIps?.Length > 0 && !string.IsNullOrEmpty(request.PublicIp))
            {
                if (!constraints.AllowedIps.Any(ip =>
                    string.Equals(ip, request.PublicIp, StringComparison.OrdinalIgnoreCase)))
                {
                    await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
                        new { reason = "IP_NOT_ALLOWED", publicIp = request.PublicIp });
                    return Results.BadRequest(ErrorDto.Create("LEASE_DENIED", "IP address not permitted by license."));
                }
            }
        }
        catch { /* malformed constraints JSON — skip */ }
    }

    /* Check activation: look up existing record for this machine (includes status) */
    var existingActivation = await conn.QuerySingleOrDefaultAsync<ExistingActivationRow>("""
        SELECT id AS Id, status AS Status FROM license_activations
        WHERE license_id = @LicenseDbId AND machine_fingerprint = @MachineFingerprint
        """, new { LicenseDbId = row.LicenseDbId, request.MachineFingerprint });

    if (existingActivation is not null)
    {
        /* Revoked activation: this machine is explicitly blocked */
        if (existingActivation.Status == "revoked")
        {
            await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
                new { reason = "ACTIVATION_REVOKED", machineFingerprint = request.MachineFingerprint });
            return Results.BadRequest(ErrorDto.Create("ACTIVATION_REVOKED", "Activation has been revoked for this machine."));
        }
    }
    else
    {
        /* New machine: enforce max_activations against currently active slots */
        var activeCount = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM license_activations
            WHERE license_id = @LicenseDbId AND status = 'active'
            """, new { LicenseDbId = row.LicenseDbId });

        if (activeCount >= (int)row.MaxActivations)
        {
            await audit.LogAsync("loader", "lease_denied", "license", row.LicenseUid, clientIp,
                new { reason = "ACTIVATION_LIMIT_REACHED", activeCount, maxActivations = row.MaxActivations });
            return Results.BadRequest(ErrorDto.Create("ACTIVATION_LIMIT_REACHED", "Activation limit reached."));
        }
    }

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

    var runtimeKey = crypto.UnprotectBuildKey(row.EncryptedSecretKey);
    var signature  = crypto.SignLease($"{leaseUid}:{request.BuildId}:{request.MachineFingerprint}:{expiresAt:O}");
    var features   = ParseFeaturesJson(row.FeaturesJson);

    await audit.LogAsync("loader", "lease_granted", "license", row.LicenseUid, clientIp,
        new { leaseId = leaseUid, buildId = request.BuildId });

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
        signature,
        features));
}).RequireRateLimiting(RateLimitPolicyLease);

static string[]? ParseFeaturesJson(string? json)
{
    if (string.IsNullOrEmpty(json)) return null;
    try { return JsonSerializer.Deserialize<string[]>(json); }
    catch { return null; }
}

/* ── Admin API ───────────────────────────────────────────────────────────── */

var adminApi = app.MapGroup("/api/v1/admin");
adminApi.AddEndpointFilter<AdminApiKeyEndpointFilter>();

/* List licenses (optional filter: ?status=active|revoked|suspended|expired) */
adminApi.MapGet("/licenses", async (IDbConnectionFactory db, string? status) =>
{
    await using var conn = await db.OpenAsync();
    var rows = await conn.QueryAsync<AdminLicenseRow>("""
        SELECT l.license_uid AS LicenseId, l.license_key AS LicenseKey,
               c.customer_uid AS CustomerId, p.project_uid AS ProjectId,
               l.status AS Status, l.valid_until AS ValidUntil,
               l.max_activations AS MaxActivations, l.created_at AS CreatedAt
        FROM licenses l
        JOIN customers c ON l.customer_id = c.id
        JOIN projects p ON l.project_id = p.id
        WHERE @Status IS NULL OR l.status = @Status
        ORDER BY l.created_at DESC
        LIMIT 200
        """, new { Status = status });

    var result = rows.Select(r => new AdminLicenseDto(
        r.LicenseId, r.LicenseKey, r.CustomerId, r.ProjectId,
        r.Status, r.ValidUntil, (int)r.MaxActivations, r.CreatedAt)).ToList();

    return Results.Ok(new { licenses = result });
});

/* Revoke a license */
adminApi.MapPost("/licenses/{licenseUid}/revoke", async (
    string licenseUid,
    AdminRevokeRequest? body,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();

    var affected = await conn.ExecuteAsync(
        "UPDATE licenses SET status = 'revoked' WHERE license_uid = @LicenseUid AND status != 'revoked'",
        new { LicenseUid = licenseUid });

    if (affected == 0)
        return Results.NotFound(ErrorDto.Create("NOT_FOUND", "License not found or already revoked."));

    await conn.ExecuteAsync("""
        INSERT INTO revocations (revocation_uid, entity_type, entity_uid, reason)
        VALUES (@RevUid, 'license', @EntityUid, @Reason)
        """, new
    {
        RevUid = "rev_" + Ids.NewId(),
        EntityUid = licenseUid,
        Reason = body?.Reason
    });

    await audit.LogAsync("admin", "license_revoked", "license", licenseUid,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { reason = body?.Reason });

    return Results.Ok(new AdminRevokeResponse(true, $"License {licenseUid} revoked."));
});

/* Revoke a build */
adminApi.MapPost("/builds/{buildUid}/revoke", async (
    string buildUid,
    AdminRevokeRequest? body,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();

    var affected = await conn.ExecuteAsync(
        "UPDATE builds SET status = 'revoked' WHERE build_uid = @BuildUid AND status != 'revoked'",
        new { BuildUid = buildUid });

    if (affected == 0)
        return Results.NotFound(ErrorDto.Create("NOT_FOUND", "Build not found or already revoked."));

    await conn.ExecuteAsync("""
        INSERT INTO revocations (revocation_uid, entity_type, entity_uid, reason)
        VALUES (@RevUid, 'build', @EntityUid, @Reason)
        """, new
    {
        RevUid = "rev_" + Ids.NewId(),
        EntityUid = buildUid,
        Reason = body?.Reason
    });

    await audit.LogAsync("admin", "build_revoked", "build", buildUid,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { reason = body?.Reason });

    return Results.Ok(new AdminRevokeResponse(true, $"Build {buildUid} revoked."));
});

/* List activations (optional filter: ?licenseUid=...) */
adminApi.MapGet("/activations", async (IDbConnectionFactory db, string? licenseUid) =>
{
    await using var conn = await db.OpenAsync();
    var rows = await conn.QueryAsync<AdminActivationRow>("""
        SELECT la.activation_uid AS ActivationId, l.license_uid AS LicenseId,
               la.machine_fingerprint AS MachineFingerprint, la.status AS Status,
               la.first_seen_at AS FirstSeenAt, la.last_seen_at AS LastSeenAt
        FROM license_activations la
        JOIN licenses l ON la.license_id = l.id
        WHERE @LicenseUid IS NULL OR l.license_uid = @LicenseUid
        ORDER BY la.last_seen_at DESC
        LIMIT 200
        """, new { LicenseUid = licenseUid });

    var result = rows.Select(r => new AdminActivationDto(
        r.ActivationId, r.LicenseId, r.MachineFingerprint,
        r.Status, r.FirstSeenAt, r.LastSeenAt)).ToList();

    return Results.Ok(new { activations = result });
});

/* Revoke a single activation */
adminApi.MapPost("/activations/{activationUid}/revoke", async (
    string activationUid,
    AdminRevokeRequest? body,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();

    var affected = await conn.ExecuteAsync(
        "UPDATE license_activations SET status = 'revoked' WHERE activation_uid = @ActivationUid",
        new { ActivationUid = activationUid });

    if (affected == 0)
        return Results.NotFound(ErrorDto.Create("NOT_FOUND", "Activation not found."));

    await conn.ExecuteAsync("""
        INSERT INTO revocations (revocation_uid, entity_type, entity_uid, reason)
        VALUES (@RevUid, 'activation', @EntityUid, @Reason)
        """, new
    {
        RevUid = "rev_" + Ids.NewId(),
        EntityUid = activationUid,
        Reason = body?.Reason
    });

    await audit.LogAsync("admin", "activation_revoked", "activation", activationUid,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { reason = body?.Reason });

    return Results.Ok(new AdminRevokeResponse(true, $"Activation {activationUid} revoked."));
});

/* Reset (delete) a single activation so the machine can re-activate */
adminApi.MapDelete("/activations/{activationUid}", async (
    string activationUid,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();

    var affected = await conn.ExecuteAsync(
        "DELETE FROM license_activations WHERE activation_uid = @ActivationUid",
        new { ActivationUid = activationUid });

    if (affected == 0)
        return Results.NotFound(ErrorDto.Create("NOT_FOUND", "Activation not found."));

    await audit.LogAsync("admin", "activation_reset", "activation", activationUid,
        httpContext.Connection.RemoteIpAddress?.ToString());

    return Results.Ok(new AdminRevokeResponse(true, $"Activation {activationUid} reset."));
});

/* Stats / monitoring */
adminApi.MapGet("/stats", async (IDbConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();

    var licTotal     = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM licenses");
    var licActive    = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM licenses WHERE status = 'active'");
    var licRevoked   = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM licenses WHERE status = 'revoked'");
    var licSuspended = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM licenses WHERE status = 'suspended'");

    var buildTotal  = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM builds");
    var buildSigned = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM builds WHERE status = 'signed'");
    var buildRev    = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM builds WHERE status = 'revoked'");

    var actTotal   = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM license_activations");
    var actActive  = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM license_activations WHERE status = 'active'");
    var actRevoked = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM license_activations WHERE status = 'revoked'");

    var leases24h = await conn.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM runtime_leases WHERE issued_at >= @Since",
        new { Since = DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss") });

    return Results.Ok(new StatsDto(
        new LicenseStatsDto(licTotal, licActive, licRevoked, licSuspended),
        new BuildStatsDto(buildTotal, buildSigned, buildRev),
        new ActivationStatsDto(actTotal, actActive, actRevoked),
        new LeaseStatsDto(leases24h),
        "ok"));
});

/* List API clients (keys are never exposed here) */
adminApi.MapGet("/api-clients", async (IDbConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    var rows = await conn.QueryAsync<ApiClientRow>("""
        SELECT client_uid AS ClientUid, name AS Name, scopes AS Scope,
               is_active AS IsActive, created_at AS CreatedAt
        FROM api_clients
        ORDER BY created_at DESC
        LIMIT 200
        """);

    var result = rows.Select(r => new ApiClientDto(
        r.ClientUid, r.Name, r.Scope ?? "", r.IsActive == 1, r.CreatedAt)).ToList();

    return Results.Ok(new { clients = result });
});

/* Create a new API client — raw key is returned ONCE, never stored */
adminApi.MapPost("/api-clients", async (
    ApiClientCreateRequest request,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(ErrorDto.Create("VALIDATION_FAILED", "Name is required."));

    var scope = request.Scope is "encoder" or "admin" or "all" ? request.Scope : "encoder";
    var uid   = "client_" + Ids.NewId();
    var rawKey = "mm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                                  .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    var keyHash = ApiKeyValidator.ComputeKeyHash(rawKey);

    await using var conn = await db.OpenAsync();
    await conn.ExecuteAsync("""
        INSERT INTO api_clients (client_uid, name, api_key_hash, scopes, is_active)
        VALUES (@ClientUid, @Name, @KeyHash, @Scope, 1)
        """, new { ClientUid = uid, request.Name, KeyHash = keyHash, Scope = scope });

    await audit.LogAsync("admin", "api_client_created", "api_client", uid,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        new { name = request.Name, scope });

    return Results.Ok(new ApiClientCreateResponse(uid, rawKey, request.Name, scope, DateTime.UtcNow));
});

/* Revoke (soft-delete) an API client */
adminApi.MapDelete("/api-clients/{clientUid}", async (
    string clientUid,
    IDbConnectionFactory db,
    AuditService audit,
    HttpContext httpContext) =>
{
    await using var conn = await db.OpenAsync();

    var affected = await conn.ExecuteAsync("""
        UPDATE api_clients
        SET is_active = 0, revoked_at = CURRENT_TIMESTAMP
        WHERE client_uid = @ClientUid AND is_active = 1
        """, new { ClientUid = clientUid });

    if (affected == 0)
        return Results.NotFound(ErrorDto.Create("NOT_FOUND", "API client not found or already revoked."));

    await audit.LogAsync("admin", "api_client_revoked", "api_client", clientUid,
        httpContext.Connection.RemoteIpAddress?.ToString());

    return Results.Ok(new AdminRevokeResponse(true, $"API client {clientUid} revoked."));
});

/* Audit log query */
adminApi.MapGet("/audit-log", async (IDbConnectionFactory db, string? entityType, string? entityUid, int limit = 100) =>
{
    if (limit is < 1 or > 1000) limit = 100;
    await using var conn = await db.OpenAsync();

    var rows = await conn.QueryAsync<AdminAuditRow>("""
        SELECT event_uid AS EventId, actor_type AS ActorType, event_type AS EventType,
               entity_type AS EntityType, entity_uid AS EntityUid,
               ip_address AS IpAddress, details AS Details, created_at AS CreatedAt
        FROM audit_log
        WHERE (@EntityType IS NULL OR entity_type = @EntityType)
          AND (@EntityUid IS NULL OR entity_uid = @EntityUid)
        ORDER BY created_at DESC
        LIMIT @Limit
        """, new { EntityType = entityType, EntityUid = entityUid, Limit = limit });

    var result = rows.Select(r => new AdminAuditEventDto(
        r.EventId, r.ActorType, r.EventType, r.EntityType,
        r.EntityUid, r.IpAddress, r.Details, r.CreatedAt)).ToList();

    return Results.Ok(new { events = result });
});

// SPA fallback: any /admin/* path not matched as a static file → serve admin/index.html
app.MapFallbackToFile("/admin/{**slug}", "admin/index.html");

app.Run();

/* ── Private query result types ─────────────────────────────────────────── */

// Must be classes with setters (not records) so Dapper uses property-level
// type handlers (DapperDateTimeHandler) rather than constructor injection.

file sealed class ExistingActivationRow
{
    public long   Id     { get; set; }
    public string Status { get; set; } = "";
}

file sealed class LeaseQueryRow
{
    public long      LicenseDbId        { get; set; }
    public string    LicenseUid         { get; set; } = "";
    public long      BuildDbId          { get; set; }
    public string    BuildUid           { get; set; } = "";
    public string    BuildStatus        { get; set; } = "";
    public string    EncryptedSecretKey { get; set; } = "";
    public string    LicenseStatus      { get; set; } = "";
    public DateTime? ValidFrom          { get; set; }
    public DateTime? ValidUntil         { get; set; }
    public long      MaxActivations     { get; set; }
    public string?   FeaturesJson       { get; set; }
    public string?   ConstraintsJson    { get; set; }
}

file sealed class ApiClientRow
{
    public string   ClientUid { get; set; } = "";
    public string   Name      { get; set; } = "";
    public string?  Scope     { get; set; }
    public int      IsActive  { get; set; }
    public DateTime CreatedAt { get; set; }
}

file sealed class AdminLicenseRow
{
    public string    LicenseId      { get; set; } = "";
    public string    LicenseKey     { get; set; } = "";
    public string    CustomerId     { get; set; } = "";
    public string    ProjectId      { get; set; } = "";
    public string    Status         { get; set; } = "";
    public DateTime? ValidUntil     { get; set; }
    public long      MaxActivations { get; set; }
    public DateTime  CreatedAt      { get; set; }
}

file sealed class AdminActivationRow
{
    public string   ActivationId       { get; set; } = "";
    public string   LicenseId          { get; set; } = "";
    public string   MachineFingerprint { get; set; } = "";
    public string   Status             { get; set; } = "";
    public DateTime FirstSeenAt        { get; set; }
    public DateTime LastSeenAt         { get; set; }
}

file sealed class AdminAuditRow
{
    public string   EventId    { get; set; } = "";
    public string   ActorType  { get; set; } = "";
    public string   EventType  { get; set; } = "";
    public string?  EntityType { get; set; }
    public string?  EntityUid  { get; set; }
    public string?  IpAddress  { get; set; }
    public string?  Details    { get; set; }
    public DateTime CreatedAt  { get; set; }
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
