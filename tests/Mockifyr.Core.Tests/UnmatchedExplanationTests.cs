using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>The pure halves of #457: which tenants are explained, and the consumer's shape of a near miss.</summary>
public sealed class UnmatchedExplanationTests
{
    [Fact]
    public void Nothing_named_explains_nobody()
    {
        var none = UnmatchedExplanation.From(null);
        Assert.False(none.IsConfigured);
        Assert.False(none.For(TenantId.Default));
        Assert.False(UnmatchedExplanation.From(["", "   "]).IsConfigured);
    }

    [Fact]
    public void A_named_tenant_is_explained_and_no_other_is()
    {
        var explanation = UnmatchedExplanation.From([" acme ", "globex", "acme"]);

        Assert.True(explanation.IsConfigured);
        Assert.True(explanation.For(new TenantId("acme")));
        Assert.True(explanation.For(new TenantId("globex")));
        Assert.False(explanation.For(new TenantId("initech")));
        Assert.False(explanation.For(TenantId.Default));
        Assert.Equal(["acme", "globex"], explanation.Tenants);
    }

    [Fact]
    public void Tenant_names_compare_ordinally_as_tenant_ids_do()
    {
        // A tenant id is case-sensitive everywhere else, so "Acme" naming "acme" would explain a tenant the
        // operator did not name — and under tr-TR, "ACME" vs "acme" must not depend on the culture either.
        var explanation = UnmatchedExplanation.From(["Acme", "İzmir"]);

        Assert.False(explanation.For(new TenantId("acme")));
        Assert.False(explanation.For(new TenantId("izmir")));
        Assert.True(explanation.For(new TenantId("İzmir")));
    }

    [Fact]
    public void The_consumers_near_miss_keeps_distance_and_attributes_and_drops_the_stub()
    {
        StubMapping stub = null!;   // the projection must not read the stub at all
        MatchAttribute[] attributes = [new("method", true, "POST"), new("headers['X-Token']", false, "wrong")];

        var projected = ConsumerNearMiss.From([new NearMiss(stub, 0.25) { Attributes = attributes }, new NearMiss(stub, 0.5)]);

        Assert.Equal(2, projected.Count);
        Assert.Equal(0.25, projected[0].Distance);
        Assert.Equal(attributes, projected[0].Attributes);
        Assert.Empty(projected[1].Attributes);
        // The type itself has nowhere to put a stub: nothing to leak by accident in a later edit.
        Assert.Equal(["Distance", "Attributes"], typeof(ConsumerNearMiss).GetProperties().Select(p => p.Name));
    }
}
