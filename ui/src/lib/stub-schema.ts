import { z } from 'zod'

// The stub editor's form model — a friendly projection of a mapping. It intentionally covers
// the common surface (URL/method/header/query/body matchers, response + templating, delay, fault,
// scenario, priority, proxy); the raw-JSON tab is the escape hatch for anything beyond it.

export const MATCH_OPS = ['equalTo', 'contains', 'matches', 'doesNotMatch'] as const
export const BODY_OPS = ['equalTo', 'equalToJson', 'matchesJsonPath', 'matchesXPath', 'contains', 'matches'] as const
// matchesJsonPath/matchesXPath object form: { expression, <subOp>: value }. '' = plain expression form.
export const BODY_SUB_OPS = ['', 'equalTo', 'contains', 'matches', 'doesNotMatch'] as const
export const URL_MATCH = ['urlPath', 'url', 'urlPathPattern', 'urlPattern'] as const
export const FAULTS = ['', 'EMPTY_RESPONSE', 'MALFORMED_RESPONSE_CHUNK', 'RANDOM_DATA_THEN_CLOSE', 'CONNECTION_RESET_BY_PEER'] as const

const kvMatcher = z.object({
  name: z.string(),
  operator: z.enum(MATCH_OPS),
  value: z.string(),
})

// A millisecond field: empty (unset) or a non-negative whole number. Kept as a string because the form
// leaves it blank when unset; validated so a stray letter or negative value is caught, not silently NaN.
const msField = z.string().refine((v) => v.trim() === '' || /^\d+$/.test(v.trim()), 'Enter milliseconds as a whole number')

export const stubSchema = z.object({
  name: z.string(),
  description: z.string(),
  method: z.string().min(1),
  urlMatchType: z.enum(URL_MATCH),
  urlValue: z.string().min(1, 'URL is required'),
  headers: z.array(kvMatcher),
  queryParams: z.array(kvMatcher),
  bodyPatterns: z.array(z.object({ operator: z.enum(BODY_OPS), value: z.string(), subOperator: z.enum(BODY_SUB_OPS), subValue: z.string() })),
  priority: z.coerce.number({ message: 'Priority must be a number' }).int().min(1, 'Priority must be 1–255').max(255, 'Priority must be 1–255'),
  scenarioName: z.string(),
  requiredScenarioState: z.string(),
  newScenarioState: z.string(),
  responseStatus: z.coerce.number({ message: 'Status must be a number' }).int().min(100, 'Status must be 100–599').max(599, 'Status must be 100–599'),
  responseHeaders: z.array(z.object({ name: z.string(), value: z.string() })),
  responseBody: z.string(),
  /** Emit the response body as jsonBody (preserved from the mapping) instead of a body string. */
  responseJsonBody: z.boolean(),
  useTemplating: z.boolean(),
  // GraphQL matcher (G14/ADR 0010): when query is non-empty the mapping carries graphql-body-matcher.
  graphqlQuery: z.string(),
  graphqlVariables: z.string(),
  graphqlOperationName: z.string(),
  fixedDelayMs: msField,
  fault: z.enum(FAULTS),
  proxyBaseUrl: z.string(),
  webhookMethod: z.string(),
  webhookUrl: z.string(),
  webhookBody: z.string(),
  webhookHeaders: z.array(z.object({ name: z.string(), value: z.string() })),
  webhookDelayMs: msField,
})
  // Scenario consistency: a required/new state only means something inside a named scenario. Guide the
  // user to name the scenario rather than silently dropping the state on export.
  .refine((f) => !(f.requiredScenarioState.trim() || f.newScenarioState.trim()) || f.scenarioName.trim().length > 0, {
    path: ['scenarioName'],
    message: 'Scenario name is required when a state is set',
  })

export type StubForm = z.infer<typeof stubSchema>

