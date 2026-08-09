import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ChevronRight, ChevronsDownUp, ChevronsUpDown, Download, Import, Pin, PinOff, Plus, Trash2, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { deleteMessageMapping, deleteStub, fetchEnvironments, fetchMessageMappings, fetchStubs, type MessageMapping, type Stub } from '@/lib/api'
import { buildStubTree, countStubs, type StubTreeNode } from '@/lib/stub-tree'
import { CryptoChips, MethodChip, ProtocolChip, StatusCode } from '@/components/ui/badges'
import { NewStubWorkspace } from '@/components/stubs/channel-editors'
import { JsonEditor } from '@/components/ui/json-field'
import { Sheet, Tooltip, FacetFilter, SearchBox } from '@qorpe/ui'
import { Button } from '@qorpe/ui'
import { EmptyState } from '@qorpe/ui'
import { PickArt, StubsArt } from '@/components/ui/illustrations'
import { applyFilters, clearFacet, countSelected, type FacetDef, facetOptions, type Selections, toggleSelection } from '@/lib/faceted'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { ContextMenu, type ContextMenuAction } from '@/components/ui/context-menu'
import { StubEditorForm } from '@/components/stubs/stub-editor'

const EMPTY_SET = new Set<string>()
const FACETS: FacetDef<Stub>[] = [
  { id: 'method', get: (s) => s.method },
  { id: 'status', get: (s) => s.status },
  { id: 'protocol', get: (s) => s.protocol },
]

interface Tab { key: string; kind: 'stub' | 'new' | 'import'; stubId?: string; initial: 'form' | 'json'; prefillUrl?: string; pinned?: boolean }

// Pinned tabs stay at the start of the bar and survive the bulk close actions (Close Others/All/Right/Left).
const sortPinnedFirst = (tabs: Tab[]) => [...tabs.filter((x) => x.pinned), ...tabs.filter((x) => !x.pinned)]

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n))

