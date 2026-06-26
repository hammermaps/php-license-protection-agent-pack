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
    string[]? Features);

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
    string Nonce);

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
    string Signature);

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
