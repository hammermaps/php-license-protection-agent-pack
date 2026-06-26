namespace MmProtect.LicenseServer.Security;

public sealed class ApiKeyValidator
{
    private readonly HashSet<string> _encoderKeys;
    private readonly HashSet<string> _adminKeys;

    public ApiKeyValidator(IConfiguration configuration)
    {
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

    public bool IsValid(string? authorizationHeader)
        => Validate(authorizationHeader, _encoderKeys);

    public bool IsAdminValid(string? authorizationHeader)
        => Validate(authorizationHeader, _adminKeys);

    private static bool Validate(string? header, HashSet<string> keys)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return keys.Contains(header[prefix.Length..].Trim());
    }
}

public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetRequiredService<ApiKeyValidator>();
        var auth = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!validator.IsValid(auth))
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
        if (!validator.IsAdminValid(auth))
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