/** A best-practice name suggestion from the method + last URL segment, e.g. GET /a/RetrieveApp → "Retrieve app". */
export function suggestName(_method: string, url: string): string {
  const seg = (url || '').split('?')[0].split('/').filter(Boolean).pop() ?? ''
  const words = seg.replace(/[-_]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').trim().toLowerCase()
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : ''
}

export const emptyStub: StubForm = {
  name: '',
  description: '',
  method: 'GET',
  urlMatchType: 'urlPath',
  urlValue: '/',
  headers: [],
  queryParams: [],
  bodyPatterns: [],
  priority: 5,
  scenarioName: '',
  requiredScenarioState: '',
  newScenarioState: '',
  responseStatus: 200,
  responseHeaders: [{ name: 'Content-Type', value: 'application/json' }],
  responseBody: '', responseJsonBody: false, graphqlQuery: '', graphqlVariables: '', graphqlOperationName: '',
  // Default ON for new stubs: in practice almost every authored body maps request values ({{…}}),
  // and modern WireMock applies templating globally by default, so OFF surprised operators.
  // Editing an existing stub still reflects the mapping's own transformers (fromMapping).
  useTemplating: true,
  fixedDelayMs: '',
  fault: '',
  proxyBaseUrl: '',
  webhookMethod: 'POST',
  webhookUrl: '',
  webhookBody: '',
  webhookHeaders: [],
  webhookDelayMs: '',
}

/** Build the mapping object from the form (unset fields are omitted). */
export function toMapping(f: StubForm): Record<string, unknown> {
  const request: Record<string, unknown> = { method: f.method, [f.urlMatchType]: f.urlValue }
  const headerMap = matcherMap(f.headers)
  if (headerMap) request.headers = headerMap
  const queryMap = matcherMap(f.queryParams)
  if (queryMap) request.queryParameters = queryMap
  if (f.bodyPatterns.length) request.bodyPatterns = f.bodyPatterns.map(serializeBodyPattern)
  // GraphQL (ADR 0010): the query field materializes as the graphql-body-matcher custom matcher, so
  // the classic form fully expresses a GraphQL stub — nothing is lost on a form save.
  if (f.graphqlQuery.trim()) {
    const parameters: Record<string, unknown> = { query: f.graphqlQuery }
    const vars = f.graphqlVariables.trim() ? tryJson(f.graphqlVariables) : null
    if (vars !== null && typeof vars === 'object') parameters.variables = vars
    if (f.graphqlOperationName.trim()) parameters.operationName = f.graphqlOperationName.trim()
    request.customMatcher = { name: 'graphql-body-matcher', parameters }
  }

  const response: Record<string, unknown> = {}
  if (f.proxyBaseUrl.trim()) {
    response.proxyBaseUrl = f.proxyBaseUrl.trim()
  } else {
    response.status = Number(f.responseStatus)
    if (f.responseHeaders.length) response.headers = Object.fromEntries(f.responseHeaders.filter((h) => h.name).map((h) => [h.name, h.value]))
    if (f.responseBody) {
      // A mapping that carried jsonBody keeps jsonBody (the gRPC codec and exports depend on it);
      // an unparseable edit falls back to a body string rather than corrupting the response.
      const parsed = f.responseJsonBody ? tryJson(f.responseBody) : null
      if (parsed !== null && typeof parsed === 'object') response.jsonBody = parsed
      else response.body = f.responseBody
    }
    if (f.useTemplating) response.transformers = ['response-template']
  }
  if (f.fixedDelayMs.trim()) response.fixedDelayMilliseconds = Number(f.fixedDelayMs)
  if (f.fault) response.fault = f.fault

  // Number-input form fields arrive as strings; status/priority are JSON numbers, and the
  // engine's reader rejects a string-typed status. Coerce here so edits to these fields serialize correctly.
  const mapping: Record<string, unknown> = { request, response, priority: Number(f.priority) }
  // Friendly name + description ride along as WireMock's standard `name` field and `metadata.description`.
  // The matching engine ignores both; the admin API round-trips them via the raw source, so no backend
  // change is needed. They drive the tree's readable labels and the editor's description.
  if (f.name.trim()) mapping.name = f.name.trim()
  if (f.description.trim()) mapping.metadata = { description: f.description.trim() }
  if (f.scenarioName.trim()) {
    mapping.scenarioName = f.scenarioName.trim()
    if (f.requiredScenarioState.trim()) mapping.requiredScenarioState = f.requiredScenarioState.trim()
    if (f.newScenarioState.trim()) mapping.newScenarioState = f.newScenarioState.trim()
  }
  // Webhook / callback: fire an outbound request after a match (postServeActions). The JSON tab
  // remains the escape hatch for advanced options (headers, delay, multiple webhooks).
  if (f.webhookUrl.trim()) {
    const parameters: Record<string, unknown> = { method: f.webhookMethod || 'POST', url: f.webhookUrl.trim() }
    const webhookHeaders = Object.fromEntries(f.webhookHeaders.filter((h) => h.name.trim()).map((h) => [h.name, h.value]))
    if (Object.keys(webhookHeaders).length) parameters.headers = webhookHeaders
    if (f.webhookBody.trim()) parameters.body = f.webhookBody
    if (f.webhookDelayMs.trim()) parameters.delay = { type: 'fixed', milliseconds: Number(f.webhookDelayMs) }
    mapping.postServeActions = [{ name: 'webhook', parameters }]
  }
  return mapping
}

export function toJson(f: StubForm): string {
  return JSON.stringify(toMapping(f), null, 2)
}

const obj = (v: unknown): Record<string, unknown> => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {})
const str = (v: unknown, fallback = ''): string => (typeof v === 'string' ? v : fallback)

