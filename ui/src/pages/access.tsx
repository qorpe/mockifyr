import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Check, Copy, KeyRound, Plus, ShieldAlert, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { fetchApiKeys, issueApiKey, revokeApiKey } from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button } from '@/components/ui/button'
import { Input, Label } from '@/components/ui/field'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'

/**
 * Sandbox access (G19e, ADR 0011): issue and revoke the tenant's sandbox API keys. The token is
 * shown exactly ONCE, in the issuance dialog — afterwards the server only knows a salted hash and
 * the 12-character display prefix, so there is nothing to re-reveal. Requires the host to run with
 * --sandbox-auth for the keys to authenticate on the mock surface.
 */
export function AccessPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()

  const { data } = useQuery({ queryKey: ['apikeys', tenant], queryFn: () => fetchApiKeys(tenant) })
  const keys = useMemo(() => data?.keys ?? [], [data])
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['apikeys', tenant] })

  const [issuing, setIssuing] = useState(false)
  const [name, setName] = useState('')
  const [quota, setQuota] = useState('')
  const [minted, setMinted] = useState<string | null>(null) // the one-time token
  const [copied, setCopied] = useState(false)
  const [confirmRevoke, setConfirmRevoke] = useState<string | null>(null)
  useEffect(() => { setIssuing(false); setMinted(null); setConfirmRevoke(null) }, [tenant])

  const trimmedName = name.trim()
  const quotaNumber = quota.trim() === '' ? null : Number(quota)
  const quotaInvalid = quotaNumber !== null && (!Number.isInteger(quotaNumber) || quotaNumber <= 0)

  const issue = useMutation({
    mutationFn: () => issueApiKey(tenant, trimmedName, quotaNumber),
    onSuccess: (result) => {
      setIssuing(false)
      setMinted(result.key)
      setCopied(false)
      void invalidate()
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const revoke = useMutation({
    mutationFn: (id: string) => revokeApiKey(tenant, id),
    onSuccess: () => { toast.success(t('access.revoked')); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  const copyToken = () => {
    if (!minted) return
    void navigator.clipboard.writeText(minted).then(() => {
      setCopied(true)
      toast.success(t('access.copied'))
    })
  }

  return (
    <div className="mx-auto max-w-[1100px]">
      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.access')}</h1>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{t('access.subtitle')}</p>
        <div className="mt-3 flex justify-end">
          <Button variant="primary" onClick={() => { setName(''); setQuota(''); setIssuing(true) }}><Plus />{t('access.issue')}</Button>
        </div>
      </header>

      <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
        {keys.length === 0 ? (
          <EmptyState
            art={<KeyRound className="size-10 text-faint" />}
            title={t('access.empty')} body={t('access.emptyHint')} className="py-16"
          />
        ) : (
          <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr>
                {[t('access.key'), t('access.name'), t('access.created'), t('access.quota'), t('access.usedThisHour'), ''].map((h, i) => (
                  <th key={i} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {keys.map((key) => (
                <tr key={key.id} className="border-b border-border last:border-b-0">
                  <td className="whitespace-nowrap px-4 py-3 font-mono text-[12.5px] font-medium">{key.prefix}…</td>
                  <td className="max-w-0 px-4 py-3"><span className="block truncate text-sm">{key.name}</span></td>
                  <td className="whitespace-nowrap px-4 py-3 text-xs tabular-nums text-muted-foreground">{new Date(key.createdAt).toLocaleString()}</td>
                  <td className="whitespace-nowrap px-4 py-3 text-xs tabular-nums text-muted-foreground">
                    {key.quotaPerHour === null ? t('access.unlimited') : t('access.perHour', { count: key.quotaPerHour })}
                  </td>
                  <td className="whitespace-nowrap px-4 py-3">
                    <UsageCell used={key.usedThisHour} quota={key.quotaPerHour} />
                  </td>
                  <td className="w-14 px-4 py-3">
                    <div className="flex justify-end">
                      <Button variant="ghost" size="iconSm" aria-label={t('access.revoke')} onClick={() => setConfirmRevoke(key.id)}><Trash2 /></Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        )}
      </div>

      {/* How a key authenticates — the host-side contract, spelled out where the keys are made. */}
      <div className="mt-4 space-y-1.5 rounded-2xl border border-border bg-muted/30 p-4 text-sm leading-relaxed text-muted-foreground">
        <p>{t('access.hint1')}</p>
        <p>
          {t('access.hint2')}{' '}
          <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">X-Api-Key: mfk_…</code>{' '}
          <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">Authorization: Bearer mfk_…</code>
        </p>
        <p>{t('access.hint3')}</p>
      </div>

      {/* Issue dialog */}
      <ConfirmDialog
        open={issuing} onOpenChange={setIssuing}
        title={t('access.issue')} body={t('access.issueHint')}
        confirmLabel={t('access.issue')} cancelLabel={t('editor.cancel')}
        onConfirm={() => { if (trimmedName && !quotaInvalid && !issue.isPending) issue.mutate() }}
      >
        <div className="mt-4 grid grid-cols-2 gap-3">
          <div>
            <Label>{t('access.name')}</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="partner-ci" autoFocus />
          </div>
          <div>
            <Label>{t('access.quota')}</Label>
            <Input value={quota} onChange={(e) => setQuota(e.target.value)} placeholder={t('access.unlimited')} inputMode="numeric" className="font-mono" />
          </div>
        </div>
        {quotaInvalid && <p className="mt-2 text-xs text-danger">{t('access.invalidQuota')}</p>}
      </ConfirmDialog>

      {/* One-time token reveal — deliberately hard to dismiss by accident (no outside-click worry:
          the only actions are copy and close, and the warning says why). */}
      {minted !== null && (
        <ConfirmDialog
          open onOpenChange={(o) => { if (!o) setMinted(null) }}
          title={t('access.tokenTitle')}
          confirmLabel={t('access.done')} cancelLabel={t('access.close')}
          onConfirm={() => setMinted(null)}
        >
          <div className="mt-3 space-y-3">
            <div className="flex items-center gap-2 rounded-lg border border-warning-border bg-warning-bg px-3 py-2 text-[12.5px] leading-relaxed text-warning">
              <ShieldAlert className="size-4 shrink-0" />
              <span>{t('access.tokenWarn')}</span>
            </div>
            <button
              onClick={copyToken}
              className={cn('flex w-full items-center gap-2 rounded-lg border px-3 py-2.5 text-start font-mono text-[12.5px] transition-colors',
                copied ? 'border-success bg-success/10 text-success' : 'border-border bg-muted/40 hover:border-border-strong')}
            >
              <span className="min-w-0 flex-1 break-all">{minted}</span>
              {copied ? <Check className="size-4 shrink-0" /> : <Copy className="size-4 shrink-0 text-muted-foreground" />}
            </button>
          </div>
        </ConfirmDialog>
      )}

      <ConfirmDialog
        open={confirmRevoke !== null} onOpenChange={(o) => { if (!o) setConfirmRevoke(null) }}
        destructive
        title={t('access.revokeTitle')} body={t('access.revokeBody')}
        confirmLabel={t('access.revoke')} cancelLabel={t('editor.cancel')}
        onConfirm={() => { if (confirmRevoke) { revoke.mutate(confirmRevoke); setConfirmRevoke(null) } }}
      />
    </div>
  )
}

/** used/quota with a tiny bar; unlimited keys show the count alone. */
function UsageCell({ used, quota }: { used: number; quota: number | null }) {
  if (quota === null) return <span className="text-xs tabular-nums text-muted-foreground">{used}</span>
  const pct = Math.min(100, Math.round((used / quota) * 100))
  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-16 overflow-hidden rounded-full bg-muted">
        <div className={cn('h-full rounded-full', pct >= 100 ? 'bg-danger' : pct >= 80 ? 'bg-warning' : 'bg-primary')} style={{ width: `${pct}%` }} />
      </div>
      <span className="text-xs tabular-nums text-muted-foreground">{used} / {quota}</span>
    </div>
  )
}
