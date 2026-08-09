// Generates demo/Mockifyr-Demo.pptx — dark technical demo deck (15 slides).
const pptxgen = require('pptxgenjs');

const BG = '0B0E14', PANEL = '131824', BORDER = '232B3D', INK = 'E8ECF3',
  DIM = '8B94A7', ACC = '4F8CFF', GRN = '34D399', WRN = 'F59E0B', CODE = 'BCD3FF';
const BODY = 'Calibri', MONO = 'Courier New';
const W = 13.33, H = 7.5, MX = 0.9;

const p = new pptxgen();
p.layout = 'LAYOUT_WIDE';
p.defineSlideMaster({ title: 'DARK', background: { color: BG } });

const slide = () => p.addSlide({ masterName: 'DARK' });

function kicker(s, text, y = 0.75) {
  s.addText(text.toUpperCase(), { x: MX, y, w: W - 2 * MX, h: 0.35, fontFace: BODY,
    fontSize: 12, bold: true, color: ACC, charSpacing: 3, margin: 0 });
}
function title(s, text, y = 1.15, size = 32) {
  s.addText(text, { x: MX, y, w: W - 2 * MX, h: 0.9, fontFace: BODY, fontSize: size,
    bold: true, color: 'FFFFFF', margin: 0 });
}
function bullets(s, items, opt = {}) {
  const runs = items.map((t, i) => (typeof t === 'string'
    ? { text: t, options: { bullet: { code: '2014', indent: 18 }, color: INK, breakLine: i < items.length - 1, paraSpaceAfter: 10 } }
    : { text: t.text, options: { bullet: { code: '2014', indent: 18 }, color: INK, breakLine: i < items.length - 1, paraSpaceAfter: 10, ...t.o } }));
  s.addText(runs, { x: opt.x ?? MX, y: opt.y ?? 2.2, w: opt.w ?? (W - 2 * MX), h: opt.h ?? 4.3,
    fontFace: BODY, fontSize: opt.size ?? 15, valign: 'top', margin: 0, lineSpacingMultiple: 1.15 });
}
function card(s, x, y, w, h, head, body, headColor = 'FFFFFF') {
  s.addShape('roundRect', { x, y, w, h, rectRadius: 0.09, fill: { color: PANEL },
    line: { color: BORDER, width: 1 } });
  s.addText(head, { x: x + 0.25, y: y + 0.18, w: w - 0.5, h: 0.4, fontFace: BODY,
    fontSize: 15, bold: true, color: headColor, margin: 0 });
  s.addText(body, { x: x + 0.25, y: y + 0.62, w: w - 0.5, h: h - 0.8, fontFace: BODY,
    fontSize: 12, color: DIM, valign: 'top', margin: 0, lineSpacingMultiple: 1.12 });
}
function statCard(s, x, y, w, h, stat, head, body) {
  s.addShape('roundRect', { x, y, w, h, rectRadius: 0.09, fill: { color: PANEL },
    line: { color: BORDER, width: 1 } });
  s.addText(head, { x: x + 0.25, y: y + 0.18, w: w - 0.5, h: 0.35, fontFace: BODY,
    fontSize: 14, bold: true, color: 'FFFFFF', margin: 0 });
  s.addText(stat, { x: x + 0.25, y: y + 0.55, w: w - 0.5, h: 0.7, fontFace: MONO,
    fontSize: 30, bold: true, color: GRN, margin: 0 });
  s.addText(body, { x: x + 0.25, y: y + 1.3, w: w - 0.5, h: h - 1.5, fontFace: BODY,
    fontSize: 11.5, color: DIM, valign: 'top', margin: 0, lineSpacingMultiple: 1.12 });
}
function chips(s, items, y, x = MX) {
  let cx = x;
  items.forEach(t => {
    const w = 0.32 + t.length * 0.085;
    s.addShape('roundRect', { x: cx, y, w, h: 0.38, rectRadius: 0.19,
      fill: { color: BG }, line: { color: '2C3650', width: 1 } });
    s.addText(t, { x: cx, y, w, h: 0.38, align: 'center', fontFace: MONO,
      fontSize: 10.5, color: DIM, margin: 0 });
    cx += w + 0.18;
  });
}
function codeCard(s, x, y, w, h, runs) {
  s.addShape('roundRect', { x, y, w, h, rectRadius: 0.09, fill: { color: '0E1420' },
    line: { color: BORDER, width: 1 } });
  s.addText(runs, { x: x + 0.28, y: y + 0.22, w: w - 0.56, h: h - 0.44, fontFace: MONO,
    fontSize: 11, color: CODE, valign: 'top', margin: 0, lineSpacingMultiple: 1.25 });
}