/** Reverse of {@link toMapping}: seed the editor form from an existing mapping (edit round-trip). */
export function fromMapping(mapping: Record<string, unknown>): StubForm {
  const req = obj(mapping.request)
  const res = obj(mapping.response)

  let urlMatchType: StubForm['urlMatchType'] = 'urlPath'
  let urlValue = '/'
  for (const t of URL_MATCH) {
    if (typeof req[t] === 'string') { urlMatchType = t; urlValue = req[t] as string; break }
  }

  const parseMatchers = (source: unknown): StubForm['headers'] =>
    Object.entries(obj(source)).map(([name, m]) => {
      const entry = obj(m)
      const operator = MATCH_OPS.find((o) => o in entry) ?? 'equalTo'
      return { name, operator, value: str(entry[operator]) }
    })

  const bodyPatterns: StubForm['bodyPatterns'] = Array.isArray(req.bodyPatterns)
    ? (req.bodyPatterns as unknown[]).map((p) => {
        const entry = obj(p)
        const operator = BODY_OPS.find((o) => o in entry) ?? 'equalToJson'
        const v = entry[operator]
        // Object-form path matcher { expression, <subOp>: value } maps onto the row's expression +
        // sub-matcher fields; any richer shape stays a JSON blob (parsed back on save, never corrupted).
        if (operator === 'matchesJsonPath' || operator === 'matchesXPath') {
          // Some exporters string-encode the object form ("{ \"expression\": …, \"equalTo\": … }");
          // save already normalizes it via tryJson, so parse it here too — otherwise an imported stub
          // shows the whole JSON dumped into the expression field until the first manual save (#158).
          const parsed = typeof v === 'string' && v.trim().startsWith('{') ? tryJson(v) : v
          const o = obj(parsed)
          const subOperator = BODY_SUB_OPS.find((s) => s !== '' && s in o)
          if (typeof o.expression === 'string' && subOperator && typeof o[subOperator] === 'string' && Object.keys(o).length === 2)
            return { operator, value: o.expression, subOperator, subValue: o[subOperator] as string }
          // A string-encoded richer shape keeps the blob convention (pretty JSON, round-tripped on save).
          if (parsed !== v && parsed !== null && typeof parsed === 'object')
            return { operator, value: JSON.stringify(parsed, null, 2), subOperator: '' as const, subValue: '' }
        }
        return { operator, value: typeof v === 'string' ? v : JSON.stringify(v ?? '', null, 2), subOperator: '' as const, subValue: '' }
      })
    : []

  const matcher = obj(req.customMatcher)
  const matcherParameters = obj(matcher.parameters)
  const isGraphql = str(matcher.name).toLowerCase() === 'graphql-body-matcher'

  const wh = obj((Array.isArray(mapping.postServeActions) ? mapping.postServeActions : []).map(obj).find((a) => a.name === 'webhook'))
  const whParams = obj(wh.parameters)
  const whDelay = obj(whParams.delay)

  return {
    name: str(mapping.name),
    description: str(obj(mapping.metadata).description),
    method: str(req.method, 'GET'),
    urlMatchType,
    urlValue,
    headers: parseMatchers(req.headers),
    queryParams: parseMatchers(req.queryParameters),
    bodyPatterns,
    priority: typeof mapping.priority === 'number' ? mapping.priority : 5,
    scenarioName: str(mapping.scenarioName),
    requiredScenarioState: str(mapping.requiredScenarioState),
    newScenarioState: str(mapping.newScenarioState),
    responseStatus: typeof res.status === 'number' ? res.status : 200,
    responseHeaders: Object.entries(obj(res.headers)).map(([name, value]) => ({ name, value: str(value) })),
    responseBody: typeof res.body === 'string' ? res.body : res.jsonBody !== undefined ? JSON.stringify(res.jsonBody, null, 2) : '',
    responseJsonBody: res.jsonBody !== undefined,
    graphqlQuery: isGraphql ? str(matcherParameters.query) : '',
    graphqlVariables: isGraphql && matcherParameters.variables !== undefined ? JSON.stringify(matcherParameters.variables, null, 2) : '',
    graphqlOperationName: isGraphql ? str(matcherParameters.operationName) : '',
    useTemplating: Array.isArray(res.transformers) && (res.transformers as unknown[]).includes('response-template'),
    fixedDelayMs: typeof res.fixedDelayMilliseconds === 'number' ? String(res.fixedDelayMilliseconds) : '',
    fault: (FAULTS as readonly string[]).includes(str(res.fault)) ? (str(res.fault) as StubForm['fault']) : '',
    proxyBaseUrl: str(res.proxyBaseUrl),
    webhookMethod: str(whParams.method, 'POST'),
    webhookUrl: str(whParams.url),
    webhookBody: str(whParams.body),
    webhookHeaders: Object.entries(obj(whParams.headers)).map(([name, value]) => ({ name, value: str(value) })),
    webhookDelayMs: typeof whDelay.milliseconds === 'number' ? String(whDelay.milliseconds) : '',
  }
}

