using Xunit;
using Dapper;
using Microsoft.Data.Sqlite;
using MmProtect.LicenseServer.Data;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace MmProtect.LicenseServer.Tests;

// Minimal E2E tests that spin up the ASP.NET Core server in-process against SQLite.
// Each test gets a fresh empty database so tests are isolated.
public sealed class SmokeTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    public SmokeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mmtest_{Guid.NewGuid():N}.db");

        // Apply the SQLite schema on a non-pooled connection so that
        // "PRAGMA foreign_keys=ON" in the schema file does not leak into
        // the connection pool that the in-process server uses.
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        var schema = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database", "sqlite", "schema.sql"));
        // SQLite pragma commands can't run in a multi-statement batch via Dapper — split on ';'
        foreach (var stmt in schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(stmt))
                conn.Execute(stmt);
        }

        SqlMapper.AddTypeHandler(new DateTimeHandler());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DatabaseProvider", "sqlite");
                builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
                builder.UseSetting("Security:EncoderApiKeys:0", "test-api-key");
                builder.UseSetting("Security:AdminApiKeys:0", "test-admin-key");
                builder.UseSetting("Security:LeaseTtlMinutes", "60");
                builder.UseSetting("Security:GracePeriodDays", "7");
            });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");
    }

    [Fact]
    public async Task Health_Returns_Ok()
    {
        var resp = await _client.GetAsync("/health");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
    }

    [Fact]
    public async Task CustomerUpsert_CreatesAndDeduplicates()
    {
        // First upsert → created=true
        var r1 = await UpsertCustomerAsync("cref-001", "Test GmbH");
        Assert.True(r1.Created);
        var id1 = r1.CustomerId;

        // Second upsert with same ref → created=false, same ID
        var r2 = await UpsertCustomerAsync("cref-001", "Test GmbH Updated");
        Assert.False(r2.Created);
        Assert.Equal(id1, r2.CustomerId);
    }

    [Fact]
    public async Task ProjectUpsert_CreatesProject()
    {
        var r = await PostJsonAsync<ProjectUpsertDto>("/api/v1/encoder/projects/upsert", new
        {
            projectKey = "proj-smoke-001",
            name = "Smoke Test Project",
            phpMinVersion = "8.4",
            description = "test"
        });

        Assert.True(r.Created);
        Assert.StartsWith("proj_", r.ProjectId);
    }

    [Fact]
    public async Task FullEncoderFlow_CustomerProjectLicenseBuild()
    {
        var customer = await UpsertCustomerAsync("cref-flow-001", "Flow Customer");
        var project = await PostJsonAsync<ProjectUpsertDto>("/api/v1/encoder/projects/upsert", new
        {
            projectKey = "proj-flow-001",
            name = "Flow Project",
            phpMinVersion = "8.4",
            description = ""
        });
        var license = await PostJsonAsync<LicenseUpsertDto>("/api/v1/encoder/licenses/upsert", new
        {
            customerId = customer.CustomerId,
            projectId = project.ProjectId,
            licenseKey = "MM-FLOW-0001",
            validFrom = "2026-01-01T00:00:00Z",
            validUntil = "2028-01-01T00:00:00Z",
            maxActivations = 3,
            features = new[] { "base" }
        });

        Assert.True(license.Created);
        Assert.StartsWith("lic_", license.LicenseId);

        var build = await PostJsonAsync<BuildStartDto>("/api/v1/encoder/builds/start", new
        {
            projectId = project.ProjectId,
            customerId = customer.CustomerId,
            licenseId = license.LicenseId,
            version = "1.0.0",
            sourceRevision = "abc123",
            encoderVersion = "test"
        });

        Assert.StartsWith("build_", build.BuildId);
        Assert.False(string.IsNullOrEmpty(build.BuildKey));

        // Register a file
        var fileResp = await _client.PostAsJsonAsync(
            $"/api/v1/encoder/builds/{build.BuildId}/files", new
            {
                files = new[]
                {
                    new
                    {
                        fileId = "file_abc001",
                        relativePath = "src/App/Application.php",
                        pathHash = "sha256:aabbcc",
                        plainHash = "sha256:112233",
                        cipherHash = "sha256:445566",
                        algorithm = "AES-256-GCM",
                        kdf = "HKDF-SHA256"
                    }
                }
            });
        fileResp.EnsureSuccessStatusCode();

        // Sign manifest
        var manifestHash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
        var signResp = await PostJsonAsync<ManifestSignDto>(
            $"/api/v1/encoder/builds/{build.BuildId}/manifest/sign", new
            {
                manifestHash,
                fileCount = 1
            });

        Assert.False(string.IsNullOrEmpty(signResp.ManifestSignature));
    }

    [Fact]
    public async Task RuntimeLease_GrantedForValidLicense()
    {
        // Set up the full build pipeline first
        var customer = await UpsertCustomerAsync("cref-lease-001", "Lease Customer");
        var project = await PostJsonAsync<ProjectUpsertDto>("/api/v1/encoder/projects/upsert", new
        {
            projectKey = "proj-lease-001",
            name = "Lease Project",
            phpMinVersion = "8.4",
            description = ""
        });
        var license = await PostJsonAsync<LicenseUpsertDto>("/api/v1/encoder/licenses/upsert", new
        {
            customerId = customer.CustomerId,
            projectId = project.ProjectId,
            licenseKey = "MM-LEASE-0001",
            validFrom = "2026-01-01T00:00:00Z",
            validUntil = "2028-01-01T00:00:00Z",
            maxActivations = 3,
            features = Array.Empty<string>()
        });
        var build = await PostJsonAsync<BuildStartDto>("/api/v1/encoder/builds/start", new
        {
            projectId = project.ProjectId,
            customerId = customer.CustomerId,
            licenseId = license.LicenseId,
            version = "1.0.0",
            sourceRevision = "HEAD",
            encoderVersion = "test"
        });

        var manifestHash = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("test-manifest-content"))).ToLowerInvariant();

        await _client.PostAsJsonAsync($"/api/v1/encoder/builds/{build.BuildId}/manifest/sign", new
        {
            manifestHash,
            fileCount = 0
        });

        // Now request a runtime lease (no API key auth for this endpoint)
        var leaseClient = _factory.CreateClient();
        var leaseResp = await leaseClient.PostAsJsonAsync("/api/v1/runtime/lease", new
        {
            projectId = project.ProjectId,
            customerId = customer.CustomerId,
            licenseId = license.LicenseId,
            buildId = build.BuildId,
            manifestHash,
            machineFingerprint = "sha256:" + new string('a', 64),
            nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
        });

        leaseResp.EnsureSuccessStatusCode();
        var leaseBody = await leaseResp.Content.ReadAsStringAsync();
        Assert.Contains("MMENC-LEASE-1", leaseBody);
        Assert.Contains("runtimeKey", leaseBody);
    }

    /* ── Security tests ──────────────────────────────────────────────────── */

    [Fact]
    public async Task RuntimeLease_ExpiredLicense_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-exp", "proj-exp", "MM-EXP-001",
            validFrom: "2020-01-01T00:00:00Z",
            validUntil: "2021-01-01T00:00:00Z");  // past

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LICENSE_EXPIRED", body);
    }

    [Fact]
    public async Task RuntimeLease_FutureLicense_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-fut", "proj-fut", "MM-FUT-001",
            validFrom: "2099-01-01T00:00:00Z",
            validUntil: "2100-01-01T00:00:00Z");  // not yet valid

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LICENSE_NOT_YET_VALID", body);
    }

    [Fact]
    public async Task RuntimeLease_RevokedLicenseStatus_Rejected()
    {
        var (buildClient, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-rev-status", "proj-rev-status", "MM-REVST-001");

        // Directly update license status in DB
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        await conn.ExecuteAsync("UPDATE licenses SET status = 'revoked' WHERE license_uid = @Id", new { Id = licenseId });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LICENSE_REVOKED", body);
    }

    [Fact]
    public async Task RuntimeLease_RevokedViaRevocationsTable_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-revtbl", "proj-revtbl", "MM-REVTBL-001");

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        await conn.ExecuteAsync(
            "INSERT INTO revocations (revocation_uid, entity_type, entity_uid, reason) VALUES (@Uid, 'license', @LicId, 'test')",
            new { Uid = "rev_" + Guid.NewGuid().ToString("N"), LicId = licenseId });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task RuntimeLease_RevokedBuild_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-revbld", "proj-revbld", "MM-REVBLD-001");

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        await conn.ExecuteAsync(
            "UPDATE builds SET status = 'revoked' WHERE build_uid = @Id", new { Id = buildId });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task RuntimeLease_ActivationLimitReached_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-actlim", "proj-actlim", "MM-ACTLIM-001", maxActivations: 1);

        // First lease → should succeed (inserts 1 activation)
        var first = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            fingerprint: "sha256:" + new string('b', 64));
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);

        // Second lease with different machine → limit reached
        var second = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            fingerprint: "sha256:" + new string('c', 64));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("ACTIVATION_LIMIT_REACHED", body);
    }

    [Fact]
    public async Task RuntimeLease_InvalidManifestHash_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, _) = await SetupBuildAsync(
            "cref-badmf", "proj-badmf", "MM-BADMF-001");

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, "sha256:wrong000");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RuntimeLease_RevokedActivation_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-actrev", "proj-actrev", "MM-ACTREV-001", maxActivations: 5);

        const string fingerprint = "sha256:" + "d" + "0000000000000000000000000000000000000000000000000000000000000000";

        // First lease: creates the activation
        var first = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fingerprint);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);

        // Revoke the activation via direct DB update (admin endpoint uses activation_uid which we resolve here)
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        await conn.ExecuteAsync(
            "UPDATE license_activations SET status = 'revoked' WHERE machine_fingerprint = @Fp",
            new { Fp = fingerprint });

        // Second lease for the same machine must now be denied
        var second = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fingerprint);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("ACTIVATION_REVOKED", body);
    }

    [Fact]
    public async Task RuntimeLease_AllowedDomain_Granted()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-dom-ok", "proj-dom-ok", "MM-DOMOK-001",
            constraints: new { allowedDomains = new[] { "example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            domain: "example.com");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task RuntimeLease_BlockedDomain_Rejected()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-dom-bad", "proj-dom-bad", "MM-DOMBAD-001",
            constraints: new { allowedDomains = new[] { "example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            domain: "attacker.com");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task AdminRevoke_License_BlocksLease()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-admrev", "proj-admrev", "MM-ADMREV-001");

        // Revoke via admin API
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var revokeResp = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/revoke", new { reason = "test" });
        revokeResp.EnsureSuccessStatusCode();
        var revokeBody = await revokeResp.Content.ReadAsStringAsync();
        Assert.Contains("true", revokeBody);

        // Lease attempt after revocation → rejected
        var leaseResp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, leaseResp.StatusCode);
        var body = await leaseResp.Content.ReadAsStringAsync();
        Assert.True(body.Contains("LEASE_DENIED") || body.Contains("LICENSE_REVOKED"),
            $"Expected LEASE_DENIED or LICENSE_REVOKED but got: {body}");
    }

    /* ── Hostname constraint tests ───────────────────────────────────────── */

    [Fact]
    public async Task Constraint_AllowedHostname_ExactMatch_Granted()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-hn-exact-ok", "proj-hn-exact-ok", "MM-HNEXOK-001",
            constraints: new { allowedHostnames = new[] { "mail.example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            hostname: "mail.example.com");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Constraint_AllowedHostname_ExactMatch_Blocked()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-hn-exact-bad", "proj-hn-exact-bad", "MM-HNEXBAD-001",
            constraints: new { allowedHostnames = new[] { "mail.example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            hostname: "evil.com");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task Constraint_AllowedHostname_Wildcard_Granted()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-hn-wild-ok", "proj-hn-wild-ok", "MM-HNWILDOK-001",
            constraints: new { allowedHostnames = new[] { "*.example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            hostname: "app.example.com");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Constraint_AllowedHostname_Wildcard_Blocked()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-hn-wild-bad", "proj-hn-wild-bad", "MM-HNWILDBAD-001",
            constraints: new { allowedHostnames = new[] { "*.example.com" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            hostname: "app.evil.com");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task Constraint_AllowedIp_Granted()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-ip-ok", "proj-ip-ok", "MM-IPOK-001",
            constraints: new { allowedIps = new[] { "10.0.0.1" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            publicIp: "10.0.0.1");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Constraint_AllowedIp_Blocked()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-ip-bad", "proj-ip-bad", "MM-IPBAD-001",
            constraints: new { allowedIps = new[] { "10.0.0.1" } });

        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash,
            publicIp: "10.0.0.2");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task Constraint_NoValueSent_ConstraintSkipped()
    {
        // Server skips constraint when the loader doesn't send the field
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-skip-ok", "proj-skip-ok", "MM-SKIPOK-001",
            constraints: new { allowedHostnames = new[] { "locked.example.com" } });

        // hostname not sent → constraint check is skipped → lease granted
        var resp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    /* ── Revocation via Admin API ─────────────────────────────────────────── */

    [Fact]
    public async Task Admin_RevokeBuild_ViaAdminApi_BlocksLease()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-bldrev-api", "proj-bldrev-api", "MM-BLDREVAPI-001");

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var revokeResp = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/builds/{buildId}/revoke", new { reason = "test" });
        revokeResp.EnsureSuccessStatusCode();

        var leaseResp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, leaseResp.StatusCode);
        var body = await leaseResp.Content.ReadAsStringAsync();
        Assert.Contains("LEASE_DENIED", body);
    }

    [Fact]
    public async Task Admin_RevokeActivation_ViaAdminApi_BlocksLease()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-actrevapi", "proj-actrevapi", "MM-ACTREVAPI-001", maxActivations: 5);

        const string fp = "sha256:eeee0000000000000000000000000000000000000000000000000000000000ee";

        // First lease → creates the activation
        var first = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fp);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);

        // Get activation_uid via admin API
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var listResp = await adminClient.GetAsync($"/api/v1/admin/activations?licenseUid={licenseId}");
        listResp.EnsureSuccessStatusCode();
        var listJson = await listResp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(listJson);
        var activationId = doc.RootElement
            .GetProperty("activations")[0]
            .GetProperty("activationId").GetString()!;

        // Revoke via admin API
        var revokeResp = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/activations/{activationId}/revoke", new { reason = "test" });
        revokeResp.EnsureSuccessStatusCode();

        // Same machine tries again → blocked
        var second = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fp);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("ACTIVATION_REVOKED", body);
    }

    [Fact]
    public async Task Admin_DeleteActivation_FreesSlot()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-actdel", "proj-actdel", "MM-ACTDEL-001", maxActivations: 1);

        const string fpA = "sha256:aaaa000000000000000000000000000000000000000000000000000000000aaa";
        const string fpB = "sha256:bbbb000000000000000000000000000000000000000000000000000000000bbb";

        // Machine A fills the single slot
        var r1 = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fpA);
        Assert.Equal(System.Net.HttpStatusCode.OK, r1.StatusCode);

        // Machine B is rejected
        var r2 = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fpB);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r2.StatusCode);
        Assert.Contains("ACTIVATION_LIMIT_REACHED", await r2.Content.ReadAsStringAsync());

        // Admin deletes machine A's activation
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var listResp = await adminClient.GetAsync($"/api/v1/admin/activations?licenseUid={licenseId}");
        listResp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var activationId = doc.RootElement.GetProperty("activations")[0]
            .GetProperty("activationId").GetString()!;

        var delResp = await adminClient.DeleteAsync($"/api/v1/admin/activations/{activationId}");
        delResp.EnsureSuccessStatusCode();

        // Machine B can now activate
        var r3 = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash, fingerprint: fpB);
        Assert.Equal(System.Net.HttpStatusCode.OK, r3.StatusCode);
    }

    /* ── Admin API endpoint coverage ─────────────────────────────────────── */

    [Fact]
    public async Task AdminApi_RequiresAuth_WithoutKey_Unauthorized()
    {
        var anonClient = _factory.CreateClient();
        var resp = await anonClient.GetAsync("/api/v1/admin/licenses");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AdminApi_ListLicenses_ReturnsCreated()
    {
        var (_, _, licenseId, _, _) = await SetupBuildAsync(
            "cref-adminlist", "proj-adminlist", "MM-ADMINLIST-001");

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var resp = await adminClient.GetAsync("/api/v1/admin/licenses");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(licenseId, body);
    }

    [Fact]
    public async Task AdminApi_ListLicenses_FilterByStatus()
    {
        var (_, _, licenseId, _, _) = await SetupBuildAsync(
            "cref-admfilt", "proj-admfilt", "MM-ADMFILT-001");

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        // Active filter must include our license
        var active = await adminClient.GetAsync("/api/v1/admin/licenses?status=active");
        active.EnsureSuccessStatusCode();
        Assert.Contains(licenseId, await active.Content.ReadAsStringAsync());

        // Revoked filter must NOT include it before revocation
        var revoked = await adminClient.GetAsync("/api/v1/admin/licenses?status=revoked");
        revoked.EnsureSuccessStatusCode();
        Assert.DoesNotContain(licenseId, await revoked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminApi_ListActivations_AfterLease_ReturnsEntry()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-admact", "proj-admact", "MM-ADMACT-001");

        var leaseResp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        leaseResp.EnsureSuccessStatusCode();

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var resp = await adminClient.GetAsync($"/api/v1/admin/activations?licenseUid={licenseId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(licenseId, body);
        Assert.Contains("active", body);
    }

    [Fact]
    public async Task AdminApi_AuditLog_AfterLeaseGrant_ContainsEntry()
    {
        var (_, leaseClient, licenseId, buildId, manifestHash) = await SetupBuildAsync(
            "cref-admaudit", "proj-admaudit", "MM-ADMAUDIT-001");

        var leaseResp = await RequestLeaseAsync(leaseClient, licenseId, buildId, manifestHash);
        leaseResp.EnsureSuccessStatusCode();

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var resp = await adminClient.GetAsync($"/api/v1/admin/audit-log?entityUid={licenseId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("lease_granted", body);
    }

    [Fact]
    public async Task AdminApi_Stats_ReturnsAllFields()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        var resp = await adminClient.GetAsync("/api/v1/admin/stats");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("licenses", body);
        Assert.Contains("builds", body);
        Assert.Contains("activations", body);
        Assert.Contains("leases", body);
    }

    [Fact]
    public async Task AdminApi_ApiClients_CreateListDelete()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-key");

        // Create
        var createResp = await adminClient.PostAsJsonAsync("/api/v1/admin/api-clients",
            new { name = "test-client-smoke", scope = "encoder" });
        createResp.EnsureSuccessStatusCode();
        var createBody = await createResp.Content.ReadAsStringAsync();
        Assert.Contains("test-client-smoke", createBody);
        using var createDoc = System.Text.Json.JsonDocument.Parse(createBody);
        var clientUid = createDoc.RootElement.GetProperty("clientUid").GetString()!;
        Assert.StartsWith("client_", clientUid);

        // List — new client appears
        var listResp = await adminClient.GetAsync("/api/v1/admin/api-clients");
        listResp.EnsureSuccessStatusCode();
        Assert.Contains(clientUid, await listResp.Content.ReadAsStringAsync());

        // Delete (soft-delete: sets is_active=0, record stays in list with isActive=false)
        var delResp = await adminClient.DeleteAsync($"/api/v1/admin/api-clients/{clientUid}");
        delResp.EnsureSuccessStatusCode();
        var delBody = await delResp.Content.ReadAsStringAsync();
        Assert.Contains("true", delBody);

        // List — client still appears but is now inactive
        var listAfter = await adminClient.GetAsync("/api/v1/admin/api-clients");
        listAfter.EnsureSuccessStatusCode();
        var listAfterBody = await listAfter.Content.ReadAsStringAsync();
        Assert.Contains(clientUid, listAfterBody);
        using var afterDoc = System.Text.Json.JsonDocument.Parse(listAfterBody);
        var matchingClient = afterDoc.RootElement.GetProperty("clients").EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("clientUid").GetString() == clientUid);
        Assert.False(matchingClient.GetProperty("isActive").GetBoolean());
    }

    /* ── Security test helpers ───────────────────────────────────────────── */

    private async Task<(HttpClient BuildClient, HttpClient LeaseClient, string LicenseId, string BuildId, string ManifestHash)>
        SetupBuildAsync(
            string custRef, string projKey, string licKey,
            string validFrom = "2026-01-01T00:00:00Z",
            string? validUntil = "2030-01-01T00:00:00Z",
            int maxActivations = 5,
            object? constraints = null)
    {
        var buildClient = _factory.CreateClient();
        buildClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");

        var customer = await PostJsonViaAsync<CustomerUpsertDto>(buildClient,
            "/api/v1/encoder/customers/upsert", new
            { externalCustomerRef = custRef, name = custRef, email = "t@example.com", notes = "" });

        var project = await PostJsonViaAsync<ProjectUpsertDto>(buildClient,
            "/api/v1/encoder/projects/upsert", new
            { projectKey = projKey, name = projKey, phpMinVersion = "8.4", description = "" });

        var license = await PostJsonViaAsync<LicenseUpsertDto>(buildClient,
            "/api/v1/encoder/licenses/upsert", new
            {
                customerId = customer.CustomerId,
                projectId = project.ProjectId,
                licenseKey = licKey,
                validFrom,
                validUntil,
                maxActivations,
                features = Array.Empty<string>(),
                constraints
            });

        var build = await PostJsonViaAsync<BuildStartDto>(buildClient,
            "/api/v1/encoder/builds/start", new
            {
                projectId = project.ProjectId,
                customerId = customer.CustomerId,
                licenseId = license.LicenseId,
                version = "1.0.0",
                sourceRevision = "HEAD",
                encoderVersion = "test"
            });

        var manifestHash = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(licKey + "-manifest"))).ToLowerInvariant();

        await buildClient.PostAsJsonAsync($"/api/v1/encoder/builds/{build.BuildId}/manifest/sign",
            new { manifestHash, fileCount = 0 });

        var leaseClient = _factory.CreateClient();

        return (buildClient, leaseClient, license.LicenseId, build.BuildId, manifestHash);
    }

    private async Task<HttpResponseMessage> RequestLeaseAsync(
        HttpClient client,
        string licenseId,
        string buildId,
        string manifestHash,
        string? fingerprint = null,
        string? domain = null,
        string? hostname = null,
        string? publicIp = null)
    {
        return await client.PostAsJsonAsync("/api/v1/runtime/lease", new
        {
            projectId = "proj-test",
            customerId = "cust-test",
            licenseId,
            buildId,
            manifestHash,
            machineFingerprint = fingerprint ?? ("sha256:" + new string('a', 64)),
            loaderVersion = "0.1.0",
            phpVersion = "8.4.0",
            sapi = "cli",
            nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            domain,
            hostname,
            publicIp
        });
    }

    private async Task<T> PostJsonViaAsync<T>(HttpClient client, string url, object body)
    {
        var resp = await client.PostAsJsonAsync(url, body);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    // ---- Helpers ----

    private async Task<CustomerUpsertDto> UpsertCustomerAsync(string extRef, string name)
    {
        return await PostJsonAsync<CustomerUpsertDto>("/api/v1/encoder/customers/upsert", new
        {
            externalCustomerRef = extRef,
            name,
            email = "test@example.com",
            notes = ""
        });
    }

    private async Task<T> PostJsonAsync<T>(string url, object body)
    {
        var resp = await _client.PostAsJsonAsync(url, body);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        // On Windows, SQLite connection pooling keeps file handles open after
        // factory disposal. ClearAllPools() flushes the pool synchronously so
        // File.Delete succeeds without depending on GC finalizer ordering.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}

// Dapper DateTime handler (same as in server Program.cs, duplicated here for test isolation)
internal sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override void SetValue(System.Data.IDbDataParameter p, DateTime v) => p.Value = v.ToString("yyyy-MM-dd HH:mm:ss");
    public override DateTime Parse(object v) => DateTime.Parse(v.ToString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}

// Minimal DTO types for response deserialization (field names match server response JSON)
internal sealed record CustomerUpsertDto(string CustomerId, bool Created);
internal sealed record ProjectUpsertDto(string ProjectId, bool Created);
internal sealed record LicenseUpsertDto(string LicenseId, bool Created);
internal sealed record BuildStartDto(string BuildId, string KeyId, string BuildKey, string ManifestSalt);
internal sealed record ManifestSignDto(string ManifestSignature, string VendorPublicKeyId, DateTimeOffset ServerTimeUtc);