// ---- 1 · Title
let s = slide();
kicker(s, 'Live demo · v1.9', 2.15);
s.addText('Mockifyr', { x: MX, y: 2.45, w: W - 2 * MX, h: 1.1, fontFace: BODY,
  fontSize: 54, bold: true, color: 'FFFFFF', margin: 0 });
s.addText([
  { text: 'An enterprise ' },
  { text: 'API mock & integration sandbox', options: { bold: true, color: 'FFFFFF' } },
  { text: ' platform. One engine — REST, callbacks, e-mail, SMS, gRPC, GraphQL, WebSocket, events — multi-tenant by construction, and honest about whether your mock still tells the truth.' },
], { x: MX, y: 3.7, w: 9.6, h: 1.4, fontFace: BODY, fontSize: 16, color: DIM, margin: 0, lineSpacingMultiple: 1.2 });
chips(s, ['.NET 10', 'zero-dependency core', 'multi-tenant', 'Apache-2.0'], 5.3);
s.addNotes('Welcome. Today: one story, Acme Payments, end to end. Everything you will see is live on my machine, one process.');

// ---- 2 · Problem
s = slide();
kicker(s, 'The problem');
title(s, 'Integration environments are where sprints go to die');
card(s, MX, 2.4, 3.7, 2.6, 'The dependency', 'The real system is shared, rate-limited, flaky — or does not exist yet. Every team queues for the same broken staging.');
card(s, MX + 3.9, 2.4, 3.7, 2.6, 'The hand-rolled mock', 'Answers only the happy path. No callbacks, no messages, no failures, no state. The first real integration test still happens in production.');
card(s, MX + 7.8, 2.4, 3.7, 2.6, 'The silent drift', 'Worst of all: a mock that drifted from the real API manufactures confidence. Green builds, broken production.', WRN);
s.addText('A mock is infrastructure. It should be provisioned, scoped, observed — and continuously checked against the contract and against reality.',
  { x: MX, y: 5.4, w: 10.5, h: 0.8, fontFace: BODY, fontSize: 14, italic: true, color: DIM, margin: 0 });
s.addNotes('Frame the pain: shared environments and happy-path mocks. Land the drift point hard — it sets up the conformance finale.');

// ---- 3 · Architecture
s = slide();
kicker(s, 'What it is');
title(s, 'A pure engine, thin edges, tenants everywhere');
bullets(s, [
  { text: 'Core engine: matching + templating — zero external dependencies, no I/O.', o: {} },
  'Facades at the edge: HTTP, Admin REST, gRPC, WebSocket, SMTP, SMS provider, broker. Transport never leaks inward.',
  'Multi-tenancy is first-class: every store and engine entry point takes a tenant — forgetting scope is a compile error, not an incident.',
  'Persistence seam: in-memory, file, LiteDB, Postgres, Redis — same behavior on all.',
], { w: 6.4, size: 14 });
codeCard(s, 7.6, 2.35, 4.85, 2.6, [
  { text: 'tenant', options: { color: GRN } }, { text: ' ─▶ facade (wire)\n' },
  { text: '        ─▶ ' }, { text: 'engine (pure)', options: { color: 'FFFFFF', bold: true } }, { text: '\n' },
  { text: '        ─▶ stores (tenant-scoped)\n\n' },
  { text: 'post-serve: ', options: { color: DIM } },
  { text: 'webhook · publish', options: { color: WRN } },
]);
s.addNotes('30 seconds max. The point the audience must keep: the engine is pure and tenant scope is a type, so isolation is not a convention.');

// ---- 4 · Demo map
s = slide();
kicker(s, 'Demo map');
title(s, 'One story: Acme Payments');
const steps = [
  ['1 · Spec → sandbox', 'OpenAPI import, live CRUD, API keys & quotas'],
  ['2 · Matching', 'validation criteria + near-miss diagnostics'],
  ['3 · Callbacks', 'answer 202, fire the webhook'],
  ['4 · Messages', 'SMS · e-mail · OTP, one inbox'],
  ['5 · Beyond HTTP', 'gRPC · GraphQL · WebSocket'],
  ['6 · Scenarios', 'stateful flows'],
  ['7 · Record & drift', 'mock reality, catch it moving'],
  ['8 · Time · chaos · contract', 'clock, degradation, conformance'],
];
steps.forEach(([h, b], i) => {
  const col = i % 4, row = Math.floor(i / 4);
  card(s, MX + col * 2.95, 2.35 + row * 1.75, 2.75, 1.55, h, b);
});
s.addText([
  { text: 'Two tenants on one host: ' },
  { text: 'acme-pay', options: { fontFace: MONO, color: CODE } },
  { text: ' is the star; ' },
  { text: 'globex', options: { fontFace: MONO, color: CODE } },
  { text: ' proves isolation and plays the recording story.' },
], { x: MX, y: 6.1, w: 11.5, h: 0.5, fontFace: BODY, fontSize: 13, color: DIM, margin: 0 });
s.addNotes('Show the map once, then never come back to slides until Act 9 — the demo carries itself.');

