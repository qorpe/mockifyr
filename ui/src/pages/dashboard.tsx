import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Activity, ArrowRight, CheckCircle2, Disc, Download, ListTree, Plus, Waypoints, XCircle } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { fetchHealth, fetchJournal, fetchScenarios, fetchStubs, persistenceLabel } from '@/lib/api'
import { MethodChip } from '@/components/ui/badges'
import { Button } from '@/components/ui/button'

// Live tenant-scoped overview, read from /__admin. When no host answers, every query falls back to
// representative sample data and the "sample" hint is shown — the numbers are never fabricated.
export function DashboardPage() {
  const { t } = useTranslation()
  const { tenant } = useUi()

  const stubs = useQuery({ queryKey: ['stubs', tenant], queryFn: () => fetchStubs(tenant) })
  const journal = useQuery({ queryKey: ['journal', tenant, false], queryFn: () => fetchJournal(tenant, false) })
  const unmatched = useQuery({ queryKey: ['journal', tenant, true], queryFn: () => fetchJournal(tenant, true) })
  const scenarios = useQuery({ queryKey: ['scenarios', tenant], queryFn: () => fetchScenarios(tenant) })
  const health = useQuery({ queryKey: ['health', tenant], queryFn: () => fetchHealth(tenant) })

  const mock = stubs.data?.mock ?? false
  const num = (n?: number) => (n == null ? '—' : n.toLocaleString())

  const stubList = stubs.data?.stubs ?? []
  const recent = (journal.data?.entries ?? []).slice(0, 8)
  const matchedTotal = Math.max(0, (journal.data?.total ?? 0) - (unmatched.data?.total ?? 0))

  // Top unmatched paths — the near-miss candidates that most need a stub.
  const topUnmatched = Object.entries(
    (unmatched.data?.entries ?? []).reduce<Record<string, number>>((acc, e) => {
      const key = `${e.method} ${e.url}`
      acc[key] = (acc[key] ?? 0) + 1
      return acc
    }, {}),
  )
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)

  // Stub distribution by method (for the mini bar chart).
  const byMethod = Object.entries(
    stubList.reduce<Record<string, number>>((acc, s) => {
      acc[s.method] = (acc[s.method] ?? 0) + 1
      return acc
    }, {}),
  ).sort((a, b) => b[1] - a[1])
  const methodMax = Math.max(1, ...byMethod.map(([, n]) => n))

  const empty = !stubs.isLoading && stubList.length === 0

  return (
    <div className="mx-auto max-w-[1360px]">
      <header className="mb-6 flex items-start gap-3">
        <div>
          <h1 className="text-[22px] font-bold tracking-tight">{t('dashboard.title')}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t('dashboard.subtitle')}</p>
        </div>
        {mock && (
          <span className="ms-auto shrink-0 rounded-full border border-warning-border bg-warning-bg px-2.5 py-0.5 text-[11.5px] font-medium text-warning">{t('stubs.sample')}</span>
        )}
      </header>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard icon={ListTree} label={t('dashboard.activeStubs')} value={num(stubList.length)} loading={stubs.isLoading} to="/stubs" />
        <StatCard icon={Activity} label={t('dashboard.requests')} value={num(journal.data?.total)} loading={journal.isLoading} to="/journal" />
        <StatCard icon={XCircle} label={t('dashboard.unmatched')} value={num(unmatched.data?.total)} loading={unmatched.isLoading} tone={(unmatched.data?.total ?? 0) > 0 ? 'danger' : undefined} to="/journal" />
        <StatCard icon={Waypoints} label={t('nav.scenarios')} value={num(scenarios.data?.scenarios.length)} loading={scenarios.isLoading} to="/scenarios" />
      </div>

      {empty ? (
        <GetStarted t={t} />
      ) : (
        <div className="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-3">
          {/* Recent activity — spans two columns on wide screens */}
          <Panel className="lg:col-span-2" title={t('dashboard.recent')} icon={Activity} to="/journal" linkLabel={t('dashboard.viewAll')}>
            {recent.length === 0 ? (
              <Hint>{t('journal.empty')}</Hint>
            ) : (
              <ul className="divide-y divide-border">
                {recent.map((e) => (
                  <li key={e.id} className="flex items-center gap-3 py-2 text-sm">
                    <MethodChip method={e.method} />
                    <span className="min-w-0 flex-1 truncate font-mono text-[12.5px] text-foreground">{e.url}</span>
                    <span className="tabular-nums text-xs text-muted-foreground">{e.status ?? '—'}</span>
                    {e.wasMatched
                      ? <CheckCircle2 className="size-4 shrink-0 text-success" />
                      : <XCircle className="size-4 shrink-0 text-danger" />}
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <div className="flex flex-col gap-4">
            {/* Quick actions */}
            <Panel title={t('common.actions')} icon={Plus}>
              <div className="flex flex-wrap gap-2">
                <Button asChild variant="primary" size="sm"><Link to="/stubs?new=1"><Plus />{t('stubs.newStub')}</Link></Button>
                <Button asChild variant="outline" size="sm"><Link to="/stubs?import=1"><Download />{t('stubs.import')}</Link></Button>
                <Button asChild variant="outline" size="sm"><Link to="/recordings"><Disc />{t('recordings.start')}</Link></Button>
              </div>
            </Panel>

            {/* Top unmatched paths */}
            <Panel title={t('dashboard.topUnmatched')} icon={XCircle} to="/journal" linkLabel={t('dashboard.viewAll')}>
              {topUnmatched.length === 0 ? (
                <Hint>{t('dashboard.noUnmatched')}</Hint>
              ) : (
                <ul className="space-y-1.5">
                  {topUnmatched.map(([path, count]) => (
                    <li key={path} className="flex items-center gap-2 text-sm">
                      <span className="min-w-0 flex-1 truncate font-mono text-[12px] text-muted-foreground">{path}</span>
                      <span className="rounded-full bg-danger-bg px-1.5 py-0.5 text-[11px] font-semibold tabular-nums text-danger">{count}</span>
                    </li>
                  ))}
                </ul>
              )}
            </Panel>
          </div>
        </div>
      )}

      {!empty && (
        <div className="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
          {/* Stubs by method */}
          <Panel title={t('dashboard.byMethod')} icon={ListTree}>
            {byMethod.length === 0 ? <Hint>{t('stubs.empty')}</Hint> : (
              <div className="space-y-2">
                {byMethod.map(([method, n]) => (
                  <div key={method} className="flex items-center gap-3">
                    <span className="w-16 shrink-0"><MethodChip method={method} /></span>
                    <div className="h-2 flex-1 overflow-hidden rounded-full bg-muted">
                      <div className="h-full rounded-full bg-primary" style={{ width: `${(n / methodMax) * 100}%` }} />
                    </div>
                    <span className="w-8 shrink-0 text-right text-xs tabular-nums text-muted-foreground">{n}</span>
                  </div>
                ))}
              </div>
            )}
          </Panel>

          {/* Matched vs unmatched */}
          <Panel title={t('dashboard.matchRate')} icon={CheckCircle2}>
            <MatchRatio matched={matchedTotal} unmatched={unmatched.data?.total ?? 0} t={t} />
          </Panel>
        </div>
      )}

      <div className="mt-4 flex flex-wrap items-center gap-2 rounded-2xl border border-border bg-background p-4 text-sm text-muted-foreground shadow-surface">
        <CheckCircle2 className="size-4 text-success" />
        <span className="font-medium text-success">{t('dashboard.verified')}</span>
        <span className="text-border-strong">·</span>
        <span>{health.data ? `${health.data.health.name} ${health.data.health.version} · ${persistenceLabel(health.data.health.persistence)}` : '·'}</span>
        <span className="ms-auto font-mono text-xs">{t('settings.tenants')}: <b className="tabular-nums">{num(health.data?.health.tenants)}</b></span>
      </div>
    </div>
  )
}

function MatchRatio({ matched, unmatched, t }: { matched: number; unmatched: number; t: (k: string) => string }) {
  const total = matched + unmatched
  if (total === 0) return <Hint>{t('journal.empty')}</Hint>
  const pct = Math.round((matched / total) * 100)
  return (
    <div>
      <div className="mb-2 flex items-end justify-between">
        <span className="text-[27px] font-bold tabular-nums">{pct}%</span>
        <span className="text-xs text-muted-foreground">{matched.toLocaleString()} / {total.toLocaleString()}</span>
      </div>
      <div className="flex h-2.5 overflow-hidden rounded-full bg-muted">
        <div className="h-full bg-success" style={{ width: `${pct}%` }} />
        <div className="h-full bg-danger" style={{ width: `${100 - pct}%` }} />
      </div>
      <div className="mt-2 flex gap-4 text-xs text-muted-foreground">
        <span className="flex items-center gap-1.5"><i className="size-2 rounded-full bg-success" />{t('journal.matched')}</span>
        <span className="flex items-center gap-1.5"><i className="size-2 rounded-full bg-danger" />{t('journal.unmatched')}</span>
      </div>
    </div>
  )
}

function GetStarted({ t }: { t: (k: string) => string }) {
  return (
    <div className="mt-4 rounded-2xl border border-dashed border-border-strong bg-background p-8 text-center shadow-surface">
      <ListTree className="mx-auto size-8 text-faint" />
      <h2 className="mt-3 text-base font-semibold">{t('dashboard.getStarted')}</h2>
      <p className="mx-auto mt-1 max-w-[46ch] text-sm text-muted-foreground">{t('dashboard.getStartedHint')}</p>
      <div className="mt-4 flex justify-center gap-2">
        <Button asChild variant="primary" size="sm"><Link to="/stubs?new=1"><Plus />{t('stubs.newStub')}</Link></Button>
        <Button asChild variant="outline" size="sm"><Link to="/stubs?import=1"><Download />{t('stubs.import')}</Link></Button>
      </div>
    </div>
  )
}

function Panel({ title, icon: Icon, to, linkLabel, className, children }: {
  title: string
  icon: React.ComponentType<{ className?: string }>
  to?: string
  linkLabel?: string
  className?: string
  children: React.ReactNode
}) {
  return (
    <section className={cn('rounded-2xl border border-border bg-background p-4 shadow-surface', className)}>
      <div className="mb-3 flex items-center gap-2">
        <Icon className="size-4 text-muted-foreground" />
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
        {to && (
          <Link to={to} className="ms-auto flex items-center gap-1 text-xs font-medium text-primary hover:underline">
            {linkLabel}<ArrowRight className="size-3" />
          </Link>
        )}
      </div>
      {children}
    </section>
  )
}

function Hint({ children }: { children: React.ReactNode }) {
  return <p className="py-2 text-sm text-faint">{children}</p>
}

const TONE: Record<string, string> = { danger: 'text-danger' }

function StatCard({ icon: Icon, label, value, loading, tone, to }: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  value: string
  loading?: boolean
  tone?: keyof typeof TONE
  to?: string
}) {
  const body = (
    <div className="rounded-2xl border border-border bg-background p-4 shadow-surface transition-colors hover:border-border-strong">
      <div className="flex items-center gap-2 text-xs font-semibold text-muted-foreground">
        <Icon className="size-4" />
        {label}
      </div>
      {loading ? (
        <div className="mt-3 h-7 w-20 animate-pulse rounded bg-muted" />
      ) : (
        <div className={cn('mt-2.5 text-[27px] font-bold tracking-tight tabular-nums', tone && TONE[tone])}>{value}</div>
      )}
    </div>
  )
  return to ? <Link to={to}>{body}</Link> : body
}
