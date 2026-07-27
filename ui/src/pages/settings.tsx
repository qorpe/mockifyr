import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ArrowDownToLine, ArrowUpFromLine, Boxes, Check, Database, GitBranch, KeyRound, Lock, Moon, Palette, Plus, ShieldCheck, Sun, Trash2, Workflow } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import {
  deleteGrpcDescriptor, distrustHost, fetchGitStatus, fetchGrpcDescriptors, fetchHealth, fetchOutboundTrust,
  gitConfigure, gitPull, gitPush, gitSetCredentials, persistenceLabel, trustHost, uploadGrpcDescriptor,
} from '@/lib/api'
import { LOCALES } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Input } from '@/components/ui/field'

export function SettingsPage() {
  const { t } = useTranslation()
  const { tenant, theme, setTheme, locale, setLocale } = useUi()
  const { data } = useQuery({ queryKey: ['health', tenant], queryFn: () => fetchHealth(tenant), refetchInterval: 8000 })
  const health = data?.health

  const providers = ['NullStubPersistence', 'FileSystemStubPersistence', 'LiteDbStubPersistence', 'PostgresStubPersistence', 'RedisStubPersistence']

  return (
    <div className="mx-auto max-w-[1360px]">
      <header className="mb-6">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.settings')}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t('settings.subtitle')}</p>
      </header>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        {/* Status */}
        <Card icon={Boxes} title={t('settings.status')}>
          {data?.mock && <SampleHint t={t} />}
          <dl className="grid grid-cols-2 gap-x-4 gap-y-3">
            <Stat label={t('settings.engine')} value={`${health?.name ?? 'Mockifyr'} v${health?.version ?? '1.0'}`} />
            <Stat label=".NET" value="10 (LTS)" />
            <Stat label={t('settings.tenants')} value={String(health?.tenants ?? '—')} />
            <Stat label={t('settings.totalStubs')} value={String(health?.totalStubs ?? '—')} />
          </dl>
        </Card>

        {/* Persistence */}
        <Card icon={Database} title={t('settings.persistence')}>
          <p className="mb-3 text-sm text-muted-foreground">{t('settings.persistenceHint')}</p>
          <div className="flex flex-wrap gap-2">
            {providers.map((p) => {
              const active = health?.persistence === p
              return (
                <span key={p} className={cn('inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-semibold',
                  active ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-muted text-muted-foreground')}>
                  {active && <Check className="size-3.5" />}{persistenceLabel(p)}
                </span>
              )
            })}
          </div>
        </Card>

        {/* Git sync (ADR 0007): status + explicit push/pull against the host's configured remote */}
        <GitCard />

        {/* Outbound certificate trust (#174): manageable here so a new internal endpoint does not
            need a restart; pinned read-only when a --trust-* flag was passed. */}
        <OutboundTrustCard />

        {/* gRPC descriptors (G18-pre, ADR 0010): upload a compiled *.dsc; serving hot-reloads. */}
        <GrpcDescriptorsCard />

        {/* Transport (host-config, read-only) */}
        <Card icon={ShieldCheck} title={t('settings.transport')}>
          <p className="mb-3 text-sm text-muted-foreground">{t('settings.transportHint')}</p>
          <ul className="space-y-2 text-sm">
            {['HTTPS / TLS', 'HTTP/2 (ALPN)', 'mTLS / client certificates', 'Multi-domain (host/port/scheme)', 'gRPC · GraphQL · WebSocket'].map((c) => (
              <li key={c} className="flex items-center gap-2"><Check className="size-4 text-success" />{c}</li>
            ))}
          </ul>
        </Card>

        {/* Appearance */}
        <Card icon={Palette} title={t('settings.appearance')}>
          <div className="mb-4">
            <div className="mb-2 text-xs font-semibold text-muted-foreground">{t('common.darkMode')}</div>
            <div className="inline-flex gap-1 rounded-lg bg-muted p-1">
              <button onClick={() => setTheme('light')} className={cn('flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-semibold', theme === 'light' ? 'bg-background shadow-sm' : 'text-muted-foreground')}><Sun className="size-4" />Light</button>
              <button onClick={() => setTheme('dark')} className={cn('flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-semibold', theme === 'dark' ? 'bg-background shadow-sm' : 'text-muted-foreground')}><Moon className="size-4" />Dark</button>
            </div>
          </div>
          <div>
            <div className="mb-2 text-xs font-semibold text-muted-foreground">{t('common.language')}</div>
            <div className="flex flex-wrap gap-1.5">
              {LOCALES.map((l) => (
                <Button key={l.code} size="sm" variant={l.code === locale ? 'primary' : 'outline'} onClick={() => setLocale(l.code)}>{l.native}</Button>
              ))}
            </div>
          </div>
        </Card>
      </div>
    </div>
  )
}

/**
 * Git sync card: the host's sync state (remote/branch/dirty/ahead/behind) plus explicit Pull and
 * Push actions. Push opens a small dialog for an optional commit message. Typed host errors
 * (pull-first, diverged, invalid remote tree, auth) surface verbatim in an error toast. Hidden
 * behaviors: unconfigured hosts get the setup hint; unreachable hosts (sample mode) show nothing.
 */
function GitCard() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  // No refetch interval: the host's status endpoint fetches from the remote, so polling would
  // hammer the Git server. It refreshes after every action instead.
  const { data: status } = useQuery({ queryKey: ['gitStatus'], queryFn: () => fetchGitStatus(tenant), refetchOnWindowFocus: false })
  const [busy, setBusy] = useState<'push' | 'pull' | null>(null)
  const [pushOpen, setPushOpen] = useState(false)
  const [message, setMessage] = useState('')

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['gitStatus'] })

  async function pull() {
    setBusy('pull')
    const result = await gitPull(tenant)
    setBusy(null)
    if (!result.ok) toast.error(result.message)
    else if (result.reason === 'up-to-date') toast.message(t('git.upToDate'))
    else {
      toast.success(t('git.pulled', { count: result.stubsLoaded ?? 0 }))
      // The served stub set changed — every tenant-scoped view refetches.
      void queryClient.invalidateQueries()
    }
    refresh()
  }

  async function push() {
    setPushOpen(false)
    setBusy('push')
    const result = await gitPush(tenant, message)
    setBusy(null)
    setMessage('')
    if (!result.ok) toast.error(result.message)
    else toast[result.reason === 'nothing-to-push' ? 'message' : 'success'](
      result.reason === 'nothing-to-push' ? t('git.nothingToPush') : t('git.pushed'))
    refresh()
  }

  const [remoteUrl, setRemoteUrl] = useState('')
  const [branch, setBranch] = useState('main')
  const [connecting, setConnecting] = useState(false)

  // Credentials (#153): sent once to the host, held in its process memory only — never persisted,
  // never echoed back. The status only reports the source (none/environment/dashboard).
  const [token, setToken] = useState('')
  const [username, setUsername] = useState('')
  const [savingCreds, setSavingCreds] = useState(false)

  async function saveCredentials() {
    setSavingCreds(true)
    const result = await gitSetCredentials(tenant, token.trim(), username)
    setSavingCreds(false)
    if ('error' in result) toast.error(result.message)
    else {
      toast.success(token.trim() ? t('git.credentialsSaved') : t('git.credentialsCleared'))
      setToken('')
      setUsername('')
    }
    refresh()
  }

  async function connect() {
    if (!remoteUrl.trim()) return
    setConnecting(true)
    // Save the optional token first, so the connect's own status fetch already authenticates.
    if (token.trim()) {
      const creds = await gitSetCredentials(tenant, token.trim(), username)
      if ('error' in creds) { toast.error(creds.message); setConnecting(false); return }
      setToken('')
      setUsername('')
    }
    const result = await gitConfigure(tenant, remoteUrl.trim(), branch)
    setConnecting(false)
    if ('error' in result) toast.error(result.message)
    else {
      toast.success(t('git.connected'))
      setRemoteUrl('')
    }
    refresh()
  }

  return (
    <Card icon={GitBranch} title={t('git.title')}>
      <p className="mb-3 text-sm text-muted-foreground">{t('git.hint')}</p>
      {!status?.configured ? (
        // Connect form (#151): remote + branch only — the local working copy resolves host-side, and
        // credentials never pass through the browser (private HTTPS remotes use MOCKIFYR_GIT_TOKEN).
        <div className="space-y-2.5">
          <div className="grid grid-cols-[minmax(0,1fr)_130px] gap-2">
            <Input value={remoteUrl} onChange={(e) => setRemoteUrl(e.target.value)}
              placeholder="https://github.com/team/stubs.git" className="font-mono"
              onKeyDown={(e) => { if (e.key === 'Enter') void connect() }} />
            <Input value={branch} onChange={(e) => setBranch(e.target.value)} placeholder="main" className="font-mono" />
          </div>
          <div className="grid grid-cols-[minmax(0,1fr)_130px] gap-2">
            <Input type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)}
              placeholder={t('git.tokenPlaceholder')} className="font-mono" />
            <Input value={username} onChange={(e) => setUsername(e.target.value)} placeholder={t('git.usernamePlaceholder')} className="font-mono" />
          </div>
          <div className="flex items-center gap-3">
            <Button size="sm" variant="primary" onClick={() => void connect()} disabled={connecting || !remoteUrl.trim()}>
              <GitBranch />{connecting ? '…' : t('git.connect')}
            </Button>
            <p className="text-xs text-faint">{t('git.tokenHint')}</p>
          </div>
        </div>
      ) : (
        <>
          <dl className="mb-3 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
            <dt className="text-xs text-muted-foreground">{t('git.remote')}</dt>
            <dd className="min-w-0 truncate font-mono text-[12.5px]">{status.remote}</dd>
            <dt className="text-xs text-muted-foreground">{t('git.branch')}</dt>
            <dd className="font-mono text-[12.5px]">{status.branch}</dd>
          </dl>
          {status.configuredBy === 'flags' && (
            <p className="mb-3 text-xs text-faint">{t('git.pinnedByFlags')}</p>
          )}
          <div className="mb-4 flex flex-wrap gap-1.5">
            <Chip tone={status.dirty ? 'warning' : 'success'}>{status.dirty ? t('git.dirty') : t('git.clean')}</Chip>
            {status.ahead > 0 && <Chip tone="info">↑ {t('git.ahead', { count: status.ahead })}</Chip>}
            {status.behind > 0 && <Chip tone="info">↓ {t('git.behind', { count: status.behind })}</Chip>}
            {status.fetchError && <Chip tone="danger">{t('git.fetchError')}</Chip>}
          </div>
          <div className="mb-4 space-y-2">
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <KeyRound className="size-3.5" />
              <span>{t('git.credentials')}</span>
              {status.credentialsSource === 'dashboard' && <Chip tone="success">{t('git.credsDashboard')}</Chip>}
              {status.credentialsSource === 'environment' && <Chip tone="info">{t('git.credsEnv')}</Chip>}
            </div>
            <div className="grid grid-cols-[minmax(0,1fr)_130px_auto] gap-2">
              <Input type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)}
                placeholder={status.credentialsSource === 'dashboard' ? '••••••••' : t('git.tokenPlaceholder')} className="font-mono"
                onKeyDown={(e) => { if (e.key === 'Enter' && token.trim()) void saveCredentials() }} />
              <Input value={username} onChange={(e) => setUsername(e.target.value)} placeholder={t('git.usernamePlaceholder')} className="font-mono" />
              <Button size="sm" variant="outline" onClick={() => void saveCredentials()}
                disabled={savingCreds || (!token.trim() && status.credentialsSource !== 'dashboard')}>
                {savingCreds ? '…' : token.trim() ? t('git.saveCredentials') : t('git.clearCredentials')}
              </Button>
            </div>
            <p className="text-xs text-faint">{t('git.credentialsHint')}</p>
          </div>
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => void pull()} disabled={busy !== null}>
              <ArrowDownToLine />{busy === 'pull' ? '…' : t('git.pull')}
            </Button>
            <Button size="sm" variant="primary" onClick={() => setPushOpen(true)} disabled={busy !== null}>
              <ArrowUpFromLine />{busy === 'push' ? '…' : t('git.push')}
            </Button>
          </div>
          <ConfirmDialog
            open={pushOpen} onOpenChange={setPushOpen}
            title={t('git.pushTitle')} body={t('git.pushHint')}
            confirmLabel={t('git.push')} cancelLabel={t('editor.cancel')}
            onConfirm={() => void push()}
          >
            <Input className="mt-3" value={message} onChange={(e) => setMessage(e.target.value)}
              placeholder={t('git.messagePlaceholder')} onKeyDown={(e) => { if (e.key === 'Enter') void push() }} />
          </ConfirmDialog>
        </>
      )}
    </Card>
  )
}

