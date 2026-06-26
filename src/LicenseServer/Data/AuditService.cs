using Dapper;
using System.Text.Json;

namespace MmProtect.LicenseServer.Data;

public sealed class AuditService(IDbConnectionFactory dbFactory)
{
    /*
     * Write an entry to audit_log.
     * This method never throws — audit logging must not interrupt the main request flow.
     */
    public async Task LogAsync(
        string  actorType,
        string  eventType,
        string? entityType = null,
        string? entityUid  = null,
        string? ip         = null,
        object? details    = null)
    {
        try
        {
            await using var conn = await dbFactory.OpenAsync();
            var detailsJson = details is null
                ? null
                : JsonSerializer.Serialize(details,
                      new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await conn.ExecuteAsync("""
                INSERT INTO audit_log
                    (event_uid, actor_type, event_type, entity_type, entity_uid, ip_address, details)
                VALUES
                    (@EventUid, @ActorType, @EventType, @EntityType, @EntityUid, @IpAddress, @Details)
                """, new
            {
                EventUid   = "evt_" + Guid.NewGuid().ToString("N"),
                ActorType  = actorType,
                EventType  = eventType,
                EntityType = entityType,
                EntityUid  = entityUid,
                IpAddress  = ip,
                Details    = detailsJson
            });
        }
        catch
        {
            /* swallow — audit failures must never crash a request */
        }
    }
}
