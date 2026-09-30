# 0004 — Own domain model + WireMock JSON import adapter

**Status:** Accepted · **Date:** 2026-07-01

## Context
Using the WireMock mappings JSON format directly as our internal model would simplify the
harness, but it would tie the engine's design to an external file format. At the same time,
the differential harness must load the same stub into both us and the oracle.

## Decision
We keep our **own clean internal domain model**; WireMock JSON is translated by an **import
adapter** (`Mockifyr.Adapters.MappingJson`). In the differential harness the **single source
of truth is WireMock JSON**: loaded raw into Java WireMock, translated into Mockifyr through
the import adapter. This puts the adapter itself under test and lets the oracle receive the
untouched canonical format.

## Consequences
- (+) The engine's model is designed for its own needs and can evolve independently of any
  import format.
- (+) The import adapter is automatically validated by the differential suite.
- (+) The admin API and the harness share the same adapter.
- (−) A model ↔ WireMock JSON translation layer must be maintained.
