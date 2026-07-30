// Seeds the stubs the k6 harness drives (#249). Node 18+, no dependencies.
//
//   node bench/load/seed.mjs                 # against http://localhost:8080
//   BASE=http://mockifyr:8080 node bench/load/seed.mjs
//
// Also seeds 999 filler stubs so the store is a realistic size rather than a store of four — a
// matching cost measured against an almost-empty store is not a number anyone can plan with.

const BASE = process.env.BASE || 'http://localhost:8080'
const FILLER = Number(process.env.FILLER ?? 999)

const mappings = [
  {
    request: { method: 'GET', urlPath: '/bench/simple' },
    response: { status: 200, body: 'ok' },
  },
  {
    request: { method: 'POST', urlPath: '/bench/templated' },
    response: {
      status: 200,
      body: "{{request.body}} {{randomValue type='UUID'}} {{now format='yyyy-MM-dd'}}",
      transformers: ['response-template'],
    },
  },
  {
    request: { method: 'GET', urlPath: '/bench/large' },
    response: { status: 200, body: 'x'.repeat(256 * 1024) },
  },
  {
    request: {
      method: 'POST',
      urlPath: '/bench/payment',
      bodyPatterns: [
        { equalToJson: '{"amount":100,"currency":"SAR"}', ignoreExtraElements: true },
      ],
    },
    response: { status: 201, body: 'created' },
  },
  ...Array.from({ length: FILLER }, (_, i) => ({
    request: { method: 'GET', urlPath: `/bench/filler-${i}` },
    response: { status: 200, body: 'filler' },
  })),
]

const res = await fetch(`${BASE}/__admin/mappings/import`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ mappings }),
})

if (!res.ok) {
  console.error(`Seed failed: ${res.status} ${await res.text()}`)
  process.exit(1)
}

console.log(`Seeded ${mappings.length} stubs into ${BASE}`)