// ---- 5 · Act 1
s = slide();
kicker(s, 'Act 1');
title(s, 'From API spec to shareable sandbox');
bullets(s, [
  'Paste the OpenAPI document → every operation becomes a stub; resource-shaped paths become live CRUD over a real document store.',
  'Seed realistic data; documents survive restarts on every persistence backend.',
  { text: 'Issue mfk_ API keys — one-time reveal, salted-hash storage, per-key hourly quotas.', o: {} },
  'The key IS the tenant. Invalid key → honest 401.',
], { w: 6.6, size: 14 });
codeCard(s, 7.7, 2.3, 4.7, 3.3, [
  { text: '$ call 5 — HTTP 200\n', options: { color: DIM } },
  { text: 'X-RateLimit-Limit: 5\nX-RateLimit-Remaining: 0\n\n' },
  { text: '$ call 6 — ', options: { color: DIM } },
  { text: 'HTTP 429\n', options: { color: WRN, bold: true } },
  { text: 'Retry-After: 1729\n' },
  { text: 'X-RateLimit-Reset: 1785967200' },
]);
s.addNotes('Dashboard: Spin up a sandbox → Add stub → OpenAPI → Import. Then payments-create/get/list, Resources page, Access page one-time reveal, key-quota.');

// ---- 6 · Act 2
s = slide();
kicker(s, 'Act 2');
title(s, "Matching criteria — and why didn't it match?");
bullets(s, [
  'URL & method, headers, query, cookies; body: equalToJson, matchesJsonPath, JSON Schema, XML/XPath, regex, logic combinators, priorities.',
  'Responses are templated from the request — path segments, headers, body fields.',
  'The 404 you serve is byte-stable; diagnosis lives on the admin surface.',
  'Near-miss speaks the mapping JSON’s own vocabulary — grep your stub file for it.',
], { w: 6.2, size: 14 });
codeCard(s, 7.3, 2.3, 5.1, 3.4, [
  { text: '"attributes": [\n' },
  { text: '  urlPath            ' }, { text: 'matched: true\n', options: { color: GRN } },
  { text: '  method             ' }, { text: 'matched: true\n', options: { color: GRN } },
  { text: "  headers['X-Partner-Key']\n                     " },
  { text: 'matched: false ', options: { color: WRN, bold: true } },
  { text: '"WRONG"\n', options: { color: WRN } },
  { text: '  bodyPatterns[0]    ' }, { text: 'matched: true', options: { color: GRN } },
  { text: ' ]' },
]);
s.addNotes('order-ok (templating), order-bad (404), near-miss. Point out: the attribute names are the mapping JSON vocabulary.');

// ---- 7 · Act 3
s = slide();
kicker(s, 'Act 3');
title(s, 'Callbacks: answer now, call back later');
bullets(s, [
  'The stub answers 202 immediately; the webhook fires after a configurable delay, templated from the original request.',
  'Delivery captured in the journal: URL, payload, response, delivered — or the error if the receiver was down.',
  'The same post-serve seam publishes to Kafka (publish action): the sandbox answers the request AND emits the event — validated against a real broker container.',
], { w: 6.6, size: 14 });
const fy = 2.5;
['POST /api/payments/authorize', '202 now', 'webhook +500 ms', 'journal: delivered ✓'].forEach((t, i) => {
  s.addShape('roundRect', { x: 7.7, y: fy + i * 0.82, w: 4.7, h: 0.62, rectRadius: 0.09,
    fill: { color: PANEL }, line: { color: BORDER, width: 1 } });
  s.addText(t, { x: 7.95, y: fy + i * 0.82, w: 4.2, h: 0.62, fontFace: MONO, fontSize: 12,
    color: i === 3 ? GRN : CODE, margin: 0, valign: 'middle' });
});
s.addNotes('demo webhook step. Show the journal detail Callback tab: delivered true, response 200 captured — and the callback request itself in the journal.');