export function StubsPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({ queryKey: ['stubs', tenant], queryFn: () => fetchStubs(tenant) })
  const stubs = useMemo(() => data?.stubs ?? [], [data])

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['stubs', tenant] })
    void queryClient.invalidateQueries({ queryKey: ['scenarios', tenant] })
  }, [queryClient, tenant])

  // Search + method/status facets narrow the tree.
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Selections>({})
  const filtering = search.trim().length > 0 || countSelected(selected) > 0
  const filtered = useMemo(() => applyFilters(stubs, FACETS, selected, search, (s) => `${s.name ?? ''} ${s.url}`), [stubs, selected, search])
  const tree = useMemo(() => buildStubTree(filtered), [filtered])
  const methodOptions = useMemo(() => facetOptions(stubs, (s) => s.method), [stubs])
  const statusOptions = useMemo(() => facetOptions(stubs, (s) => s.status), [stubs])
  const protocolOptions = useMemo(() => facetOptions(stubs, (s) => s.protocol), [stubs])

  // WebSocket message-mappings (G18-pre): a separate admin resource, listed under the stub tree.
  const { data: wsData } = useQuery({ queryKey: ['message-mappings', tenant], queryFn: () => fetchMessageMappings(tenant) })
  const wsMappings = wsData?.messageMappings ?? []
  const [wsOpen, setWsOpen] = useState<MessageMapping | null>(null)
  const removeWs = useCallback(async (id: string) => {
    await deleteMessageMapping(tenant, id)
    setWsOpen(null)
    void queryClient.invalidateQueries({ queryKey: ['message-mappings', tenant] })
    toast.success(t('channels.wsDeleted'))
  }, [tenant, queryClient, t])

  // Open tabs — persistent (localStorage) per tenant. Stub tabs restore across reloads; new/import tabs
  // are ephemeral. Every open tab's editor stays mounted (hidden when inactive), so unsaved edits
  // survive switching tabs.
  const storageKey = `ui.stubTabs.${tenant}`
  const [tabs, setTabs] = useState<Tab[]>([])
  const [active, setActive] = useState<string>('')
  const [dirty, setDirty] = useState<Record<string, boolean>>({})
  const restored = useRef('')

  useEffect(() => {
    if (isLoading || restored.current === tenant) return
    restored.current = tenant
    try {
      const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as { ids?: string[]; active?: string; pinned?: string[] } | null
      const pinnedIds = new Set(saved?.pinned ?? [])
      const ids = (saved?.ids ?? []).filter((id) => stubs.some((s) => s.id === id))
      const restoredTabs = sortPinnedFirst(ids.map<Tab>((id) => ({ key: `stub:${id}`, kind: 'stub', stubId: id, initial: 'form', pinned: pinnedIds.has(id) })))
      setTabs(restoredTabs)
      setActive(restoredTabs.some((x) => x.key === saved?.active) ? saved!.active! : restoredTabs[0]?.key ?? '')
    } catch { /* start clean */ }
  }, [isLoading, tenant, stubs, storageKey])

  useEffect(() => {
    const stubTabs = tabs.filter((x) => x.kind === 'stub' && x.stubId)
    localStorage.setItem(storageKey, JSON.stringify({
      ids: stubTabs.map((x) => x.stubId),
      active,
      pinned: stubTabs.filter((x) => x.pinned).map((x) => x.stubId),
    }))
  }, [tabs, active, storageKey])

  const openStub = useCallback((stub: Stub) => {
    const key = `stub:${stub.id}`
    setTabs((prev) => (prev.some((x) => x.key === key) ? prev : [...prev, { key, kind: 'stub', stubId: stub.id, initial: 'form' }]))
    setActive(key)
  }, [])

  const seq = useRef(0)
  const openBlank = useCallback((initial: 'form' | 'json', prefillUrl?: string) => {
    const key = `${initial === 'json' ? 'import' : 'new'}:${seq.current++}`
    setTabs((prev) => [...prev, { key, kind: initial === 'json' ? 'import' : 'new', initial, prefillUrl }])
    setActive(key)
  }, [])

  const closeTab = useCallback((key: string) => {
    setTabs((prev) => {
      const idx = prev.findIndex((x) => x.key === key)
      const next = prev.filter((x) => x.key !== key)
      setActive((cur) => (cur !== key ? cur : next[Math.max(0, idx - 1)]?.key ?? ''))
      return next
    })
    setDirty((d) => { const { [key]: _, ...rest } = d; return rest })
  }, [])

  // Bulk close (context menu actions). If the active tab is among the closed, focus falls back to
  // `fallback` — the surviving tab the action was invoked from, or the last survivor.
  const closeMany = useCallback((keys: string[], fallback?: string) => {
    if (keys.length === 0) return
    const drop = new Set(keys)
    setTabs((prev) => {
      const next = prev.filter((x) => !drop.has(x.key))
      setActive((cur) => (!drop.has(cur) ? cur : fallback ?? next[next.length - 1]?.key ?? ''))
      return next
    })
    setDirty((d) => Object.fromEntries(Object.entries(d).filter(([k]) => !drop.has(k))))
  }, [])

  const togglePin = useCallback((key: string) => {
    setTabs((prev) => sortPinnedFirst(prev.map((x) => (x.key === key ? { ...x, pinned: !x.pinned } : x))))
  }, [])

  // Right-click context menu on a tab — position + target tab key.
  const [menu, setMenu] = useState<{ x: number; y: number; key: string } | null>(null)

  const [searchParams, setSearchParams] = useSearchParams()
  useEffect(() => {
    if (searchParams.get('new') === '1') { openBlank('form'); setSearchParams({}, { replace: true }) }
    else if (searchParams.get('import') === '1') { openBlank('json'); setSearchParams({}, { replace: true }) }
    else if (searchParams.get('open')) {
      // Deep-link from the journal's "matched stub" reference: open that exact stub (by id, since many
      // stubs can share a URL and differ only by header/body matchers). Wait for the stub list so a
      // slow load isn't mistaken for a deleted stub.
      if (isLoading) return
      const stub = stubs.find((s) => s.id === searchParams.get('open'))
      if (stub) openStub(stub)
      else toast.warning(t('stubs.openMissing'))
      setSearchParams({}, { replace: true })
    }
  }, [searchParams, setSearchParams, openBlank, openStub, stubs, isLoading, t])

  // Exports a bare top-level array (no {"mappings":…} wrapper) — the import path (UI and host) accepts
  // both shapes, so an export always round-trips unmodified. When the tenant has environments, the
  // export switches to the wrapper and carries them (#198): re-importing then restores the keys the
  // stubs' {{key}} references depend on. `resolved` stays out — it is computed, not state.
  const exportAll = useCallback(async () => {
    const mappings = stubs.map((s) => s.raw).filter(Boolean)
    const { environments } = await fetchEnvironments(tenant)
    const bundle = environments.length > 0
      ? { mappings, environments: environments.map(({ key, activeValue, values }) => ({ key, activeValue, values })) }
      : mappings
    const blob = new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `mockifyr-${tenant}-stubs.json`
    a.click()
    URL.revokeObjectURL(url)
  }, [stubs, tenant])

  // Delete asks first: the trash icon only opens the confirmation dialog; the API call happens on
  // explicit confirm. Cancel/Escape/outside click leave the stub untouched.
  const [confirmDelete, setConfirmDelete] = useState<Stub | null>(null)
  const remove = useCallback(async (stub: Stub) => {
    setConfirmDelete(null)
    const { mock } = await deleteStub(tenant, stub.id)
    toast[mock ? 'message' : 'success'](mock ? t('editor.savedSample') : t('editor.deleted'))
    closeTab(`stub:${stub.id}`)
    refresh()
  }, [tenant, refresh, t, closeTab])

  const onTabSaved = useCallback((tab: Tab, saved: boolean) => {
    refresh()
    if (saved && tab.kind !== 'stub') closeTab(tab.key)
    else setDirty((d) => ({ ...d, [tab.key]: false }))
  }, [refresh, closeTab])

  // Expand/Collapse All — an epoch'd signal so every tree node applies the latest bulk action once,
  // while individual chevron toggles keep working afterwards.
  const [bulk, setBulk] = useState<{ open: boolean; n: number } | null>(null)
  const setAllOpen = useCallback((open: boolean) => setBulk((b) => ({ open, n: (b?.n ?? 0) + 1 })), [])

  const empty = !isLoading && stubs.length === 0

  // Resizable tree — one panel, split from the workspace by a draggable divider (min/max clamped).
  const [treeWidth, setTreeWidth] = useState(() => clamp(Number(localStorage.getItem('ui.stubTreeWidth')) || 288, 220, 560))
  useEffect(() => { localStorage.setItem('ui.stubTreeWidth', String(treeWidth)) }, [treeWidth])
  const onSplitterDown = useCallback((e: React.PointerEvent) => {
    e.preventDefault()
    const startX = e.clientX
    const startW = treeWidth
    const move = (ev: PointerEvent) => setTreeWidth(clamp(startW + (ev.clientX - startX), 220, 560))
    const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); document.body.style.cursor = '' }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    document.body.style.cursor = 'col-resize'
  }, [treeWidth])
  const activeStubId = tabs.find((x) => x.key === active)?.stubId

  return (
    <div className="flex h-full min-h-0 overflow-hidden">
      {/* Tree panel — the frame side: sits on the grey surface, so it reads as chrome around the white workspace */}
      <aside style={{ width: treeWidth }} className="flex shrink-0 flex-col overflow-hidden">
        <div className="flex flex-col gap-2.5 p-3">
          <div className="flex items-center gap-2">
            <h1 className="text-sm font-semibold">{t('nav.stubs')}</h1>
            <span className="rounded-full bg-muted px-1.5 text-[11px] tabular-nums text-muted-foreground">{stubs.length}</span>
            <div className="ms-auto flex gap-0.5">
              <Tooltip label={t('stubs.expandAll')}>
                <Button variant="ghost" size="iconSm" aria-label={t('stubs.expandAll')} onClick={() => setAllOpen(true)} disabled={!filtered.length}><ChevronsUpDown /></Button>
              </Tooltip>
              <Tooltip label={t('stubs.collapseAll')}>
                <Button variant="ghost" size="iconSm" aria-label={t('stubs.collapseAll')} onClick={() => setAllOpen(false)} disabled={!filtered.length}><ChevronsDownUp /></Button>
              </Tooltip>
              <Tooltip label={t('stubs.export')}>
                <Button variant="ghost" size="iconSm" aria-label={t('stubs.export')} onClick={() => void exportAll()} disabled={!stubs.length}><Download /></Button>
              </Tooltip>
              <Tooltip label={t('stubs.import')}>
                <Button variant="ghost" size="iconSm" aria-label={t('stubs.import')} onClick={() => openBlank('json')}><Import /></Button>
              </Tooltip>
              <Tooltip label={t('stubs.newStub')}>
                <Button variant="ghost" size="iconSm" aria-label={t('stubs.newStub')} onClick={() => openBlank('form')}><Plus /></Button>
              </Tooltip>
            </div>
          </div>
          {/* flex-none: the tree header is a flex column, so SearchBox's own flex-1 would collapse its height. */}
          <SearchBox value={search} onCommit={setSearch} label={t('stubs.filter')} clearLabel={t('common.clear')} placeholder={t('stubs.filter')} className="flex-none bg-background" />
          {/* One joined segmented group, flush with the search box: three equal cells, shared inner
              borders, rounded only at the outer ends — no gaps, no wrapping. */}
          <div className="grid w-full grid-cols-3">
            <FacetFilter compact className="w-full min-w-0 justify-center overflow-hidden rounded-e-none" label={t('stubs.method')} options={methodOptions} selected={selected.method ?? EMPTY_SET}
              onToggle={(v) => setSelected((s) => toggleSelection(s, 'method', v))} onClear={() => setSelected((s) => clearFacet(s, 'method'))} clearLabel={t('common.clear')} />
            <FacetFilter compact className="w-full min-w-0 justify-center overflow-hidden rounded-none border-x-0" label={t('stubs.status')} options={statusOptions} selected={selected.status ?? EMPTY_SET}
              onToggle={(v) => setSelected((s) => toggleSelection(s, 'status', v))} onClear={() => setSelected((s) => clearFacet(s, 'status'))} clearLabel={t('common.clear')} />
            <FacetFilter compact className="w-full min-w-0 justify-center overflow-hidden rounded-s-none" label={t('stubs.protocol')} options={protocolOptions} selected={selected.protocol ?? EMPTY_SET}
              onToggle={(v) => setSelected((s) => toggleSelection(s, 'protocol', v))} onClear={() => setSelected((s) => clearFacet(s, 'protocol'))} clearLabel={t('common.clear')} />
          </div>
        </div>
        <div className="scroll-area min-h-0 flex-1 overflow-y-auto px-1.5 pb-2">
          {isLoading ? (
            <div className="space-y-2 p-2">{Array.from({ length: 6 }).map((_, i) => <div key={i} className="h-6 animate-pulse rounded bg-muted" />)}</div>
          ) : filtered.length === 0 ? (
            <p className="p-3 text-sm text-faint">{filtering ? t('stubs.empty') : t('dashboard.getStarted')}</p>
          ) : (
            <TreeView node={tree} depth={0} basePath="" defaultOpen={filtered.length <= 40} forceOpen={filtering} bulk={bulk} activeStubId={activeStubId} onOpen={openStub} onDelete={setConfirmDelete} onAddUnder={(url) => openBlank('form', url)} />
          )}
          {wsMappings.length > 0 && (
            <div className="mt-3 border-t border-border/70 pt-2">
              <div className="flex items-center gap-1.5 px-2 pb-1 text-[11px] font-semibold uppercase tracking-wide text-faint">
                {t('channels.wsSection')}
                <span className="rounded-full bg-muted px-1.5 tabular-nums">{wsMappings.length}</span>
              </div>
              {wsMappings.map((m) => (
                <div key={m.id} onClick={() => setWsOpen(m)}
                  className="group flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1 text-[13px] transition-colors hover:bg-muted/60">
                  <ProtocolChip protocol="websocket" />
                  <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{m.trigger}</span>
                  <span role="button" tabIndex={-1} aria-label="Delete" onClick={(e) => { e.stopPropagation(); void removeWs(m.id) }}
                    className="shrink-0 rounded p-0.5 text-faint opacity-0 transition-opacity hover:bg-danger-bg hover:text-danger group-hover:opacity-100"><Trash2 className="size-3.5" /></span>
                </div>
              ))}
            </div>
          )}
        </div>
        {data?.mock && <div className="p-2 text-center"><span className="rounded-full border border-warning-border bg-warning-bg px-2 py-0.5 text-[11px] font-medium text-warning">{t('stubs.sample')}</span></div>}
      </aside>

      {/* Splitter — a hairline that firms up subtly on hover/drag (no loud highlight) */}
      <div onPointerDown={onSplitterDown} className="group relative z-10 w-px shrink-0 cursor-col-resize bg-border" role="separator" aria-orientation="vertical">
        <div className="absolute inset-y-0 -inset-x-1 transition-colors group-hover:bg-border-strong/50" />
      </div>

      {/* Workspace — the white editing canvas, lifted above the grey frame */}
      <section className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background">
        {tabs.length === 0 ? (
          <EmptyWorkspace t={t} empty={empty} onNew={() => openBlank('form')} onImport={() => openBlank('json')} />
        ) : (
          <>
            <div className="scroll-area flex items-stretch overflow-x-auto border-b border-border bg-muted/30">
              {tabs.map((tab) => {
                const stub = tab.stubId ? stubs.find((s) => s.id === tab.stubId) : null
                const label = stub ? (stub.name || stub.url) : tab.kind === 'import' ? t('editor.importTitle') : t('editor.newTitle')
                return (
                  <button key={tab.key} onClick={() => setActive(tab.key)}
                    // Middle-click closes (browser-tab convention); mousedown is prevented too so the
                    // browser's autoscroll/paste-on-middle-click never kicks in over a tab.
                    onMouseDown={(e) => { if (e.button === 1) e.preventDefault() }}
                    onAuxClick={(e) => { if (e.button === 1) { e.preventDefault(); closeTab(tab.key) } }}
                    onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, key: tab.key }) }}
                    className={cn('group flex max-w-[220px] shrink-0 items-center gap-2 border-e border-border px-3 py-2 text-[12.5px] transition-colors',
                      active === tab.key ? 'border-b-2 border-b-primary bg-background text-foreground' : 'text-muted-foreground hover:text-foreground')}>
                    {tab.pinned && <Pin className="size-3 shrink-0 text-faint" aria-label={t('tabs.pin')} />}
                    {stub && <MethodChip method={stub.method} />}
                    {stub && <ProtocolChip protocol={stub.protocol} />}
                    <span className="truncate">{label}</span>
                    {dirty[tab.key] && <span className="size-1.5 shrink-0 rounded-full bg-primary" aria-label="unsaved" />}
                    <span role="button" tabIndex={-1} aria-label={t('editor.cancel')} onClick={(e) => { e.stopPropagation(); closeTab(tab.key) }}
                      className="rounded p-0.5 text-faint hover:bg-muted hover:text-foreground"><X className="size-3.5" /></span>
                  </button>
                )
              })}
              <button onClick={() => openBlank('form')} aria-label={t('stubs.newStub')} className="shrink-0 px-3 text-muted-foreground hover:text-foreground"><Plus className="size-4" /></button>
            </div>
            {tabs.map((tab) => (
              <div key={tab.key} className={cn('min-h-0 flex-1', active === tab.key ? 'flex flex-col' : 'hidden')}>
                {tab.kind === 'new' ? (
                  <NewStubWorkspace
                    active={active === tab.key}
                    prefillUrl={tab.prefillUrl}
                    onSaved={(saved) => onTabSaved(tab, saved)}
                    onDirtyChange={(d) => setDirty((prev) => (prev[tab.key] === d ? prev : { ...prev, [tab.key]: d }))}
                  />
                ) : (
                  <StubEditorForm
                    editing={tab.stubId ? stubs.find((s) => s.id === tab.stubId) ?? null : null}
                    initialTab={tab.initial}
                    prefillUrl={tab.prefillUrl}
                    active={active === tab.key}
                    onSaved={(saved) => onTabSaved(tab, saved)}
                    onDirtyChange={(d) => setDirty((prev) => (prev[tab.key] === d ? prev : { ...prev, [tab.key]: d }))}
                  />
                )}
              </div>
            ))}
          </>
        )}
      </section>

      {menu && <TabContextMenu menu={menu} tabs={tabs} onClose={() => setMenu(null)} closeTab={closeTab} closeMany={closeMany} togglePin={togglePin} />}

      {/* WebSocket message-mapping detail: registration JSON as posted, read-only, with delete. */}
      <Sheet open={wsOpen !== null} onOpenChange={(o) => { if (!o) setWsOpen(null) }} title={t('channels.wsDetail')} description={wsOpen?.trigger}>
        {wsOpen && <JsonEditor value={JSON.stringify(wsOpen.raw, null, 2)} readOnly className="min-h-64" />}
        <div className="mt-4 flex justify-end gap-2 border-t border-border pt-3">
          <Button variant="ghost" onClick={() => setWsOpen(null)}>{t('editor.cancel')}</Button>
          <Button variant="danger" onClick={() => { if (wsOpen) void removeWs(wsOpen.id) }}>{t('stubs.delete')}</Button>
        </div>
      </Sheet>

      <ConfirmDialog
        open={confirmDelete !== null}
        onOpenChange={(o) => { if (!o) setConfirmDelete(null) }}
        title={t('stubs.deleteConfirmTitle')}
        body={t('stubs.deleteConfirmBody')}
        confirmLabel={t('stubs.delete')}
        cancelLabel={t('editor.cancel')}
        destructive
        onConfirm={() => { if (confirmDelete) void remove(confirmDelete) }}
      >
        {confirmDelete && (
          <div className="mt-3 flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2 text-sm">
            <MethodChip method={confirmDelete.method} />
            <span className="min-w-0 truncate font-mono text-[12.5px]">{confirmDelete.url}</span>
            {confirmDelete.name && <span className="ms-auto shrink-0 truncate text-muted-foreground">{confirmDelete.name}</span>}
          </div>
        )}
      </ConfirmDialog>
    </div>
  )
}

