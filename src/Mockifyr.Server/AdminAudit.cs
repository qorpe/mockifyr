using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// Records administrative changes (#247): who changed what, in which tenant, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// One middleware rather than instrumentation on each of the 33 mutating admin routes. That is the
/// point: a route added tomorrow is audited without anyone remembering to, and there is a single
/// definition of "a change" instead of 33 that can drift apart. The trade-off is that entries describe
/// the operation (<c>POST /__admin/mappings</c>) rather than a domain event — which is what a reviewer
/// asks for anyway, and it never claims success for a change the handler rejected.
/// </para>
/// <para>
/// Unauthenticated attempts (401) are skipped: they are not administrative changes, they have no
/// principal to name, and auditing them would hand anyone a lever to evict the bounded trail by
/// repetition. They surface in metrics and access logs instead.
/// </para>
/// </remarks>
public static class AdminAuditMiddleware
{
    /// <summary>Runs the operation, then records it if it was an authenticated admin change.</summary>
    public static async Task InvokeAsync(
        HttpContext context,
        RequestDelegate next,
        IAuditLog log,
        AuditPrincipalResolver principals,
        ILogger logger,
        string tenantHeader)
    {
        if (!AuditAction.IsAuditable(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        // Resolved before the handler runs: a handler is free to rewrite the request, and the tenant
        // header is what the caller actually addressed.
        var principal = principals.Resolve(context.Request.Headers.Authorization.ToString());
        var tenant = context.Request.Headers.TryGetValue(tenantHeader, out var header) && !string.IsNullOrEmpty(header)
            ? new TenantId(header.ToString())
            : TenantId.Default;
        var action = $"{context.Request.Method} {context.Request.Path}";
        var target = AuditAction.TargetOf(context.Request.Path);

        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return;
        }

        var entry = new AuditEntry(
            Guid.NewGuid(), DateTimeOffset.UtcNow, principal, tenant, action, target, context.Response.StatusCode);
        log.Append(entry);

        // Also a log line, because the in-memory trail dies with the pod: with --log-json (#246) this is
        // the structured record a SIEM keeps for as long as its own retention says.
        logger.LogInformation(
            "admin.audit principal={Principal} tenant={Tenant} action={Action} target={Target} outcome={Outcome}",
            entry.Principal, entry.Tenant.Value, entry.Action, entry.Target ?? "-", entry.Outcome);
    }
}
