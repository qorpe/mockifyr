// k6 load harness for the HTTP facade (#249).
//
// The engine benchmarks in ../Mockifyr.Benchmarks measure what one request costs inside Mockifyr.
// This measures what a client actually sees: Kestrel, the network, and the request in flight. Both
// exist because a regression in one is invisible in the other.
//
//   k6 run bench/load/mockifyr-load.js                      # against http://localhost:8080
//   BASE=http://mockifyr:8080 k6 run bench/load/mockifyr-load.js
//   SCENARIO=journal_on k6 run bench/load/mockifyr-load.js   # one scenario only
//
// Seed the host first:
//   dotnet run --project src/Mockifyr.Server -- --port 8080 --journal-disabled
//   node bench/load/seed.mjs
//
// Thresholds are deliberately loose. They exist to make a run fail loudly when something is badly
// wrong, not to encode a performance promise — the published numbers come from a stated machine,
// and a laptop under a video call is not that machine.

import http from 'k6/http'
import { check } from 'k6'

const BASE = __ENV.BASE || 'http://localhost:8080'
const ONLY = __ENV.SCENARIO

const profile = {
  executor: 'constant-vus',
  vus: Number(__ENV.VUS || 50),
  duration: __ENV.DURATION || '30s',
}

const all = {
  // A static stub: the floor. Everything else is measured against this.
  simple: { ...profile, exec: 'simple', tags: { case: 'simple' } },
  // A templated response — the renderer runs per request.
  templated: { ...profile, exec: 'templated', tags: { case: 'templated' } },
  // A 256 KiB response body: where the cost stops being logic and becomes bytes.
  large: { ...profile, exec: 'large', tags: { case: 'large' } },
  // Structural JSON body matching, which parses rather than compares.
  jsonBody: { ...profile, exec: 'jsonBody', tags: { case: 'json-body' } },
  // The same simple case against a host started WITHOUT --journal-disabled. Run both and compare:
  // that difference is what the journal costs, and it is the number that decides the flag.
  journal_on: { ...profile, exec: 'simple', tags: { case: 'journal-on' } },
}

export const options = {
  scenarios: ONLY ? { [ONLY]: all[ONLY] } : { simple: all.simple },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(99)<250'],
  },
}

export function simple() {
  const res = http.get(`${BASE}/bench/simple`)
  check(res, { 'status 200': (r) => r.status === 200 })
}

export function templated() {
  const res = http.post(`${BASE}/bench/templated`, JSON.stringify({ hello: 'world' }), {
    headers: { 'Content-Type': 'application/json' },
  })
  check(res, { 'status 200': (r) => r.status === 200 })
}

export function large() {
  const res = http.get(`${BASE}/bench/large`)
  check(res, { 'status 200': (r) => r.status === 200 })
}

export function jsonBody() {
  const res = http.post(
    `${BASE}/bench/payment`,
    JSON.stringify({ amount: 100, currency: 'SAR', reference: 'abc' }),
    { headers: { 'Content-Type': 'application/json' } },
  )
  check(res, { 'status 201': (r) => r.status === 201 })
}
