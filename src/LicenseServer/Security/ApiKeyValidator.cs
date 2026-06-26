using Dapper;
using MmProtect.LicenseServer.Data;
using System.Security.Cryptography;
using System.Text;

namespace MmProtect.LicenseServer.Security;

public sealed class ApiKeyValidator
{
    private readonly HashSet<string> _encoderKeys;
    private readonly HashSet<string> _adminKeys;
    private readonly IDbConnectionFactory _db;

    public ApiKeyValidator(IConfiguration configuration, IDbConnectionFactory db)
    {
        _db = db;

        _encoderKeys = configuration
            .GetSection("Security:EncoderApiKeys")
            .Get<string[]>()?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        _adminKeys = configuration
            .GetSection("Security:AdminApiKeys")
            .Get<string[]>()?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
    }

    /* Sync fast-path — config keys only, no DB roundtrip */
    public bool IsValid(string? authorizationHeader)
        => Validate(authorizationHeader, _encoderKeys);

    public bool IsAdminValid(string? authorizationHeader)
        => Validate(authorizationHeader, _adminKeys);

    /* Async path — config keys first, then DB api_clients table */
    public async ValueTask<bool> IsValidAsync(string? header)
    {
        if (!TryExtractToken(header, out var token)) return false;
        if (_encoderKeys.Contains(token)) return true;
        return await CheckDbKeyAsync(token, "encoder");
    }

    public async ValueTask<bool> IsAdminValidAsync(string? header)
    {
        if (!TryExtractToken(header, out var token)) return false;
        if (_adminKeys.Contains(token)) return true;
        return await CheckDbKeyAsync(token, "admin");
    }

    private async Task<bool> CheckDbKeyAsync(string token, string scope)
    {
        var hash = ComputeKeyHash(token);
        try
        {
            await using var conn = await _db.OpenAsync();
            var count = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM api_clients
                WHERE api_key_hash = @Hash
                  AND is_active = 1
                  AND revoked_at IS NULL
                  AND (scopes = @Scope OR scopes = 'all')
                """, new { Hash = hash, Scope = scope });
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    /* Shared helper: SHA-256(utf8(key)) as lowercase hex */
    public static string ComputeKeyHash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static bool Validate(string? header, HashSet<string> keys)
    {
        if (!TryExtractToken(header, out var token)) return false;
        return keys.Contains(token);
    }

    internal static bool TryExtractToken(string? header, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(header)) return false;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = header[prefix.Length..].Trim();
        return !string.IsNullOrEmpty(token);
    }
}

public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetRequiredService<ApiKeyValidator>();
        var auth = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!await validator.IsValidAsync(auth))
            return Results.Json(ErrorDto.Create("AUTH_INVALID", "Invalid or missing API key."), statusCode: 401);
        return await next(context);
    }
}

public sealed class AdminApiKeyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetRequiredService<ApiKeyValidator>();
        var auth = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!await validator.IsAdminValidAsync(auth))
            return Results.Json(ErrorDto.Create("AUTH_INVALID", "Invalid or missing admin API key."), statusCode: 401);
        return await next(context);
    }
}

public sealed record ErrorDto(ErrorBody Error)
{
    public static ErrorDto Create(string code, string message)
        => new(new ErrorBody(code, message, Guid.NewGuid().ToString("N")));
}

public sealed record ErrorBody(string Code, string Message, string TraceId);