// The tab bar's right-click menu: single/bulk close (pinned tabs survive the bulk actions) + pin toggle.
function TabContextMenu({ menu, tabs, onClose, closeTab, closeMany, togglePin }: {
  menu: { x: number; y: number; key: string }
  tabs: Tab[]
  onClose: () => void
  closeTab: (key: string) => void
  closeMany: (keys: string[], fallback?: string) => void
  togglePin: (key: string) => void
}) {
  const { t } = useTranslation()
  const idx = tabs.findIndex((x) => x.key === menu.key)
  const target = tabs[idx]
  if (!target) return null
  const closable = (list: Tab[]) => list.filter((x) => !x.pinned && x.key !== menu.key).map((x) => x.key)
  const others = closable(tabs)
  const right = closable(tabs.slice(idx + 1))
  const left = closable(tabs.slice(0, idx))
  const actions: ContextMenuAction[] = [
    { label: t('tabs.close'), icon: <X className="size-3.5" />, onSelect: () => closeTab(menu.key) },
    { label: t('tabs.closeOthers'), disabled: others.length === 0, onSelect: () => closeMany(others, menu.key) },
    { label: t('tabs.closeRight'), disabled: right.length === 0, onSelect: () => closeMany(right, menu.key) },
    { label: t('tabs.closeLeft'), disabled: left.length === 0, onSelect: () => closeMany(left, menu.key) },
    { label: t('tabs.closeAll'), disabled: tabs.every((x) => x.pinned), onSelect: () => closeMany(tabs.filter((x) => !x.pinned).map((x) => x.key)) },
    {
      label: target.pinned ? t('tabs.unpin') : t('tabs.pin'),
      icon: target.pinned ? <PinOff className="size-3.5" /> : <Pin className="size-3.5" />,
      separatorBefore: true,
      onSelect: () => togglePin(menu.key),
    },
  ]
  return <ContextMenu x={menu.x} y={menu.y} actions={actions} onClose={onClose} />
}