function Chip({ tone, children }: { tone: 'success' | 'warning' | 'info' | 'danger'; children: React.ReactNode }) {
  const tones = {
    success: 'border-success-border bg-success-bg text-success',
    warning: 'border-warning-border bg-warning-bg text-warning',
    info: 'border-info-border bg-info-bg text-info',
    danger: 'border-danger-border bg-danger-bg text-danger',
  }
  return <span className={cn('inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-[11.5px] font-medium', tones[tone])}>{children}</span>
}

/**
 * Outbound certificate trust (#174). An endpoint served by an internal CA is trusted by the operator's
 * machine but not by the container, so a callback or proxy to it fails where Postman succeeds. Adding
 * the host here takes effect on the next call — no restart.
 *
 * Two modes, mirroring the Git card: a host started with a --trust-* flag is read-only here.
 * "Trust every target" is deliberately absent — disabling verification wholesale stays a startup
 * decision rather than something one click can do.
 */
function OutboundTrustCard() {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const { data: trust } = useQuery({ queryKey: ['outboundTrust'], queryFn: () => fetchOutboundTrust(tenant) })
  const [host, setHost] = useState('')
  const [busy, setBusy] = useState(false)

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['outboundTrust'] })

  // Unreachable host (sample mode): show nothing, like the Git card.
  if (!trust) return null

  async function add() {
    const value = host.trim()
    if (!value) return
    setBusy(true)
    const result = await trustHost(tenant, value)
    setBusy(false)
    if ('error' in result) { toast.error(result.message); return }
    setHost('')
    toast.success(t('trust.added', { host: value }))
    refresh()
  }

  async function remove(value: string) {
    const result = await distrustHost(tenant, value)
    if ('error' in result) { toast.error(result.message); return }
    toast.success(t('trust.removed', { host: value }))
    refresh()
  }

  return (
    <Card icon={Lock} title={t('settings.outboundTrust')}>
      <p className="mb-3 text-sm text-muted-foreground">{t('settings.outboundTrustHint')}</p>

      {trust.trustAll && (
        <p className="mb-3 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-xs text-warning">
          {t('trust.allWarning')}
        </p>
      )}
      {trust.pinned && !trust.trustAll && (
        <p className="mb-3 text-xs text-faint">{t('trust.pinnedByFlags')}</p>
      )}
      {!trust.pinned && !trust.persistent && (
        <p className="mb-3 text-xs text-faint">{t('trust.notPersistent')}</p>
      )}

      {trust.hosts.length > 0 ? (
        <ul className="mb-3 space-y-1.5">
          {trust.hosts.map((h) => (
            <li key={h} className="flex items-center justify-between gap-2 rounded-lg border border-border px-3 py-1.5">
              <span className="break-all font-mono text-[12.5px]">{h}</span>
              {!trust.pinned && (
                <Button variant="ghost" size="iconSm" aria-label={t('trust.remove')} onClick={() => void remove(h)}>
                  <Trash2 />
                </Button>
              )}
            </li>
          ))}
        </ul>
      ) : (
        !trust.trustAll && <p className="mb-3 text-sm text-muted-foreground">{t('trust.empty')}</p>
      )}

      {!trust.pinned && (
        <div className="flex gap-2">
          <Input
            value={host} onChange={(e) => setHost(e.target.value)} placeholder="api.dev.mycorp.intra"
            className="font-mono" onKeyDown={(e) => { if (e.key === 'Enter') void add() }}
          />
          <Button variant="primary" onClick={() => void add()} disabled={!host.trim() || busy}>
            <Plus />{t('trust.add')}
          </Button>
        </div>
      )}
    </Card>
  )
}