// ---- 8 · Act 4
s = slide();
kicker(s, 'Act 4');
title(s, 'Messages: your app sends real mail & SMS — nobody receives it');
bullets(s, [
  'SMTP capture: a real ESMTP listener; the AUTH username names the tenant.',
  'Provider-shaped SMS: the official SDK works unchanged — realistic responses, realistic error codes, simulated provider failures.',
  'One inbox for e-mail, SMS — and broker messages — with a verify surface: GET /__admin/messages/otp returns the code your E2E test needs.',
  'Behaviors: SMTP faults & delay, provider errors, capture webhook, bounded inbox.',
], { w: 6.8, size: 14 });
codeCard(s, 7.9, 2.4, 4.5, 2.2, [
  { text: '$ demo otp\n', options: { color: DIM } },
  { text: '{\n  "otp": ' },
  { text: '"482913"', options: { color: GRN, bold: true } },
  { text: ',\n  "receivedAt": "…"\n}' },
]);
s.addNotes('sms → Messages page (OTP chip on the row) → otp endpoint → email via SMTP AUTH tenant. Verify-by-API replaces "check the phone".');

// ---- 9 · Act 5
s = slide();
kicker(s, 'Act 5');
title(s, 'Beyond HTTP: same engine, other wires');
card(s, MX, 2.4, 3.7, 3.1, 'gRPC',
  'Upload a descriptor set → serving hot-enables, no restart. Unary + single-message streaming, full codec (enums, maps, oneof, wrappers), real gRPC error statuses. Tenant rides call metadata.');
card(s, MX + 3.9, 2.4, 3.7, 3.1, 'GraphQL',
  'Match on query + variables + operationName. Queries are AST-normalized — whitespace, field order, argument order are irrelevant. Templated data responses.');
card(s, MX + 7.8, 2.4, 3.7, 3.1, 'WebSocket',
  'Message mappings: connect-time pushes, per-message triggers with the standard matcher set, templated replies, tenant-scoped broadcast, admin server-push.');
s.addText('One stub list, one journal — protocol chips tell the channels apart.',
  { x: MX, y: 5.85, w: 10, h: 0.5, fontFace: BODY, fontSize: 14, italic: true, color: DIM, margin: 0 });
s.addNotes('grpc-descriptor (hot enable), grpc, graphql, graphql-messy (the wow beat), ws. Then show the Stubs tree with all four protocol chips.');

// ---- 10 · Acts 6–7
s = slide();
kicker(s, 'Acts 6–7');
title(s, 'State you can steer · reality you can record');
bullets(s, [
  { text: 'Scenarios: stateful stub groups — pending → settled; inspect, set, and rewind states from the dashboard.', o: {} },
  'Recording: point at an upstream, drive traffic, snapshot → stubs. Repeated calls become a scenario chain that replays the sequence.',
], { w: 5.6, size: 14, h: 3.6 });
bullets(s, [
  'Drift check: with a session live, compare what the upstream just returned against what your stubs would answer.',
  { text: 'Structural, never literal — ids and timestamps don’t drown findings: fieldMissing /settlementBatch, fieldUnexpected /currency.', o: {} },
  'Serves nothing while it looks — same matcher, zero side effects.',
], { x: 7.0, w: 5.4, size: 14, h: 3.6 });
s.addNotes('Tenant switch to Globex Retail (empty list = isolation). Record against the 9090 upstream, snapshot, Import all, then drift + record-verify BEFORE stop.');

// ---- 11 · Act 8 time & chaos
s = slide();
kicker(s, 'Act 8');
title(s, 'Time, chaos — on your terms');
bullets(s, [
  'Tenant clock: freeze or shift the instant templates see — test the token that "expires in an hour" without waiting an hour.',
  'Journal, audit and inbox keep real time, by design.',
], { w: 5.6, size: 14, h: 3.4 });
bullets(s, [
  'Degradation profiles: latency + error ratio + faults composed over every stub of a tenant — what if the whole dependency degrades?',
  'Deterministic from a seed the host always reports: a chaos run becomes a regression test. The admin surface is never degraded.',
], { x: 7.0, w: 5.4, size: 14, h: 3.4 });
codeCard(s, MX, 5.0, 11.5, 1.5, [
  { text: 'PUT /__admin/clock {"frozenAt":"2027-01-01T09:00:00Z"}   ', options: {} },
  { text: '→ token now expires in 2027\n', options: { color: GRN } },
  { text: 'PUT /__admin/degradation {latency, errorRate, ', options: {} },
  { text: 'seed: 42', options: { color: WRN, bold: true } },
  { text: '}      → same chaos, every run', options: { color: GRN } },
]);
s.addNotes('token → clock-freeze → token → clock-reset. chaos-on → chaos-probe (mixed 503s, slower) → chaos-off.');

