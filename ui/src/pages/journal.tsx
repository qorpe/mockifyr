import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { useQuery } from '@tanstack/react-query'
import {
  type ColumnDef, flexRender, getCoreRowModel, getPaginationRowModel,
  getSortedRowModel, type SortingState, useReactTable,
} from '@tanstack/react-table'
import { ArrowUpDown, CheckCircle2, ChevronLeft, ChevronRight, Clock, MessageSquareText, RefreshCw, Rows2, Rows3, Trash2, XCircle } from 'lucide-react'
import { cn, formatDateTime, timeAgo } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { fetchJournal, resetJournal, type JournalEntry } from '@/lib/api'
import { MethodChip } from '@/components/ui/badges'
import { Button } from '@qorpe/ui'
import { FacetFilter } from '@/components/ui/facet-filter'
import { SearchBox } from '@/components/ui/search-box'
import { EmptyState } from '@qorpe/ui'
import { JournalArt } from '@/components/ui/illustrations'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { JournalDetailSheet } from '@/components/journal/journal-detail'
import {
  applyFilters, clearFacet, countSelected, type FacetDef, facetOptions, type Selections, toggleSelection,
} from '@/lib/faceted'

function statusTone(status: number | null): string {
  if (status == null) return 'text-muted-foreground bg-muted border-border'
  if (status >= 500) return 'text-danger bg-danger-bg border-danger-border'
  if (status >= 400) return 'text-warning bg-warning-bg border-warning-border'
  return 'text-success bg-success-bg border-success-border'
}

/** Group a status into its class for faceting (2xx/3xx/4xx/5xx), or null when there is no response. */
function statusClass(status: number | null): string | null {
  if (status == null) return null
  if (status >= 500) return '5xx'
  if (status >= 400) return '4xx'
  if (status >= 300) return '3xx'
  if (status >= 200) return '2xx'
  return null
}

// TanStack Table requires referentially-stable `data`/`columns` across renders; a fresh array each
// render feeds an update→render→update cycle that freezes a visible tab. Keep every table input stable.
const EMPTY_ENTRIES: JournalEntry[] = []
const EMPTY_SET = new Set<string>()
const FACETS: FacetDef<JournalEntry>[] = [
  { id: 'method', get: (r) => r.method },
  { id: 'status', get: (r) => statusClass(r.status) },
  { id: 'protocol', get: (r) => r.protocol ?? 'http' },
]