// gRPC descriptor management (G18-pre): list the host's *.dsc files and their indexed services,
// upload a new set (hot-reloads serving — no restart), delete one. Host-level, like outbound trust.
function GrpcDescriptorsCard() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const { data } = useQuery({ queryKey: ['grpc-descriptors'], queryFn: fetchGrpcDescriptors })
  const grpc = data?.grpc
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['grpc-descriptors'] })

  async function upload(file: File) {
    const { ok } = await uploadGrpcDescriptor(file.name, await file.arrayBuffer())
    if (ok) { toast.success(t('settings.descriptorUploaded')); refresh() }
    else toast.error(t('settings.descriptorInvalid'))
  }

  async function remove(name: string) {
    const { ok } = await deleteGrpcDescriptor(name)
    if (ok) { toast.success(t('settings.descriptorDeleted')); refresh() }
  }

  return (
    <Card icon={Workflow} title={t('settings.grpcDescriptors')}>
      <p className="mb-3 text-sm text-muted-foreground">{t('settings.grpcDescriptorsHint')}</p>
      {(grpc?.descriptors ?? []).length === 0 ? (
        <p className="mb-3 text-sm text-faint">{t('settings.noDescriptors')}</p>
      ) : (
        <ul className="mb-3 space-y-2">
          {grpc!.descriptors.map((d) => (
            <li key={d.name} className="flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2 text-sm">
              <span className="font-mono text-[12.5px]">{d.name}</span>
              <span className="text-xs text-faint">{(d.size / 1024).toFixed(1)} KB</span>
              <button onClick={() => void remove(d.name)} aria-label={t('common.remove')}
                className="ms-auto rounded p-1 text-faint transition-colors hover:bg-danger-bg hover:text-danger"><Trash2 className="size-3.5" /></button>
            </li>
          ))}
        </ul>
      )}
      {(grpc?.services ?? []).length > 0 && (
        <ul className="mb-3 space-y-1 text-sm">
          {grpc!.services.map((s) => (
            <li key={s.service}>
              <span className="font-mono text-[12.5px] text-muted-foreground">{s.service}</span>
              <span className="ms-2 text-xs text-faint">{s.methods.map((m) => m.method).join(' · ')}</span>
            </li>
          ))}
        </ul>
      )}
      <label className="inline-flex cursor-pointer">
        <input type="file" accept=".dsc" className="hidden"
          onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = '' }} />
        <span className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-border bg-background px-3 text-[13px] font-medium transition-colors hover:bg-muted">
          <Plus className="size-3.5" />{t('settings.uploadDescriptor')}
        </span>
      </label>
    </Card>
  )
}

function Card({ icon: Icon, title, children }: { icon: React.ComponentType<{ className?: string }>; title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-2xl border border-border bg-background p-5 shadow-surface">
      <div className="mb-4 flex items-center gap-2.5">
        <span className="flex size-8 items-center justify-center rounded-lg bg-muted text-muted-foreground"><Icon className="size-4" /></span>
        <h2 className="font-semibold">{title}</h2>
      </div>
      {children}
    </section>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 font-semibold tabular-nums">{value}</dd>
    </div>
  )
}

function SampleHint({ t }: { t: (k: string) => string }) {
  return <div className="mb-3 inline-flex rounded-full border border-warning-border bg-warning-bg px-2.5 py-0.5 text-[11.5px] font-medium text-warning">{t('stubs.sample')}</div>
}
