// Client-side faceted filtering shared by every list/table screen. Semantics follow the usual faceted filter
// model: within a single facet the selected values are OR'd; across different facets they are AND'd; a
// free-text term (committed on Enter) additionally requires a substring match on the row's search field.
//
// This runs on already-bounded data (stubs are small; the request journal is capped when fetched), so
// filtering in the client is instant and keeps the engine untouched. If full-history server-side
// filtering is ever needed, the same selection shape can drive an admin-API query instead.

export type Selections = Record<string, Set<string>>

export interface FacetDef<T> {
  id: string
  get: (row: T) => string | null | undefined
}

/** Distinct values (with counts) present in `rows` for one accessor, sorted for stable display. */
export function facetOptions<T>(rows: T[], get: (row: T) => string | null | undefined): { value: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const r of rows) {
    const v = get(r)
    if (v == null || v === '') continue
    counts.set(v, (counts.get(v) ?? 0) + 1)
  }
  return [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([value, count]) => ({ value, count }))
}

/** Apply facet selections (OR within, AND across) plus a committed search term. */
export function applyFilters<T>(
  rows: T[],
  facets: FacetDef<T>[],
  selected: Selections,
  search: string,
  searchGet: (row: T) => string,
): T[] {
  const q = search.trim().toLowerCase()
  return rows.filter((row) => {
    for (const f of facets) {
      const sel = selected[f.id]
      if (sel && sel.size > 0) {
        const v = f.get(row)
        if (v == null || !sel.has(v)) return false
      }
    }
    if (q && !searchGet(row).toLowerCase().includes(q)) return false
    return true
  })
}

/** Immutably toggle one value in one facet; empties are pruned so `countSelected` stays honest. */
export function toggleSelection(selected: Selections, facetId: string, value: string): Selections {
  const next: Selections = { ...selected }
  const set = new Set(next[facetId] ?? [])
  if (set.has(value)) set.delete(value)
  else set.add(value)
  if (set.size === 0) delete next[facetId]
  else next[facetId] = set
  return next
}

export function clearFacet(selected: Selections, facetId: string): Selections {
  if (!selected[facetId]) return selected
  const next = { ...selected }
  delete next[facetId]
  return next
}

export function countSelected(selected: Selections): number {
  return Object.values(selected).reduce((sum, set) => sum + set.size, 0)
}