export function JournalPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const [unmatchedOnly, setUnmatchedOnly] = useState(false)
  const { data, isLoading, isFetching, refetch } = useQuery({
    queryKey: ['journal', tenant, unmatchedOnly],
    queryFn: () => fetchJournal(tenant, unmatchedOnly),
    // Poll for new requests. Sample mode (no host answering) polls slower instead of stopping: a
    // single failed fetch used to switch the interval off permanently, freezing the table until a
    // full page reload even after the host came back (#155).
    refetchInterval: (query) => (query.state.data?.mock ? 15000 : 5000),
    // Keep polling when the window is unfocused — the typical flow is driving requests from a
    // terminal while watching this table in a background browser window (#155).
    refetchIntervalInBackground: true,
  })

  const [confirmClear, setConfirmClear] = useState(false)

  // Clearing is what makes a shared host usable between test runs: counts and this table read the
  // same store, so the reset empties both. It is scoped to the current tenant — never a neighbour's.
  async function clearAll() {
    setConfirmClear(false)
    await resetJournal(tenant)
    setDetailId(null)
    await refetch()
    toast.success(t('journal.cleared'))
  }

  const [selected, setSelected] = useState<Selections>({})
  const [search, setSearch] = useState('')
  // Default to newest-first (#118); the timestamp column is sortable like the rest.
  const [sorting, setSorting] = useState<SortingState>([{ id: 'loggedDate', desc: true }])
  const [dense, setDense] = useState(false)
  const [detailId, setDetailId] = useState<string | null>(null)

  const columns = useMemo<ColumnDef<JournalEntry>[]>(() => [
    { accessorKey: 'method', header: () => t('stubs.method'), cell: ({ getValue }) => <MethodChip method={getValue<string>()} /> },
    {
      accessorKey: 'url', header: () => t('stubs.url'),
      cell: ({ row, getValue }) => {
        // Non-HTTP protocols get a chip (ADR 0010) — same rule as the stub tree, HTTP stays unmarked.
        const p = row.original.protocol
        return (
          <span className="inline-flex items-center gap-2">
            {p === 'grpc' && <span className="rounded-md border border-violet-border bg-violet-bg px-1.5 py-0.5 font-mono text-[10px] font-bold text-violet">gRPC</span>}
            {p === 'graphql' && <span className="rounded-md border border-info-border bg-info-bg px-1.5 py-0.5 font-mono text-[10px] font-bold text-info">GraphQL</span>}
            {p === 'sms' && <span className="inline-flex items-center gap-1 rounded-md border border-warning-border bg-warning-bg px-1.5 py-0.5 font-mono text-[10px] font-bold text-warning"><MessageSquareText className="size-3" />SMS</span>}
            <span className="font-mono text-[12.5px]">{getValue<string>()}</span>
          </span>
        )
      },
    },
    {
      accessorKey: 'status', header: () => t('journal.status'),
      cell: ({ getValue }) => {
        const s = getValue<number | null>()
        return <span className={cn('inline-flex rounded-md border px-2 py-0.5 font-mono text-[11px] font-bold', statusTone(s))}>{s ?? '—'}</span>
      },
    },
    {
      accessorKey: 'wasMatched', header: () => t('journal.result'),
      cell: ({ getValue }) => getValue<boolean>()
        ? <span className="inline-flex items-center gap-1.5 text-[12px] font-medium text-success"><CheckCircle2 className="size-3.5" />{t('journal.matched')}</span>
        : <span className="inline-flex items-center gap-1.5 text-[12px] font-medium text-warning"><XCircle className="size-3.5" />{t('journal.unmatched')}</span>,
    },
    {
      // Full date-time first; the compact relative time trails it so at-a-glance recency stays.
      accessorKey: 'loggedDate', header: () => t('journal.when'),
      cell: ({ getValue }) => {
        const iso = getValue<string | null>()
        return (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground">
            <Clock className="size-3.5" />
            <span className="font-mono tabular-nums text-foreground">{formatDateTime(iso)}</span>
            <span className="text-faint">{timeAgo(iso)}</span>
          </span>
        )
      },
    },
  ], [t])

  // Stable references: entries changes only when the query result changes; facet options and the
  // filtered set are memoized so the table never sees a fresh array on an unrelated render.
  const entries = data?.entries ?? EMPTY_ENTRIES
  const methodOptions = useMemo(() => facetOptions(entries, (r) => r.method), [entries])
  const statusOptions = useMemo(() => facetOptions(entries, (r) => statusClass(r.status)), [entries])
  const protocolOptions = useMemo(() => facetOptions(entries, (r) => r.protocol ?? 'http'), [entries])
  const rows = useMemo(() => applyFilters(entries, FACETS, selected, search, (r) => r.url), [entries, selected, search])

  const table = useReactTable({
    data: rows,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    autoResetPageIndex: false,
    initialState: { pagination: { pageSize: 12 } },
  })

  // Reset to the first page whenever the search term or filters change, so a match that lives on page 1
  // isn't hidden behind a stale page index (#123). autoResetPageIndex is off to keep polling from
  // yanking the page, so we reset explicitly on the inputs that should.
  useEffect(() => { table.setPageIndex(0) }, [search, selected, unmatchedOnly, table])

  const activeCount = countSelected(selected)

  return (
    <div className="mx-auto max-w-[1360px]">
      <div className="mb-6 inline-flex gap-1 rounded-xl bg-muted p-1">
        <button onClick={() => setUnmatchedOnly(false)} className={cn('rounded-lg px-3.5 py-1.5 text-sm font-semibold transition-colors', !unmatchedOnly ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground')}>{t('journal.allRequests')}</button>
        <button onClick={() => setUnmatchedOnly(true)} className={cn('rounded-lg px-3.5 py-1.5 text-sm font-semibold transition-colors', unmatchedOnly ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground')}>{t('journal.unmatchedOnly')}</button>
      </div>

      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.journal')}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t('journal.subtitle')}</p>
      </header>

      <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
        <div className="flex flex-wrap items-center gap-2 border-b border-border p-3">
          <SearchBox value={search} onCommit={setSearch} placeholder={t('journal.filter')} />
          <FacetFilter label={t('stubs.method')} options={methodOptions} selected={selected.method ?? EMPTY_SET}
            onToggle={(v) => setSelected((s) => toggleSelection(s, 'method', v))} onClear={() => setSelected((s) => clearFacet(s, 'method'))} clearLabel={t('common.clear')} />
          <FacetFilter label={t('journal.status')} options={statusOptions} selected={selected.status ?? EMPTY_SET}
            onToggle={(v) => setSelected((s) => toggleSelection(s, 'status', v))} onClear={() => setSelected((s) => clearFacet(s, 'status'))} clearLabel={t('common.clear')} />
          <FacetFilter label={t('stubs.protocol')} options={protocolOptions} selected={selected.protocol ?? EMPTY_SET}
            onToggle={(v) => setSelected((s) => toggleSelection(s, 'protocol', v))} onClear={() => setSelected((s) => clearFacet(s, 'protocol'))} clearLabel={t('common.clear')} />
          {activeCount > 0 && (
            <Button variant="ghost" size="sm" onClick={() => setSelected({})}>{t('common.clear')}</Button>
          )}
          <Button variant="outline" className="ms-auto" onClick={() => refetch()} disabled={isFetching}>
            <RefreshCw className={cn(isFetching && 'animate-spin')} />{t('common.refresh')}
          </Button>
          <Button variant="outline" onClick={() => setDense((d) => !d)}>
            {dense ? <Rows3 /> : <Rows2 />}{t('stubs.density')}
          </Button>
          <Button variant="outline" onClick={() => setConfirmClear(true)} disabled={!entries.length}>
            <Trash2 />{t('journal.clearAll')}
          </Button>
        </div>

        <div className="scroll-area overflow-x-auto">
          <table className="w-full min-w-[760px] border-collapse">
            <thead>
              {table.getHeaderGroups().map((hg) => (
                <tr key={hg.id}>
                  {hg.headers.map((h) => (
                    <th key={h.id} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                      <button onClick={h.column.getToggleSortingHandler()} className="inline-flex items-center gap-1.5 hover:text-foreground">
                        {flexRender(h.column.columnDef.header, h.getContext())}
                        <ArrowUpDown className="size-3" />
                      </button>
                    </th>
                  ))}
                </tr>
              ))}
            </thead>
            <tbody>
              {isLoading ? (
                Array.from({ length: 6 }).map((_, i) => (
                  <tr key={i}><td colSpan={columns.length} className="px-4 py-3.5"><div className="h-4 w-full animate-pulse rounded bg-muted" /></td></tr>
                ))
              ) : table.getRowModel().rows.length === 0 ? (
                <tr><td colSpan={columns.length}><EmptyState art={<JournalArt />} title={t('journal.empty')} className="py-16" /></td></tr>
              ) : (
                table.getRowModel().rows.map((row) => (
                  <tr key={row.id} onClick={() => setDetailId(row.original.id)}
                    className="cursor-pointer border-b border-border transition-colors hover:bg-muted/40">
                    {row.getVisibleCells().map((cell) => (
                      <td key={cell.id} className={cn('px-4 align-middle', dense ? 'py-2' : 'py-3')}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>
                    ))}
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <div className="flex flex-wrap items-center gap-3 border-t border-border bg-muted/30 px-4 py-3 text-[12.5px] text-muted-foreground">
          {data?.mock && (
            <span className="inline-flex items-center gap-1.5 rounded-full border border-warning-border bg-warning-bg px-2.5 py-0.5 text-[11.5px] font-medium text-warning">{t('stubs.sample')}</span>
          )}
          <span>{t('stubs.showing')} <b className="tabular-nums">{table.getRowModel().rows.length}</b> {t('stubs.of')} <b className="tabular-nums">{rows.length}</b></span>
          {data && data.total > data.entries.length && (
            <span className="text-warning">· {t('journal.capped', { cap: data.entries.length })}</span>
          )}
          <div className="ms-auto flex items-center gap-1.5">
            <Button variant="outline" size="iconSm" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()} aria-label="Previous"><ChevronLeft className="rtl:rotate-180" /></Button>
            <span className="px-1 tabular-nums">{table.getState().pagination.pageIndex + 1} / {Math.max(1, table.getPageCount())}</span>
            <Button variant="outline" size="iconSm" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()} aria-label="Next"><ChevronRight className="rtl:rotate-180" /></Button>
          </div>
        </div>
      </div>

      <JournalDetailSheet id={detailId} tenant={tenant} onClose={() => setDetailId(null)} />

      <ConfirmDialog open={confirmClear} onOpenChange={setConfirmClear}
        title={t('journal.clearConfirmTitle')} body={t('journal.clearConfirmBody')}
        confirmLabel={t('journal.clearAll')} cancelLabel={t('editor.cancel')} destructive
        onConfirm={() => void clearAll()} />
    </div>
  )
}
