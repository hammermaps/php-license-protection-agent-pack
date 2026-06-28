using System.Net.Http.Json;
using System.Text.Json;

namespace MmProtect.EncoderCli.Server;

public sealed class LicenseServerClient
{
    private readonly HttpClient _http;

    public LicenseServerClient(HttpClient http, string apiKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<CustomerUpsertResponse> UpsertCustomerAsync(object request)
        => await PostAsync<CustomerUpsertResponse>("api/v1/encoder/customers/upsert", request);

    public async Task<ProjectUpsertResponse> UpsertProjectAsync(object request)
        => await PostAsync<ProjectUpsertResponse>("api/v1/encoder/projects/upsert", request);

    public async Task<LicenseUpsertResponse> UpsertLicenseAsync(object request)
        => await PostAsync<LicenseUpsertResponse>("api/v1/encoder/licenses/upsert", request);

    public async Task<BuildStartResponse> StartBuildAsync(object request)
        => await PostAsync<BuildStartResponse>("api/v1/encoder/builds/start", request);

    public async Task RegisterFilesAsync(string buildId, object request)
        => await PostAsync<JsonElement>($"api/v1/encoder/builds/{Uri.EscapeDataString(buildId)}/files", request);

    public async Task<ManifestSignResponse> SignManifestAsync(string buildId, object request)
        => await PostAsync<ManifestSignResponse>($"api/v1/encoder/builds/{Uri.EscapeDataString(buildId)}/manifest/sign", request);

    /// <summary>
    /// Fire-and-forget telemetry event. Never throws — telemetry failure must not break the build.
    /// </summary>
    public async Task SendTelemetryAsync(string eventType, string? buildId, string? projectId,
        string? licenseId, Dictionary<string, string>? data = null, string? endpointUrl = null)
    {
        try
        {
            var url = string.IsNullOrEmpty(endpointUrl) ? "api/v1/encoder/telemetry" : endpointUrl;
            var payload = new
            {
                source     = "encoder",
                eventType,
                licenseId,
                buildId,
                projectId,
                occurredAt = DateTimeOffset.UtcNow,
                data
            };
            using var response = await _http.PostAsJsonAsync(url, payload);
            // ignore status — telemetry is best-effort
        }
        catch { /* telemetry errors must never propagate */ }
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        using var response = await _http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Serverfehler {response.StatusCode}: {text}");

        var result = JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result ?? throw new InvalidOperationException($"Leere Serverantwort für {url}");
    }
}

public sealed record CustomerUpsertResponse(string CustomerId, bool Created);
public sealed record ProjectUpsertResponse(string ProjectId, bool Created);
public sealed record LicenseUpsertResponse(string LicenseId, bool Created);
public sealed record BuildStartResponse(string BuildId, string KeyId, string BuildKey, string ManifestSalt);
public sealed record ManifestSignResponse(string ManifestSignature, string VendorPublicKeyId, DateTimeOffset ServerTimeUtc);
