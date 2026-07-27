import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Check, Globe, Pencil, Plus, Trash2, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import {
  ENV_KEY_PATTERN, type EnvironmentKey, type EnvironmentValue, isReservedKey, migrateLegacyEnvironments,
} from '@/lib/environments'
import {
  deleteEnvironmentKey, fetchEnvironments, putEnvironmentKey, setEnvironmentActiveValue,
} from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button } from '@/components/ui/button'
import { Input, Label } from '@/components/ui/field'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'

/**
 * Environments (#165, #166): tenant-scoped keys, each with several named values and one active.
 * A stub referencing {{key}} resolves to the active value at the moment it is served — so switching
 * the active value below changes what every stub using that key returns, with no re-save.
 *
 * Keys live on the server (/__admin/environments) and are scoped to the tenant in the header, which
 * is what makes one tenant's values invisible to another.
 */
export function EnvironmentsPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()

  const { data, isLoading } = useQuery({
    queryKey: ['environments', tenant],
    queryFn: () => fetchEnvironments(tenant),
  })
  const keys = useMemo(() => data?.environments ?? [], [data])
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['environments', tenant] })

  // One-time migration of environments created under the old localStorage model (#157). Runs per
  // tenant because the legacy data carried none — see migrateLegacyEnvironments.
  useEffect(() => {
    if (isLoading || data?.mock) return
    void migrateLegacyEnvironments(tenant, async (tn, key) => (await putEnvironmentKey(tn, key)).ok)
      .then((count) => {
        if (count > 0) {
          toast.success(t('env.migrated', { count }))
          void invalidate()
        }
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenant, isLoading, data?.mock])

  const [editing, setEditing] = useState<string | null>(null) // key being edited, '' = new
  const [keyName, setKeyName] = useState('')
  const [values, setValues] = useState<EnvironmentValue[]>([{ name: 'default', value: '' }])
  const [activeValue, setActiveValue] = useState('default')
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)

  // Switching tenants closes the edit panel and any pending delete confirmation (#199): both hold a
  // key from the previous tenant, which would otherwise render as if it belonged to the new one.
  useEffect(() => { setEditing(null); setConfirmDelete(null) }, [tenant])

  const startEdit = (entry?: EnvironmentKey) => {
    setEditing(entry?.key ?? '')
    setKeyName(entry?.key ?? '')
    setValues(entry ? entry.values.map((v) => ({ ...v })) : [{ name: 'default', value: '' }])
    setActiveValue(entry?.activeValue ?? 'default')
  }

  const trimmedKey = keyName.trim()
  const keyInvalid = trimmedKey.length > 0 && !ENV_KEY_PATTERN.test(trimmedKey)
  const keyReserved = trimmedKey.length > 0 && isReservedKey(trimmedKey)
  const keyTaken = editing !== null && trimmedKey !== editing && keys.some((k) => k.key === trimmedKey)
  const filled = values.filter((v) => v.name.trim() && v.value.trim())
  const duplicateValue = new Set(filled.map((v) => v.name.trim())).size !== filled.length
  const activeMissing = filled.length > 0 && !filled.some((v) => v.name.trim() === activeValue)
  const blocked = !trimmedKey || filled.length === 0 || keyInvalid || keyReserved || keyTaken || duplicateValue || activeMissing

  const save = useMutation({
    mutationFn: async () => {
      // A rename is a create + delete: the key IS the identity, so the old row must go.
      const result = await putEnvironmentKey(tenant, {
        key: trimmedKey,
        activeValue,
        resolved: null,
        // Trailing slashes are stripped so {{key}}/path composes without a double slash.
        values: filled.map((v) => ({ name: v.name.trim(), value: v.value.trim().replace(/\/$/, '') })),
      })
      if (!result.ok) throw new Error(result.message ?? result.error ?? 'failed')
      if (editing && editing !== trimmedKey) await deleteEnvironmentKey(tenant, editing)
    },
    onSuccess: () => { toast.success(t('env.saved')); setEditing(null); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  const activate = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => setEnvironmentActiveValue(tenant, key, value),
    onSuccess: () => void invalidate(),
  })

  const remove = useMutation({
    mutationFn: (key: string) => deleteEnvironmentKey(tenant, key),
    onSuccess: () => { toast.success(t('env.deleted')); void invalidate() },
  })

  const error = keyInvalid ? t('env.invalidName')
    : keyReserved ? t('env.reservedName')
      : keyTaken ? t('env.nameTaken')
        : duplicateValue ? t('env.duplicateValue')
          : activeMissing ? t('env.activeMissing')
            : null

  return (
    <div className="mx-auto max-w-[1100px]">
      {/* Full-width description with the action tucked under its end (#200) — the side-by-side split
          left the text cramped in half the row while the button's half sat empty. */}
      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.environments')}</h1>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{t('env.subtitle')}</p>
        <div className="mt-3 flex justify-end">
          <Button variant="primary" onClick={() => startEdit()}><Plus />{t('env.add')}</Button>
        </div>
      </header>

      <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
        {keys.length === 0 && editing === null ? (
          <EmptyState art={<Globe className="size-10 text-faint" />} title={t('env.empty')} className="py-16" />
        ) : (
          <table className="w-full border-collapse">
            <thead>
              <tr>
                {[t('env.name'), t('env.values'), t('env.resolved'), ''].map((h, i) => (
                  <th key={i} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {keys.map((entry) => (
                <tr key={entry.key} className="border-b border-border align-top">
                  <td className="px-4 py-3 font-mono text-[12.5px] font-medium whitespace-nowrap">{`{{${entry.key}}}`}</td>
                  <td className="px-4 py-3">
                    {/* One selector per key — each key's active value is independent of every other. */}
                    <div className="flex flex-wrap gap-1.5">
                      {entry.values.map((v) => (
                        <button
                          key={v.name}
                          onClick={() => activate.mutate({ key: entry.key, value: v.name })}
                          title={v.value}
                          className={cn('inline-flex items-center gap-1.5 rounded-lg border px-2 py-1 font-mono text-[12px] transition-colors',
                            entry.activeValue === v.name
                              ? 'border-success bg-success/10 text-success'
                              : 'border-border text-muted-foreground hover:border-muted-foreground')}
                        >
                          {entry.activeValue === v.name && <Check className="size-3" />}
                          {v.name}
                        </button>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-3 font-mono text-[12.5px] text-muted-foreground break-all">
                    {entry.resolved ?? <span className="text-danger">{t('env.unresolved')}</span>}
                  </td>
                  <td className="w-24 px-4 py-3">
                    <div className="flex justify-end gap-1">
                      <Button variant="ghost" size="iconSm" aria-label={t('stubs.edit')} onClick={() => startEdit(entry)}><Pencil /></Button>
                      <Button variant="ghost" size="iconSm" aria-label={t('stubs.delete')} onClick={() => setConfirmDelete(entry.key)}><Trash2 /></Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {editing !== null && (
          <div className="border-t border-border bg-muted/30 p-4">
            <div className="max-w-[240px]">
              <Label>{t('env.name')}</Label>
              <Input value={keyName} onChange={(e) => setKeyName(e.target.value)} placeholder="baseUrl" className="font-mono" autoFocus />
            </div>

            <Label className="mt-4 block">{t('env.values')}</Label>
            <div className="mt-1 space-y-2">
              {values.map((v, i) => (
                <div key={i} className="grid grid-cols-[28px_180px_minmax(0,1fr)_auto] items-center gap-2">
                  {/* Radio, not checkbox: exactly one value is active per key. */}
                  <button
                    onClick={() => setActiveValue(v.name.trim())}
                    aria-label={t('env.activate')} title={t('env.activate')}
                    className={cn('flex size-5 items-center justify-center rounded-full border transition-colors',
                      activeValue === v.name.trim() ? 'border-success bg-success text-white' : 'border-border hover:border-muted-foreground')}
                  >
                    {activeValue === v.name.trim() && <Check className="size-3.5" />}
                  </button>
                  <Input
                    value={v.name} placeholder="dev" className="font-mono"
                    onChange={(e) => {
                      const next = [...values]
                      // Keep the active selection pinned to the row being renamed.
                      if (activeValue === next[i].name.trim()) setActiveValue(e.target.value.trim())
                      next[i] = { ...next[i], name: e.target.value }
                      setValues(next)
                    }}
                  />
                  <Input
                    value={v.value} placeholder="https://dev.example.intra" className="font-mono"
                    onChange={(e) => { const next = [...values]; next[i] = { ...next[i], value: e.target.value }; setValues(next) }}
                  />
                  <Button
                    variant="ghost" size="iconSm" aria-label={t('env.removeValue')} disabled={values.length === 1}
                    onClick={() => setValues(values.filter((_, j) => j !== i))}
                  ><X /></Button>
                </div>
              ))}
            </div>

            <div className="mt-3 flex items-center gap-2">
              <Button variant="ghost" onClick={() => setValues([...values, { name: '', value: '' }])}><Plus />{t('env.addValue')}</Button>
              <div className="flex-1" />
              <Button variant="primary" onClick={() => save.mutate()} disabled={blocked || save.isPending}>{t('env.save')}</Button>
              <Button variant="ghost" onClick={() => setEditing(null)}>{t('editor.cancel')}</Button>
            </div>

            {error && <p className="mt-2 text-xs text-danger">{error}</p>}
          </div>
        )}
      </div>

      {/* The {ex} slot is filled here (not via i18next interpolation) because the example itself
          contains double curly braces, which i18next would treat as a variable. */}
      <p className="mt-4 text-sm leading-relaxed text-muted-foreground">
        {t('env.hint').split('{ex}').map((part, i, arr) => (
          <span key={i}>
            {part}
            {i < arr.length - 1 && <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">{'{{baseUrl}}'}/OrderManager/callback</code>}
          </span>
        ))}
      </p>

      <ConfirmDialog
        open={confirmDelete !== null} onOpenChange={(o) => { if (!o) setConfirmDelete(null) }}
        title={t('env.deleteTitle')} body={t('env.deleteBody')}
        confirmLabel={t('stubs.delete')} cancelLabel={t('editor.cancel')}
        onConfirm={() => { if (confirmDelete) { remove.mutate(confirmDelete); setConfirmDelete(null) } }}
      />
    </div>
  )
}