// ---- 12 · Conformance
s = slide();
kicker(s, 'Act 8 · conformance');
title(s, 'Three questions no mock usually answers');
card(s, MX, 2.4, 3.7, 2.9, 'Stubs vs contract',
  'openapi/verify — which operations are uncovered, which stubs are undeclared, which responses violate the schema.');
card(s, MX + 3.9, 2.4, 3.7, 2.9, 'Reality vs stubs',
  'recordings/verify — has the upstream drifted from what we mock, since we recorded it?');
card(s, MX + 7.8, 2.4, 3.7, 2.9, 'Traffic vs contract',
  'requests/verify — did the CONSUMER stay inside the contract? The failure a permissive mock hides completely.');
s.addText('One engine, one set of ambiguity rules — three reports that cannot disagree about which operation a path belongs to.',
  { x: MX, y: 5.65, w: 11, h: 0.6, fontFace: BODY, fontSize: 14, italic: true, color: DIM, margin: 0 });
s.addNotes('verify-stubs (5/5 covered, 8 undeclared on purpose) and verify-traffic. Tie back to the drift slide: this is how a mock stays honest.');

// ---- 13 · Trust
s = slide();
kicker(s, 'Why trust it');
title(s, 'Maturity is measured, not claimed');
statCard(s, MX, 2.35, 3.7, 3.0, '1122', 'Proven differentially',
  'tests green across four suites. Dialect behavior is pinned against the reference engine running in Docker — never self-assessment.');
statCard(s, MX + 3.9, 2.35, 3.7, 3.0, '100%', 'No oracle? Real clients',
  'mutation score on the message logic (Stryker). Mail, SMS and broker channels validated with the official client libraries.');
statCard(s, MX + 7.8, 2.35, 3.7, 3.0, '392 ns', 'Measured performance',
  'to match the last of 1000 stubs (was 29 µs). Templated response 699 µs → 1.21 µs. Semantics pinned by the differential suite while optimizing.');
s.addText('Honest surfaces: import warnings for unsupported fields, a public deferred-edge register, binding semver promises since 1.0.',
  { x: MX, y: 5.75, w: 11.3, h: 0.6, fontFace: BODY, fontSize: 14, color: DIM, margin: 0 });
s.addNotes('The definition of done is a green differential diff, not self-assessment. Where no oracle exists: real clients + mutation testing.');

// ---- 14 · Enterprise posture
s = slide();
kicker(s, 'Run it anywhere');
title(s, 'Enterprise posture, day one');
card(s, MX, 2.35, 5.6, 1.85, 'Deploy',
  'Non-root image, live/ready probes with drain, Helm chart whose security posture is asserted in CI.');
card(s, MX + 5.9, 2.35, 5.6, 1.85, 'Supply chain',
  'SBOM, keyless signing, provenance, image scanning, automated dependency updates.');
card(s, MX, 4.4, 5.6, 1.95, 'Observe',
  'OpenTelemetry traces & metrics, credential-free Prometheus scrape, JSON logs, bounded label cardinality.');
card(s, MX + 5.9, 4.4, 5.6, 1.95, 'Secure',
  'Admin auth: Basic, per-tenant credentials, OIDC + PKCE dashboard sign-in. Audit trail of every admin change. Key rings with restart-free rotation. Payload crypto: JWE decrypt, response protect & sign.');
s.addNotes('One breath per card. Close with: the broker channel is shipping — the sandbox is growing from HTTP-shaped to event-shaped.');

// ---- 15 · Closing
s = slide();
kicker(s, 'Closing', 2.3);
s.addText([
  { text: 'Mock the API.\n', options: { color: 'FFFFFF' } },
  { text: 'Keep it honest.', options: { color: ACC } },
], { x: MX, y: 2.7, w: 11, h: 1.9, fontFace: BODY, fontSize: 44, bold: true, margin: 0, lineSpacingMultiple: 1.05 });
s.addText('Spec → sandbox in a minute · every channel your integration touches · and three verify surfaces that tell you when the mock stops telling the truth.',
  { x: MX, y: 4.75, w: 9.8, h: 0.9, fontFace: BODY, fontSize: 15, color: DIM, margin: 0, lineSpacingMultiple: 1.2 });
chips(s, ['demo: localhost:8080/__mockifyr', 'docs: mockifyr.qorpe.com'], 5.9);
s.addNotes('Questions. Offer the sandbox URL + a key to anyone who wants to poke at it from their own laptop.');

p.writeFile({ fileName: 'demo/Mockifyr-Demo.pptx' }).then(() => console.log('written'));
