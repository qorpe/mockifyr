import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import { ScrollText, ShieldAlert } from 'lucide-react'
import { cn } from '@/lib/utils'
import { fetchAuditEntries, fetchHealth } from '@/lib/api'
import { useUi } from '@/components/providers'
import { Input } from '@/components/ui/field'
import { EmptyState } from '@qorpe/ui'

/**
 * The admin audit trail (#247): who changed what in this tenant, newest first. Read-only by design —
 * entries are written by the host as a side effect of the change they describe, so nothing on this
 * screen (or the API behind it) can rewrite history.
 */
export function AuditPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const [filter, setFilter] = useState('')

  const { data } = useQuery({
    queryKey: ['audit', tenant],
    queryFn: () => fetchAuditEntries(tenant),
    refetchInterval: 5000,
  })
  const { data: status } = useQuery({ queryKey: ['health', tenant], queryFn: () => fetchHealth(tenant) })
  const entries = useMemo(() => data?.entries ?? [], [data])

  const needle = filter.trim().toLowerCase()
  const shown = needle
    ? entries.filter((e) =>
        e.action.toLowerCase().includes(needle) ||
        e.principal.toLowerCase().includes(needle) ||
        (e.target ?? '').toLowerCase().includes(needle) ||
        String(e.outcome).includes(needle))
    : entries

  // An empty trail is ambiguous on its own — nothing changed, or nobody is recording. The host says
  // which, so the screen never leaves an operator guessing.
  const disabled = status?.health.audit === false

  return (
    <div className="mx-auto max-w-[1100px]">
      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.audit')}</h1>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{t('audit.subtitle')}</p>
      </header>

      {disabled && (
        <div className="mb-4 flex items-start gap-2 rounded-2xl border border-warning-border bg-warning-bg px-4 py-3 text-[13px] leading-relaxed text-warning">
          <ShieldAlert className="mt-0.5 size-4 shrink-0" />
          <span>{t('audit.disabled')}</span>
        </div>
      )}

      <div className="mb-3">
        <Input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder={t('audit.filter')}
          className="max-w-xs"
        />
      </div>

      <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
        {shown.length === 0 ? (
          <EmptyState
            art={<ScrollText className="size-10 text-faint" />}
            title={t('audit.empty')}
            body={disabled ? t('audit.disabled') : t('audit.emptyHint')}
            className="py-16"
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr>
                  {[t('audit.when'), t('audit.principal'), t('audit.action'), t('audit.target'), t('audit.outcome')].map((h, i) => (
                    <th key={i} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {shown.map((entry) => (
                  <tr key={entry.id} className="border-b border-border last:border-b-0">
                    <td className="whitespace-nowrap px-4 py-3 text-xs tabular-nums text-muted-foreground">
                      {new Date(entry.timestamp).toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <span className="rounded-md bg-muted px-1.5 py-0.5 font-mono text-[12px]">{entry.principal}</span>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-[12.5px]">{entry.action}</td>
                    <td className="max-w-0 px-4 py-3">
                      {/* Ids are long and the action already says which collection they belong to. */}
                      <span className="block truncate font-mono text-[12px] text-muted-foreground">{entry.target ?? '—'}</span>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <OutcomeBadge status={entry.outcome} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <p className="mt-4 rounded-2xl border border-border bg-muted/30 p-4 text-sm leading-relaxed text-muted-foreground">
        {t('audit.hint')}
      </p>
    </div>
  )
}

/** The status the operation answered with — a refused change is as interesting as a successful one. */
function OutcomeBadge({ status }: { status: number }) {
  const ok = status < 400
  const denied = status === 403
  return (
    <span className={cn('rounded-md px-1.5 py-0.5 font-mono text-[12px] tabular-nums',
      ok ? 'bg-success/10 text-success' : denied ? 'bg-danger/10 text-danger' : 'bg-warning-bg text-warning')}>
      {status}
    </span>
  )
}
