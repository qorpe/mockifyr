namespace Mockifyr.Core;

/// <summary>
/// Tenant-scoped store of stub definitions. This is the persistence seam: the default
/// implementation is in-memory; durable providers slot in behind this contract without the
/// engine changing. There is deliberately no tenant-less overload — forgetting the scope must
/// be a compile error. See docs/decisions/0003 and 0006.
/// </summary>
public interface IStubStore
{
    /// <summary>Returns the stubs owned by <paramref name="tenant"/>.</summary>
    IReadOnlyList<StubMapping> GetStubs(TenantId tenant);

    /// <summary>
    /// Returns every tenant that currently owns at least one stub. Used by change-feed reload (G16g) to
    /// reconcile all tenants — including pruning a tenant whose last stub was deleted elsewhere.
    /// </summary>
    IReadOnlyCollection<TenantId> GetTenants();

    /// <summary>Adds or replaces a stub.</summary>
    void Put(StubMapping stub);

    /// <summary>Removes a stub from a tenant.</summary>
    void Remove(TenantId tenant, Guid id);

    /// <summary>
    /// The stubs that could match <paramref name="request"/>, in store order (#265). A store may
    /// narrow the field with an index; the caller still evaluates every candidate, so narrowing is an
    /// optimization and never a decision.
    /// </summary>
    /// <remarks>
    /// Defaulted to "everything", so a store that has no index — or a custom one written before this
    /// existed — keeps working unchanged and simply gains nothing. A candidate set must always be a
    /// superset of the stubs that can match: dropping one would silently change behaviour, which is
    /// why the default is the safe answer rather than an error.
    /// </remarks>
    IReadOnlyList<StubMapping> GetCandidates(TenantId tenant, CanonicalRequest request) => GetStubs(tenant);
}

/// <summary>
/// An atomic, composable matcher. Logical operators (and/or/not) implement this interface too,
/// so composition comes for free. This is also the custom-matcher extension seam (G10).
/// </summary>
public interface IMatcher
{
    /// <summary>Evaluates the input and returns an exact match or a distance.</summary>
    MatchResult Match(MatchInput input);
}

/// <summary>Renders a <see cref="ResponseDefinition"/> into a concrete response (templating).</summary>
public interface IResponseRenderer
{
    /// <summary>Renders the definition against the given context.</summary>
    CanonicalResponse Render(ResponseDefinition definition, RenderContext context);
}

/// <summary>
/// Tenant-scoped scenario state store. Read before matching (eligibility) and written after
/// matching (transition).
/// </summary>
public interface IScenarioStateStore
{
    /// <summary>Gets the current state of a scenario (default <c>Started</c>).</summary>
    string GetState(TenantId tenant, string scenario);

    /// <summary>Sets the state of a scenario.</summary>
    void SetState(TenantId tenant, string scenario, string state);
}

/// <summary>
/// Tenant-scoped store of environment keys (G17). An environment key carries several named values and
/// one of them is active, so <c>{{key}}</c> in a stub resolves to whichever value is active *at the
/// moment the stub is served* — never baked in when the stub is saved. There is deliberately no
/// tenant-less overload: one tenant must not see, resolve, or edit another's keys (see docs/decisions
/// /0003 and issue #166).
/// </summary>
public interface IEnvironmentStore
{
    /// <summary>Returns the environment keys owned by <paramref name="tenant"/>.</summary>
    IReadOnlyList<EnvironmentKey> GetKeys(TenantId tenant);

    /// <summary>
    /// Returns every tenant that currently owns at least one key. Used by change-feed reload (G16g),
    /// including pruning a tenant whose last key was deleted elsewhere.
    /// </summary>
    IReadOnlyCollection<TenantId> GetTenants();

    /// <summary>Adds or replaces a key.</summary>
    void Put(TenantId tenant, EnvironmentKey key);

    /// <summary>Removes a key from a tenant. Returns false when the tenant does not own it.</summary>
    bool Remove(TenantId tenant, string key);

    /// <summary>Removes every key owned by a tenant.</summary>
    void Clear(TenantId tenant);
}

/// <summary>
/// The serve-path view of <see cref="IEnvironmentStore"/>: the narrow, allocation-free lookup the
/// renderer needs on every request. Kept separate from the store so the hot path cannot accidentally
/// enumerate or mutate, and so a facade with no environments configured can supply a null resolver.
/// </summary>
public interface IEnvironmentResolver
{
    /// <summary>True when <paramref name="tenant"/> has at least one key — the fast-path guard.</summary>
    bool HasKeys(TenantId tenant);

    /// <summary>Resolves a key to its currently active value.</summary>
    bool TryResolve(TenantId tenant, string key, out string value);
}

/// <summary>Tenant-scoped request journal, used by verification and near-miss diagnostics.</summary>
public interface IRequestJournal
{
    /// <summary>Records a served event.</summary>
    void Record(ServeEvent serveEvent);

    /// <summary>Queries recorded events for a tenant.</summary>
    IReadOnlyList<ServeEvent> Query(TenantId tenant, ServeEventQuery query);

    /// <summary>
    /// Discards everything recorded for a tenant. What a suite calls between tests so counts assert
    /// about the test that is running, and never about the one before it.
    /// </summary>
    void Clear(TenantId tenant);
}

/// <summary>
/// A serve-event listener, fired asynchronously after a request is served. Webhooks/callbacks
/// are an implementation of this seam; their outbound I/O lives outside the pure engine core.
/// </summary>
public interface IServeEventListener
{
    /// <summary>Called after a request is served.</summary>
    Task OnServeEventAsync(ServeEvent serveEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Renders a template string against a serve event's original (triggering) request — the model
/// exposed to webhooks as <c>originalRequest</c> (G3b, verified by the differential suite). The
/// implementation lives in the templating edge; the webhook listener depends only on this
/// contract, keeping outbound I/O and templating decoupled.
/// </summary>
public interface IServeEventTemplateRenderer
{
    /// <summary>
    /// Renders <paramref name="template"/> with the original request exposed as <c>originalRequest</c>.
    /// The tenant is carried because environment keys (G17) resolve per tenant, and a webhook URL or
    /// body is exactly the kind of field that references one.
    /// </summary>
    string Render(string template, CanonicalRequest originalRequest, TenantId tenant);
}

/// <summary>
/// Signals that the underlying store changed out-of-band (e.g. an edited file or a DB row) so
/// the engine can reload its in-memory compiled index. This — not reading the DB on the hot
/// path — is how "edit directly and have it reflected" is served. See docs/decisions/0006.
/// </summary>
public interface IStubChangeSource
{
    /// <summary>Raised when the external source has changed and a reload is warranted.</summary>
    event EventHandler<TenantId> Changed;
}
