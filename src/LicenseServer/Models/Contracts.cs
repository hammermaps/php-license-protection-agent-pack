namespace MmProtect.LicenseServer.Models;

/* ── Encoder API ──────────────────────────────────────────────────────────── */

public sealed record CustomerUpsertRequest(string ExternalCustomerRef, string Name, string? Email, string? Notes);
public sealed record CustomerUpsertResponse(string CustomerId, bool Created);

public sealed record ProjectUpsertRequest(string ProjectKey, string Name, string PhpMinVersion, string? Description);
public sealed record ProjectUpsertResponse(string ProjectId, bool Created);

public sealed record LicenseUpsertRequest(
    string CustomerId,
    string ProjectId,
    string LicenseKey,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    int MaxActivations,
    string[]? Features,
    LicenseConstraints? Constraints = null);

public sealed record LicenseConstraints(
    string[]? AllowedHostnames = null,
    string[]? AllowedDomains = null,
    string[]? AllowedIps = null);

public sealed record LicenseUpsertResponse(string LicenseId, bool Created);

public sealed record BuildStartRequest(
    string ProjectId,
    string CustomerId,
    string LicenseId,
    string Version,
    string? SourceRevision,
    string? EncoderVersion);

public sealed record BuildStartResponse(string BuildId, string KeyId, string BuildKey, string ManifestSalt);

public sealed record BuildFilesRequest(List<BuildFileDto> Files);

public sealed record BuildFileDto(
    string FileId,
    string RelativePath,
    string PathHash,
    string PlainHash,
    string CipherHash,
    string Algorithm,
    string Kdf);

public sealed record ManifestSignRequest(string ManifestHash, int FileCount);
public sealed record ManifestSignResponse(string ManifestSignature, string VendorPublicKeyId, DateTimeOffset ServerTimeUtc);

/* ── Runtime API ─────────────────────────────────────────────────────────── */

public sealed record RuntimeLeaseRequest(
    string ProjectId,
    string CustomerId,
    string LicenseId,
    string BuildId,
    string ManifestHash,
    string MachineFingerprint,
    string LoaderVersion,
    string PhpVersion,
    string Sapi,
    string Nonce,
    string? Hostname = null,
    string? Domain = null,
    string? PublicIp = null);

public sealed record RuntimeLeaseResponse(
    string Format,
    string LeaseId,
    string ProjectId,
    string CustomerId,
    string LicenseId,
    string BuildId,
    string KeyId,
    string RuntimeKey,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset GraceUntil,
    string Signature,
    string[]? Features = null);

/* ── Admin API ───────────────────────────────────────────────────────────── */

public sealed record AdminRevokeRequest(string? Reason);

public sealed record AdminRevokeResponse(bool Revoked, string Message);

public sealed record AdminLicenseDto(
    string LicenseId,
    string LicenseKey,
    string CustomerId,
    string ProjectId,
    string Status,
    DateTime? ValidUntil,
    int MaxActivations,
    DateTime CreatedAt);

public sealed record AdminActivationDto(
    string ActivationId,
    string LicenseId,
    string MachineFingerprint,
    string Status,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

public sealed record AdminAuditEventDto(
    string EventId,
    string ActorType,
    string EventType,
    string? EntityType,
    string? EntityUid,
    string? IpAddress,
    string? Details,
    DateTime CreatedAt);

/* ── API-Client Management ───────────────────────────────────────────────── */

public sealed record ApiClientCreateRequest(string Name, string Scope);

public sealed record ApiClientCreateResponse(
    string ClientUid,
    string ApiKey,
    string Name,
    string Scope,
    DateTime CreatedAt);

public sealed record ApiClientDto(
    string ClientUid,
    string Name,
    string Scope,
    bool IsActive,
    DateTime CreatedAt);

/* ── Monitoring ──────────────────────────────────────────────────────────── */

public sealed record StatsDto(
    LicenseStatsDto Licenses,
    BuildStatsDto Builds,
    ActivationStatsDto Activations,
    LeaseStatsDto Leases,
    string Database);

public sealed record LicenseStatsDto(int Total, int Active, int Revoked, int Suspended);
public sealed record BuildStatsDto(int Total, int Signed, int Revoked);
public sealed record ActivationStatsDto(int Total, int Active, int Revoked);
public sealed record LeaseStatsDto(int Issued24h);
