using System.Text;
using Mockifyr.Core;
using Microsoft.AspNetCore.Http;
using Mockifyr.Server;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the audit trail (#247): the bound and its eviction order, newest-first
/// reads, tenant isolation in the store itself (not merely in the endpoint above it), and principal
/// labelling — including the two cases that must never be confused, a near-miss credential and a
/// credential on a host that has none.
/// </summary>
public sealed class AdminAuditUnitTests
{
    private static AuditEntry Entry(string action, string tenant = "default", string principal = "system") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, principal, new TenantId(tenant), action, null, 200);

    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void Reads_return_newest_first()
    {
        var log = new InMemoryAuditLog();
        log.Append(Entry("first"));
        log.Append(Entry("second"));
        log.Append(Entry("third"));

        var entries = log.Read(TenantId.Default, 10);

        Assert.Equal(["third", "second", "first"], entries.Select(e => e.Action));
    }

    [Fact]
    public void The_bound_evicts_the_oldest_entry()
    {
        var log = new InMemoryAuditLog(limit: 2);
        log.Append(Entry("first"));
        log.Append(Entry("second"));
        log.Append(Entry("third"));

        var entries = log.Read(TenantId.Default, 10);

        Assert.Equal(["third", "second"], entries.Select(e => e.Action));
    }

    [Fact]
    public void A_non_positive_limit_means_unbounded()
    {
        var log = new InMemoryAuditLog(limit: 0);
        for (var i = 0; i < 50; i++)
        {
            log.Append(Entry($"change-{i}"));
        }

        // The journal's convention (#220): an operator who asks for no bound gets none, and it is their
        // choice to make — not silently re-capped at a default.
        Assert.Equal(50, log.Read(TenantId.Default, 1000).Count);
    }

    [Fact]
    public void A_read_limit_caps_the_result_without_dropping_stored_entries()
    {
        var log = new InMemoryAuditLog();
        for (var i = 0; i < 10; i++)
        {
            log.Append(Entry($"change-{i}"));
        }

        Assert.Equal(3, log.Read(TenantId.Default, 3).Count);
        Assert.Equal(10, log.Read(TenantId.Default, 100).Count);
    }

    [Fact]
    public void The_bound_is_per_tenant_and_tenants_never_see_each_other()
    {
        var log = new InMemoryAuditLog(limit: 2);
        log.Append(Entry("alpha-1", "alpha"));
        log.Append(Entry("alpha-2", "alpha"));
        log.Append(Entry("alpha-3", "alpha"));
        log.Append(Entry("beta-1", "beta"));

        // One busy tenant must not evict another's history, and neither can read the other's.
        Assert.Equal(["alpha-3", "alpha-2"], log.Read(new TenantId("alpha"), 10).Select(e => e.Action));
        Assert.Equal(["beta-1"], log.Read(new TenantId("beta"), 10).Select(e => e.Action));
        Assert.Empty(log.Read(new TenantId("gamma"), 10));
    }

    [Fact]
    public void Concurrent_appends_are_all_recorded()
    {
        var log = new InMemoryAuditLog(limit: 0);

        Parallel.For(0, 200, i => log.Append(Entry($"change-{i}")));

        // Admin mutations arrive concurrently in any real host; a lost entry is a hole in the record.
        Assert.Equal(200, log.Read(TenantId.Default, 1000).Count);
    }

    [Fact]
    public void The_null_log_records_nothing_and_reads_empty()
    {
        var log = new NullAuditLog();
        log.Append(Entry("ignored"));

        Assert.Empty(log.Read(TenantId.Default, 10));
    }

    [Fact]
    public void The_system_credential_is_labelled_system()
    {
        var resolver = new AuditPrincipalResolver(Basic("op", "secret"), TenantCredentials.Parse([]));

        Assert.Equal("system", resolver.Resolve(Basic("op", "secret")));
    }

    [Fact]
    public void A_tenant_credential_is_labelled_with_its_tenant()
    {
        var resolver = new AuditPrincipalResolver(
            Basic("op", "secret"),
            TenantCredentials.Parse(["--tenant-credential", "acme:acme-user:acme-pass"]));

        Assert.Equal("tenant:acme", resolver.Resolve(Basic("acme-user", "acme-pass")));
    }

    [Fact]
    public void A_near_miss_credential_is_anonymous_not_system()
    {
        var resolver = new AuditPrincipalResolver(Basic("op", "secret"), TenantCredentials.Parse([]));

        // The label is an authorization claim in the record. Attributing a wrong password to "system"
        // would make the trail lie about who acted.
        Assert.Equal("anonymous", resolver.Resolve(Basic("op", "secre")));
        Assert.Equal("anonymous", resolver.Resolve(Basic("op", "secrett")));
        Assert.Equal("anonymous", resolver.Resolve(Basic("Op", "secret")));
        Assert.Equal("anonymous", resolver.Resolve(string.Empty));
    }

    [Fact]
    public void On_an_open_host_every_principal_is_anonymous()
    {
        var resolver = new AuditPrincipalResolver(null, TenantCredentials.Parse([]));

        // No configured credential must never mean "anything matches system" — an empty expected value
        // compared loosely would do exactly that.
        Assert.Equal("anonymous", resolver.Resolve(string.Empty));
        Assert.Equal("anonymous", resolver.Resolve(Basic("op", "secret")));
    }

    [Theory]
    [InlineData("POST", "/__admin/mappings", true)]
    [InlineData("PUT", "/__admin/mappings/abc", true)]
    [InlineData("DELETE", "/__admin/environments/key", true)]
    [InlineData("PATCH", "/__admin/resources/orders/1", true)]
    [InlineData("GET", "/__admin/mappings", false)]
    [InlineData("HEAD", "/__admin/mappings", false)]
    [InlineData("OPTIONS", "/__admin/mappings", false)]
    [InlineData("POST", "/__admin/audit", false)]
    [InlineData("POST", "/some/mocked/path", false)]
    [InlineData("DELETE", "/", false)]
    public void Only_admin_changes_are_auditable(string method, string path, bool expected) =>
        Assert.Equal(expected, AuditAction.IsAuditable(method, new PathString(path)));

    [Theory]
    [InlineData("/__admin/mappings", null)]
    [InlineData("/__admin/mappings/", null)]
    [InlineData("/__admin/mappings/abc-123", "abc-123")]
    [InlineData("/__admin/environments/apiHost/active", "active")]
    [InlineData("/__admin/resources/orders/42", "42")]
    [InlineData("/", null)]
    public void The_target_is_the_addressed_id_or_nothing(string path, string? expected) =>
        Assert.Equal(expected, AuditAction.TargetOf(new PathString(path)));

    [Fact]
    public void An_empty_path_has_no_target_and_is_not_auditable()
    {
        // A default PathString carries a null Value. No real request produces one, but auditing must
        // never be able to throw inside the operation it is describing.
        Assert.Null(AuditAction.TargetOf(default));
        Assert.False(AuditAction.IsAuditable("POST", default));
    }
}