interface TreeProps {
  node: StubTreeNode
  depth: number
  basePath: string
  defaultOpen: boolean
  forceOpen: boolean
  bulk: { open: boolean; n: number } | null
  activeStubId?: string
  onOpen: (s: Stub) => void
  onDelete: (s: Stub) => void
  onAddUnder: (url: string) => void
}
type Shared = Omit<TreeProps, 'node' | 'depth'>

// Path → Method → Case. A node renders its sub-path folders, then the method buckets for stubs that end
// at this node; each method expands to its individual cases (one stub each, by status + name).
function TreeView({ node, depth, basePath, ...shared }: TreeProps) {
  const groups = [...node.groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  const methods = [...node.methods.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  return (
    <div className="space-y-0.5">
      {groups.map(([seg, child]) => (
        <Folder key={`g:${seg}`} seg={seg} child={child} depth={depth} basePath={`${basePath}/${seg}`} {...shared} />
      ))}
      {methods.map(([method, stubs]) => (
        <MethodGroup key={`m:${method}`} method={method} stubs={stubs} basePath={basePath} {...shared} />
      ))}
    </div>
  )
}

// One tree node's expanded state, wired to the sidebar's Expand/Collapse All signal. Children only
// mount once their parent expands, so a node born after a bulk action seeds from that action's state —
// this is what makes Expand All reach every depth. Individual chevron toggles keep working in between;
// the epoch guard stops a stale signal from clobbering them on re-render.
function useOpenState({ bulk, defaultOpen }: Pick<Shared, 'bulk' | 'defaultOpen'>) {
  const [open, setOpen] = useState(bulk ? bulk.open : defaultOpen)
  const applied = useRef(bulk?.n ?? 0)
  useEffect(() => {
    if (bulk && bulk.n !== applied.current) {
      applied.current = bulk.n
      setOpen(bulk.open)
    }
  }, [bulk])
  return [open, setOpen] as const
}

// A subtle connector line + breathing room wraps every nested level so the hierarchy reads at a glance.
// The line sits at 15px — row padding (8) + half a chevron (7) — so it drops straight under the parent's
// chevron. Padding is kept tight so 3–4 levels deep don't eat the panel's width.
function Nested({ children }: { children: React.ReactNode }) {
  return <div className="ms-[15px] mt-1 space-y-0.5 border-s border-border/70 ps-1.5">{children}</div>
}

function Folder({ seg, child, depth, basePath, ...shared }: Shared & { seg: string; child: StubTreeNode; depth: number }) {
  const [open, setOpen] = useOpenState(shared)
  const expanded = shared.forceOpen || open
  return (
    <div>
      <div onClick={() => setOpen((o) => !o)}
        className="group flex cursor-pointer items-center gap-1.5 rounded-lg px-2 py-1 text-[13px] text-muted-foreground transition-colors hover:bg-muted/60">
        <ChevronRight className={cn('size-3.5 shrink-0 transition-transform', expanded && 'rotate-90')} />
        <span className="min-w-0 flex-1 truncate font-medium">/{seg}</span>
        <span role="button" tabIndex={-1} aria-label="Add stub here" onClick={(e) => { e.stopPropagation(); shared.onAddUnder(`${basePath}/`) }}
          className="shrink-0 rounded p-0.5 text-faint opacity-0 transition-opacity hover:bg-muted hover:text-foreground group-hover:opacity-100"><Plus className="size-3.5" /></span>
        <span className="shrink-0 text-[11px] tabular-nums text-faint group-hover:hidden">{countStubs(child)}</span>
      </div>
      {expanded && <Nested><TreeView node={child} depth={depth + 1} basePath={basePath} {...shared} /></Nested>}
    </div>
  )
}

function MethodGroup({ method, stubs, basePath, ...shared }: Shared & { method: string; stubs: Stub[] }) {
  const [open, setOpen] = useOpenState(shared)
  const expanded = shared.forceOpen || open
  // Order cases by status code, then name — so the happy path (2xx) reads first.
  const cases = [...stubs].sort((a, b) => (a.responseStatus ?? 0) - (b.responseStatus ?? 0) || (a.name ?? '').localeCompare(b.name ?? ''))
  return (
    <div>
      <div onClick={() => setOpen((o) => !o)}
        className="group flex cursor-pointer items-center gap-1.5 rounded-lg px-2 py-1 text-[13px] transition-colors hover:bg-muted/60">
        <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform', expanded && 'rotate-90')} />
        <MethodChip method={method} />
        <span className="min-w-0 flex-1" />
        <span role="button" tabIndex={-1} aria-label="Add case here" onClick={(e) => { e.stopPropagation(); shared.onAddUnder(basePath || '/') }}
          className="shrink-0 rounded p-0.5 text-faint opacity-0 transition-opacity hover:bg-muted hover:text-foreground group-hover:opacity-100"><Plus className="size-3.5" /></span>
        <span className="shrink-0 text-[11px] tabular-nums text-faint group-hover:hidden">{stubs.length}</span>
      </div>
      {expanded && <Nested>{cases.map((stub) => <CaseLeaf key={stub.id} stub={stub} active={stub.id === shared.activeStubId} onOpen={shared.onOpen} onDelete={shared.onDelete} />)}</Nested>}
    </div>
  )
}

function CaseLeaf({ stub, active, onOpen, onDelete }: { stub: Stub; active: boolean; onOpen: (s: Stub) => void; onDelete: (s: Stub) => void }) {
  const { t } = useTranslation()
  return (
    <div onClick={() => onOpen(stub)}
      className={cn('group flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1 text-[13px] transition-colors',
        active ? 'bg-muted font-medium text-foreground' : 'text-foreground hover:bg-muted/60')}>
      <StatusCode code={stub.responseStatus} />
      <ProtocolChip protocol={stub.protocol} />
      <CryptoChips encrypted={stub.encrypted} signed={stub.signed}
        encryptedLabel={t('crypto.encrypted')} signedLabel={t('crypto.signed')} />
      <span className="min-w-0 flex-1 truncate">{stub.name?.trim() || t('stubs.untitledCase')}</span>
      <span role="button" tabIndex={-1} aria-label="Delete" onClick={(e) => { e.stopPropagation(); onDelete(stub) }}
        className="shrink-0 rounded p-0.5 text-faint opacity-0 transition-opacity hover:bg-danger-bg hover:text-danger group-hover:opacity-100"><Trash2 className="size-3.5" /></span>
    </div>
  )
}

function EmptyWorkspace({ t, empty, onNew, onImport }: { t: (k: string) => string; empty: boolean; onNew: () => void; onImport: () => void }) {
  return (
    <EmptyState
      art={empty ? <StubsArt /> : <PickArt />}
      title={empty ? t('dashboard.getStarted') : t('stubs.pickHint')}
      body={empty ? t('dashboard.getStartedHint') : t('stubs.pickHintBody')}
      action={empty ? (
        <>
          <Button variant="primary" size="sm" onClick={onNew}><Plus />{t('stubs.newStub')}</Button>
          <Button variant="outline" size="sm" onClick={onImport}><Download />{t('stubs.import')}</Button>
        </>
      ) : undefined}
    />
  )
}