// The two path matchers distinguish a string form (the value IS the path expression) from an object
// form ({ expression, <subOp>: value }). Everything else serializes the row value as-is.
function serializeBodyPattern(b: StubForm['bodyPatterns'][number]): Record<string, unknown> {
  if (b.operator === 'equalToJson') return { equalToJson: tryJson(b.value) }
  if (b.operator === 'matchesJsonPath' || b.operator === 'matchesXPath') {
    if (b.subOperator && b.subValue.trim() !== '') return { [b.operator]: { expression: b.value, [b.subOperator]: b.subValue } }
    // A JSON-blob fallback (an object shape the row fields don't model, kept verbatim by fromMapping)
    // must parse back to the object — serializing it as a "path expression" string corrupts the matcher.
    const parsed = tryJson(b.value)
    if (parsed !== null && typeof parsed === 'object') return { [b.operator]: parsed }
  }
  return { [b.operator]: b.value }
}

function matcherMap(rows: StubForm['headers']): Record<string, unknown> | null {
  const entries = rows.filter((r) => r.name.trim())
  if (!entries.length) return null
  return Object.fromEntries(entries.map((r) => [r.name, { [r.operator]: r.value }]))
}

function tryJson(v: string): unknown {
  try { return JSON.parse(v) } catch { return v }
}
