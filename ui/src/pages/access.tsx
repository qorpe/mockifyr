import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Check, Copy, KeyRound, Plus, RefreshCw, ShieldAlert, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { fetchApiKeys, fetchUsage, issueApiKey, revokeApiKey, rotateApiKey } from '@/lib/api'
import type { ApiKeyEntry, KeyUsageEntry } from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button } from '@qorpe/ui'
import { Input, Label } from '@/components/ui/field'
import { EmptyState } from '@qorpe/ui'
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
  // Usage is a separate query because it is a separate decision: a host without --usage answers an
  // empty list, and the keys table must still render exactly as it always did.
  const { data: usage } = useQuery({ queryKey: ['usage', tenant], queryFn: () => fetchUsage(tenant) })
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['apikeys', tenant] })

  const [issuing, setIssuing] = useState(false)
  const [name, setName] = useState('')
  const [quota, setQuota] = useState('')
  const [minted, setMinted] = useState<string | null>(null) // the one-time token
  const [copied, setCopied] = useState(false)
  const [confirmRevoke, setConfirmRevoke] = useState<string | null>(null)
  const [revokeReason, setRevokeReason] = useState('')
  const [expiresInDays, setExpiresInDays] = useState('')
  const [readOnly, setReadOnly] = useState(false)
  const [rotating, setRotating] = useState<string | null>(null)
  const [overlap, setOverlap] = useState('60')
  useEffect(() => {
    setIssuing(false); setMinted(null); setConfirmRevoke(null); setRotating(null)
  }, [tenant])

  const trimmedName = name.trim()
  const quotaNumber = quota.trim() === '' ? null : Number(quota)
  const quotaInvalid = quotaNumber !== null && (!Number.isInteger(quotaNumber) || quotaNumber <= 0)

  const expiryDays = expiresInDays.trim() === '' ? null : Number(expiresInDays)
  const expiryInvalid = expiryDays !== null && (!Number.isInteger(expiryDays) || expiryDays <= 0)

  const issue = useMutation({
    mutationFn: () => issueApiKey(tenant, trimmedName, quotaNumber, {
      expiresInDays: expiryDays,
      scope: readOnly ? 'read' : 'readwrite',
    }),
    onSuccess: (result) => {
      setIssuing(false)
      setMinted(result.key)
      setCopied(false)
      void invalidate()
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const revoke = useMutation({
    mutationFn: (id: string) => revokeApiKey(tenant, id, revokeReason.trim() || undefined),
    onSuccess: () => { toast.success(t('access.revoked')); setRevokeReason(''); void invalidate() },
    onError: (error: Error) => toast.error(error.message),
  })

  // Rotation reveals its successor through the same one-time dialog as issuance: the token exists
  // exactly once either way, and two reveal paths would be two chances to leak it.
  const rotate = useMutation({
    mutationFn: (id: string) => rotateApiKey(tenant, id, Number(overlap) || 0),
    onSuccess: (result) => { setRotating(null); setMinted(result.key); setCopied(false); void invalidate() },
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
                {[t('access.key'), t('access.name'), t('access.status'), t('access.expires'), t('access.quota'), t('access.usedThisHour'), ''].map((h, i) => (
                  <th key={i} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {keys.map((key) => (
                <tr key={key.id} className={cn('border-b border-border last:border-b-0', key.status !== 'active' && 'opacity-60')}>
                  <td className="whitespace-nowrap px-4 py-3 font-mono text-[12.5px] font-medium">{key.prefix}…</td>
                  <td className="max-w-0 px-4 py-3">
                    <span className="block truncate text-sm">{key.name}</span>
                    {key.scope === 'read' && (
                      <span className="mt-0.5 inline-block whitespace-nowrap rounded bg-muted px-1.5 py-0.5 text-[10.5px] font-medium uppercase tracking-wide text-muted-foreground">
                        {t('access.readOnly')}
                      </span>
                    )}
                  </td>
                  <td className="whitespace-nowrap px-4 py-3"><StatusCell entry={key} /></td>
                  <td className="whitespace-nowrap px-4 py-3"><ExpiryCell entry={key} /></td>
                  <td className="whitespace-nowrap px-4 py-3 text-xs tabular-nums text-muted-foreground">
                    {key.quotaPerHour === null ? t('access.unlimited') : t('access.perHour', { count: key.quotaPerHour })}
                  </td>
                  <td className="whitespace-nowrap px-4 py-3">
                    <UsageCell used={key.usedThisHour} quota={key.quotaPerHour} />
                  </td>
                  <td className="w-24 px-4 py-3">
                    <div className="flex justify-end gap-1">
                      {key.status === 'active' && (
                        <Button
                          variant="ghost" size="iconSm" aria-label={t('access.rotate')}
                          onClick={() => { setOverlap('60'); setRotating(key.id) }}
                        ><RefreshCw /></Button>
                      )}
                      {key.status !== 'revoked' && (
                        <Button variant="ghost" size="iconSm" aria-label={t('access.revoke')} onClick={() => { setRevokeReason(''); setConfirmRevoke(key.id) }}><Trash2 /></Button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        )}
      </div>

      {(usage?.length ?? 0) > 0 && <UsagePanel entries={usage!} />}

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
        onConfirm={() => { if (trimmedName && !quotaInvalid && !expiryInvalid && !issue.isPending) issue.mutate() }}
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
          <div>
            <Label>{t('access.expiresInDays')}</Label>
            <Input value={expiresInDays} onChange={(e) => setExpiresInDays(e.target.value)} placeholder={t('access.never')} inputMode="numeric" className="font-mono" />
          </div>
          <div>
            <Label>{t('access.scope')}</Label>
            <label className="mt-1.5 flex items-center gap-2 text-sm">
              <input type="checkbox" checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} />
              <span>{t('access.readOnlyHint')}</span>
            </label>
          </div>
        </div>
        {quotaInvalid && <p className="mt-2 text-xs text-danger">{t('access.invalidQuota')}</p>}
        {expiryInvalid && <p className="mt-2 text-xs text-danger">{t('access.invalidExpiry')}</p>}
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
      >
        <div className="mt-3">
          <Label>{t('access.reason')}</Label>
          <Input value={revokeReason} onChange={(e) => setRevokeReason(e.target.value)} placeholder={t('access.reasonHint')} />
        </div>
      </ConfirmDialog>

      {/* Rotation: issue the successor, let the old key lapse. The overlap is the whole reason this
          is a separate gesture from revoke — zero means "the credential is already out there". */}
      <ConfirmDialog
        open={rotating !== null} onOpenChange={(o) => { if (!o) setRotating(null) }}
        title={t('access.rotateTitle')} body={t('access.rotateBody')}
        confirmLabel={t('access.rotate')} cancelLabel={t('editor.cancel')}
        onConfirm={() => { if (rotating && !rotate.isPending) rotate.mutate(rotating) }}
      >
        <div className="mt-3">
          <Label>{t('access.overlapMinutes')}</Label>
          <Input value={overlap} onChange={(e) => setOverlap(e.target.value)} inputMode="numeric" className="font-mono" />
          <p className="mt-1.5 text-xs text-muted-foreground">{t('access.overlapHint')}</p>
        </div>
      </ConfirmDialog>
    </div>
  )
}

/**
 * What each consumer actually did over the last 24 hours (#356). Shown only when the host is keeping
 * counts; the unmatched paths come first inside each row, because a call the sandbox does not model is
 * the integration going wrong and is the reason anybody opens this.
 */
function UsagePanel({ entries }: { entries: KeyUsageEntry[] }) {
  const { t } = useTranslation()
  return (
    <div className="mt-4 overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
      <div className="border-b border-border bg-muted/40 px-4 py-2.5">
        <h2 className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{t('access.usageTitle')}</h2>
      </div>
      <div className="divide-y divide-border">
        {entries.map((entry) => (
          <div key={entry.id} className="px-4 py-3">
            <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
              <span className="text-sm font-medium">{entry.name}</span>
              <span className="font-mono text-[11.5px] text-muted-foreground">{entry.prefix}…</span>
              <span className="text-xs tabular-nums text-muted-foreground">{t('access.usageTotal', { count: entry.total })}</span>
              <Tally label={t('access.usageMatched')} value={entry.matched} />
              <Tally label={t('access.usageUnmatched')} value={entry.unmatched} tone={entry.unmatched > 0 ? 'warning' : undefined} />
              <Tally label={t('access.usageRateLimited')} value={entry.rateLimited} tone={entry.rateLimited > 0 ? 'warning' : undefined} />
              <Tally label={t('access.usageUnauthorized')} value={entry.unauthorized} tone={entry.unauthorized > 0 ? 'danger' : undefined} />
              <Tally label={t('access.usageForbidden')} value={entry.forbidden} tone={entry.forbidden > 0 ? 'danger' : undefined} />
            </div>
            {entry.topUnmatchedPaths.length > 0 && (
              <div className="mt-2">
                <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{t('access.usageTopUnmatched')}</p>
                <ul className="mt-1 space-y-0.5">
                  {entry.topUnmatchedPaths.map((path) => (
                    <li key={path.path} className="flex items-baseline gap-2 font-mono text-[12px]">
                      <span className="tabular-nums text-muted-foreground">{path.count}×</span>
                      <span className="break-all">{path.path}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}

function Tally({ label, value, tone }: { label: string; value: number; tone?: 'warning' | 'danger' }) {
  if (value === 0 && tone === undefined) return null
  return (
    <span className={cn('text-xs tabular-nums',
      tone === 'danger' ? 'text-danger' : tone === 'warning' ? 'text-warning' : 'text-muted-foreground')}>
      {label} {value}
    </span>
  )
}

/** Active / expired / revoked, as the host computed it — never recomputed here. */
function StatusCell({ entry }: { entry: ApiKeyEntry }) {
  const { t } = useTranslation()
  const tone = entry.status === 'active' ? 'bg-success/10 text-success'
    : entry.status === 'expired' ? 'bg-warning-bg text-warning'
    : 'bg-danger/10 text-danger'
  return (
    <span className={cn('inline-block rounded px-2 py-0.5 text-[11px] font-medium', tone)}
      title={entry.revokedBy ? `${entry.revokedBy}${entry.revokedReason ? ` — ${entry.revokedReason}` : ''}` : undefined}>
      {t(`access.status_${entry.status}`)}
    </span>
  )
}

/**
 * When the key dies, and — while it is still alive — how soon. A key that dies unannounced is an
 * incident on a Sunday, so the warning appears a week before rather than after.
 */
function ExpiryCell({ entry }: { entry: ApiKeyEntry }) {
  const { t } = useTranslation()
  if (entry.expiresAt === null) return <span className="text-xs text-muted-foreground">{t('access.never')}</span>

  // Rounded to the unit that means something: a rotation overlap is measured in minutes, and
  // "in 1 d" for a key that dies in an hour is worse than no number at all.
  const remaining = new Date(entry.expiresAt).getTime() - Date.now()
  const hours = remaining / 3_600_000
  const label = hours >= 48 ? t('access.inDays', { count: Math.ceil(hours / 24) })
    : hours >= 1 ? t('access.inHours', { count: Math.floor(hours) })
    : t('access.inMinutes', { count: Math.max(0, Math.floor(remaining / 60_000)) })
  const soon = entry.status === 'active' && hours <= 7 * 24
  return (
    <span className={cn('text-xs tabular-nums', soon ? 'font-medium text-warning' : 'text-muted-foreground')}
      title={new Date(entry.expiresAt).toLocaleString()}>
      {entry.status === 'expired' ? t('access.expired') : label}
    </span>
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
