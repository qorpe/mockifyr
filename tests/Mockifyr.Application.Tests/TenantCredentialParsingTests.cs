using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for per-tenant admin credential parsing (#224): the argv reading that .NET
/// configuration cannot do (repeated keys), and the constant-time principal lookup.
/// </summary>
public sealed class TenantCredentialParsingTests
{
    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void Every_repeated_flag_is_kept()
    {
        // The reason this reads argv: configuration keeps only the LAST value of a repeated key,
        // which would silently drop every tenant but one.
        var credentials = TenantCredentials.Parse(
            ["--port", "0", "--tenant-credential", "acme:u1:p1", "--tenant-credential", "globex:u2:p2"]);

        Assert.Equal(2, credentials.Count);
        Assert.Equal("acme", credentials.TenantFor(Basic("u1", "p1")));
        Assert.Equal("globex", credentials.TenantFor(Basic("u2", "p2")));
    }

    [Fact]
    public void A_password_may_contain_colons()
    {
        var credentials = TenantCredentials.Parse(["--tenant-credential", "acme:user:pa:ss:word"]);

        Assert.Equal("acme", credentials.TenantFor(Basic("user", "pa:ss:word")));
    }

    [Fact]
    public void Malformed_and_empty_entries_are_ignored()
    {
        var credentials = TenantCredentials.Parse([
            "--tenant-credential", "missing-parts",
            "--tenant-credential", "acme:onlyuser",
            "--tenant-credential", ":empty:tenant",
            "--tenant-credential", "acme:user:",
            "--tenant-credential"]);

        Assert.True(credentials.IsEmpty);
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public void An_unknown_or_absent_header_maps_to_no_principal()
    {
        var credentials = TenantCredentials.Parse(["--tenant-credential", "acme:user:pass"]);

        Assert.Null(credentials.TenantFor(Basic("user", "wrong")));
        Assert.Null(credentials.TenantFor(Basic("other", "pass")));
        Assert.Null(credentials.TenantFor(null));
        Assert.Null(credentials.TenantFor(string.Empty));
        // The system credential is deliberately unknown here — it falls through to the global check.
        Assert.Null(credentials.TenantFor(Basic("system", "root")));
    }

    [Fact]
    public void Only_the_flags_own_values_are_read()
    {
        // Another flag's value may itself look like tenant:user:pass — a connection string, for
        // instance. Parsing must key off the flag, never off the shape of a neighbouring argument,
        // or an unrelated option would silently mint an admin principal.
        var credentials = TenantCredentials.Parse([
            "--redis", "redis://user:pass@localhost:6379",
            "--postgres", "Host=db;Port=5432;Password=a:b:c"]);

        Assert.True(credentials.IsEmpty);
        Assert.Null(credentials.TenantFor(Basic("user", "pass@localhost:6379")));
    }

    [Fact]
    public void No_flag_means_no_behavior_change()
    {
        var credentials = TenantCredentials.Parse(["--port", "0", "--admin-user", "op"]);

        Assert.True(credentials.IsEmpty);
        Assert.Null(credentials.TenantFor(Basic("op", "whatever")));
    }
}
