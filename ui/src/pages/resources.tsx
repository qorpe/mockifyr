import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ChevronLeft, ChevronRight, Database, Eraser, Pencil, Plus, Sprout, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import {
  deleteResourceDocument, fetchResourceCollections, fetchResourceDocuments, putResourceDocument,
  resetResources, seedResourceCollection, type ResourceDoc,
} from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button } from '@qorpe/ui'
import { Input, Label, Textarea } from '@/components/ui/field'
import { EmptyState } from '@qorpe/ui'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { JsonEditor } from '@/components/ui/json-editor'

const PAGE_SIZE = 50

// Mirrors the server's ResourceGuards: 1..64 of [A-Za-z0-9_-] for both collections and ids.
const NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/

/**
 * Sandbox resources (G19e, ADR 0011): the data plane of the integration sandbox. Collections on the
 * left, a paged document table on the right; documents are opaque JSON edited verbatim. Everything
 * is tenant-scoped through the admin header — one tenant's sandbox is invisible to another.
 */
export function ResourcesPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()

  const collectionsQuery = useQuery({
    queryKey: ['resources', tenant],
    queryFn: () => fetchResourceCollections(tenant),
  })
  const collections = useMemo(() => collectionsQuery.data?.collections ?? [], [collectionsQuery.data])

  const [selected, setSelected] = useState<string | null>(null)
  const [offset, setOffset] = useState(0)

  // Keep the selection valid: default to the first collection, drop it when it disappears
  // (reset/delete), and clear everything on a tenant switch (the #199 lesson).
  useEffect(() => { setSelected(null); setOffset(0) }, [tenant])
  useEffect(() => {
    if (collectionsQuery.isLoading) return
    if (selected === null || !collections.some((c) => c.name === selected)) {
      setSelected(collections[0]?.name ?? null)
      setOffset(0)
    }
  }, [collections, collectionsQuery.isLoading, selected])

  const documentsQuery = useQuery({
    queryKey: ['resources', tenant, selected, offset],
    queryFn: () => fetchResourceDocuments(tenant, selected!, PAGE_SIZE, offset),
    enabled: selected !== null,
  })
  const documents = documentsQuery.data?.documents ?? []
  const total = documentsQuery.data?.total ?? 0

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['resources', tenant] })

  // ---- dialogs ------------------------------------------------------------------------------

  // Document editor: null = closed; a ResourceDoc = editing; 'new' = creating (collection editable
  // only when it is the very first document of a brand-new collection).
  const [editing, setEditing] = useState<ResourceDoc | 'new' | 'new-collection' | null>(null)
  const [docCollection, setDocCollection] = useState('')
  const [docId, setDocId] = useState('')
  const [docBody, setDocBody] = useState('')
  const [seeding, setSeeding] = useState(false)
  const [seedJson, setSeedJson] = useState('')
  const [confirm, setConfirm] = useState<{ kind: 'doc'; id: string } | { kind: 'collection' } | { kind: 'all' } | null>(null)
  useEffect(() => { setEditing(null); setSeeding(false); setConfirm(null) }, [tenant])

  const openEditor = (doc?: ResourceDoc, newCollection = false) => {
    setEditing(doc ?? (newCollection ? 'new-collection' : 'new'))
    setDocCollection(doc?.collection ?? (newCollection ? '' : selected ?? ''))
    setDocId(doc?.id ?? '')
    setDocBody(doc ? JSON.stringify(doc.body, null, 2) : '{\n  \n}')
  }

  const bodyValid = useMemo(() => {
    try { JSON.parse(docBody); return true } catch { return false }
  }, [docBody])
  const collectionInvalid = !NAME_PATTERN.test(docCollection.trim())
  const idInvalid = !NAME_PATTERN.test(docId.trim())

  const save = useMutation({
    mutationFn: () => putResourceDocument(tenant, docCollection.trim(), docId.trim(), docBody),
    onSuccess: () => {
      toast.success(t('res.saved'))
      setEditing(null)
      if (editing === 'new-collection') setSelected(docCollection.trim())
      void invalidate()
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const remove = useMutation({
    mutationFn: (id: string) => deleteResourceDocument(tenant, selected!, id),
    onSuccess: () => { toast.success(t('res.deleted')); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  const reset = useMutation({
    mutationFn: (collection?: string) => resetResources(tenant, collection),
    onSuccess: () => { toast.success(t('res.resetDone')); setOffset(0); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  const seed = useMutation({
    mutationFn: () => seedResourceCollection(tenant, selected!, seedJson),
    onSuccess: () => { toast.success(t('res.seeded')); setSeeding(false); setSeedJson(''); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  const seedValid = useMemo(() => {
    try { return Array.isArray(JSON.parse(seedJson)) } catch { return false }
  }, [seedJson])

  const page = Math.floor(offset / PAGE_SIZE) + 1
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <div className="mx-auto max-w-[1360px]">
      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.resources')}</h1>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{t('res.subtitle')}</p>
        <div className="mt-3 flex flex-wrap justify-end gap-2">
          {collections.length > 0 && (
            <Button variant="outline" onClick={() => setConfirm({ kind: 'all' })}><Eraser />{t('res.resetAll')}</Button>
          )}
          <Button variant="primary" onClick={() => openEditor(undefined, true)}><Plus />{t('res.newCollection')}</Button>
        </div>
      </header>

      {collections.length === 0 ? (
        <div className="rounded-2xl border border-border bg-background shadow-surface">
          <EmptyState
            art={<Database className="size-10 text-faint" />}
            title={t('res.empty')} body={t('res.emptyHint')} className="py-16"
          />
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[260px_minmax(0,1fr)]">
          {/* Collections rail */}
          <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface lg:self-start">
            <div className="border-b border-border bg-muted/40 px-4 py-2.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              {t('res.collections')}
            </div>
            <ul className="max-h-[520px] overflow-y-auto p-2">
              {collections.map((c) => (
                <li key={c.name}>
                  <button
                    onClick={() => { setSelected(c.name); setOffset(0) }}
                    className={cn('mb-0.5 flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-sm transition-colors',
                      selected === c.name ? 'bg-sidebar-accent font-semibold text-sidebar-accent-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground')}
                  >
                    <Database className="size-4 shrink-0" />
                    <span className="min-w-0 flex-1 truncate text-start font-mono text-[12.5px]">{c.name}</span>
                    <span className="rounded-md bg-muted px-1.5 py-0.5 text-[11px] tabular-nums text-muted-foreground">{c.count}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>

          {/* Documents */}
          <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
            <div className="flex flex-wrap items-center gap-2 border-b border-border bg-muted/40 px-4 py-2">
              <span className="font-mono text-[12.5px] font-semibold">{selected}</span>
              <span className="text-xs tabular-nums text-muted-foreground">{t('res.total', { count: total })}</span>
              <div className="ms-auto flex gap-1.5">
                <Button variant="ghost" size="sm" onClick={() => { setSeedJson(''); setSeeding(true) }}><Sprout />{t('res.seed')}</Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirm({ kind: 'collection' })}><Eraser />{t('res.reset')}</Button>
                <Button variant="outline" size="sm" onClick={() => openEditor()}><Plus />{t('res.newDoc')}</Button>
              </div>
            </div>

            <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr>
                  {[t('res.docId'), t('res.preview'), t('res.version'), t('res.updated'), ''].map((h, i) => (
                    <th key={i} className="border-b border-border px-4 py-2 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {documents.map((doc) => (
                  <tr key={doc.id} className="border-b border-border last:border-b-0">
                    <td className="whitespace-nowrap px-4 py-2.5 font-mono text-[12.5px] font-medium">{doc.id}</td>
                    <td className="max-w-0 px-4 py-2.5">
                      <span className="block truncate font-mono text-[12px] text-muted-foreground">{JSON.stringify(doc.body)}</span>
                    </td>
                    <td className="whitespace-nowrap px-4 py-2.5 text-xs tabular-nums text-muted-foreground">v{doc.version}</td>
                    <td className="whitespace-nowrap px-4 py-2.5 text-xs tabular-nums text-muted-foreground">{new Date(doc.updatedAt).toLocaleString()}</td>
                    <td className="w-20 px-4 py-2.5">
                      <div className="flex justify-end gap-1">
                        <Button variant="ghost" size="iconSm" aria-label={t('stubs.edit')} onClick={() => openEditor(doc)}><Pencil /></Button>
                        <Button variant="ghost" size="iconSm" aria-label={t('stubs.delete')} onClick={() => setConfirm({ kind: 'doc', id: doc.id })}><Trash2 /></Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            </div>
            {documents.length === 0 && !documentsQuery.isLoading && (
              <EmptyState art={<Database className="size-8 text-faint" />} title={t('res.noDocs')} className="py-10" />
            )}

            {pages > 1 && (
              <div className="flex items-center justify-end gap-2 border-t border-border px-4 py-2 text-xs tabular-nums text-muted-foreground">
                <span>{page} / {pages}</span>
                <Button variant="ghost" size="iconSm" aria-label={t('res.prev')} disabled={offset === 0} onClick={() => setOffset(offset - PAGE_SIZE)}><ChevronLeft className="rtl:rotate-180" /></Button>
                <Button variant="ghost" size="iconSm" aria-label={t('res.next')} disabled={offset + PAGE_SIZE >= total} onClick={() => setOffset(offset + PAGE_SIZE)}><ChevronRight className="rtl:rotate-180" /></Button>
              </div>
            )}
          </div>
        </div>
      )}

      <p className="mt-4 text-sm leading-relaxed text-muted-foreground">{t('res.hint')}</p>

      {/* Document editor */}
      {editing !== null && (
        <ConfirmDialog
          open onOpenChange={(o) => { if (!o) setEditing(null) }}
          title={editing === 'new' || editing === 'new-collection' ? t('res.newDoc') : t('res.editDoc')}
          confirmLabel={t('env.save')} cancelLabel={t('editor.cancel')}
          onConfirm={() => { if (bodyValid && !collectionInvalid && !idInvalid) save.mutate() }}
        >
          <div className="mt-4 space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <Label>{t('res.collection')}</Label>
                <Input value={docCollection} onChange={(e) => setDocCollection(e.target.value)} placeholder="orders"
                  className="font-mono" disabled={editing !== 'new-collection'} autoFocus={editing === 'new-collection'} />
              </div>
              <div>
                <Label>{t('res.docId')}</Label>
                <Input value={docId} onChange={(e) => setDocId(e.target.value)} placeholder="ord-1001"
                  className="font-mono" disabled={editing !== 'new' && editing !== 'new-collection'} autoFocus={editing === 'new'} />
              </div>
            </div>
            <div>
              <Label>{t('res.docBody')}</Label>
              <JsonEditor value={docBody} onChange={setDocBody} minimal className="mt-1 max-h-72 overflow-y-auto rounded-lg border border-border" />
            </div>
            {(docCollection.trim() || docId.trim()) && (collectionInvalid || idInvalid) && (
              <p className="text-xs text-danger">{t('res.invalidName')}</p>
            )}
            {docBody.trim() && !bodyValid && <p className="text-xs text-danger">{t('res.invalidJson')}</p>}
          </div>
        </ConfirmDialog>
      )}

      {/* Seed dialog */}
      <ConfirmDialog
        open={seeding} onOpenChange={setSeeding}
        title={t('res.seedTitle', { collection: selected ?? '' })} body={t('res.seedHint')}
        confirmLabel={t('res.seed')} cancelLabel={t('editor.cancel')}
        onConfirm={() => { if (seedValid) seed.mutate() }}
      >
        <div className="mt-3">
          <Textarea
            value={seedJson} onChange={(e) => setSeedJson(e.target.value)} rows={8} autoFocus
            placeholder={'[\n  { "id": "ord-1001", "status": "pending" },\n  { "status": "shipped" }\n]'}
            className="font-mono text-[12.5px]"
          />
          {seedJson.trim() && !seedValid && <p className="mt-1.5 text-xs text-danger">{t('res.seedInvalid')}</p>}
        </div>
      </ConfirmDialog>

      <ConfirmDialog
        open={confirm !== null} onOpenChange={(o) => { if (!o) setConfirm(null) }}
        destructive
        title={confirm?.kind === 'doc' ? t('res.deleteTitle')
          : confirm?.kind === 'collection' ? t('res.resetTitle', { collection: selected ?? '' })
            : t('res.resetAllTitle')}
        body={confirm?.kind === 'doc' ? t('res.deleteBody')
          : confirm?.kind === 'collection' ? t('res.resetBody')
            : t('res.resetAllBody')}
        confirmLabel={confirm?.kind === 'doc' ? t('stubs.delete') : t('res.reset')} cancelLabel={t('editor.cancel')}
        onConfirm={() => {
          if (confirm?.kind === 'doc') remove.mutate(confirm.id)
          else if (confirm?.kind === 'collection') reset.mutate(selected ?? undefined)
          else reset.mutate(undefined)
          setConfirm(null)
        }}
      />
    </div>
  )
}
