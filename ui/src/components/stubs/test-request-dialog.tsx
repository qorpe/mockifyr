import { useEffect, useMemo, useState } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { AlertTriangle, Copy, FlaskConical, Plus, Send, Terminal, WandSparkles, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { TENANT_HEADER } from '@/lib/tenants'
import { previewEnvironment, type EnvironmentKey } from '@/lib/environments'
import { Button } from '@qorpe/ui'
import { Input, NativeSelect } from '@/components/ui/field'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { JsonField } from '@/components/ui/json-editor'
import { BodyView, HeadersView, StatusChip } from '@/components/ui/http-view'

/** What the runner is seeded with — the editor's current (possibly unsaved) request half. */
export interface TestSeed {
  method: string
  url: string
  /** equalTo query-parameter matchers become prefilled params. */
  params: { name: string; value: string }[]
  /** equalTo header matchers become prefilled headers. */
  headers: { name: string; value: string }[]
  /** The first equalTo/equalToJson body matcher becomes the prefilled body. */
  body: string
}

interface Kv { name: string; value: string }

interface TestResponse {
  status: number
  durationMs: number
  bytes: number
  headers: { name: string; value: string }[]
  body: string
}

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']
const BODYLESS = new Set(['GET', 'HEAD'])

/** Mock traffic origin: same origin when embedded; the dev proxy tunnel while on the Vite server. */
const MOCK_BASE = import.meta.env.DEV ? '/__mock' : ''

/** Resolves {{key}} references and appends the params rows to the URL's query string. */
function buildUrl(raw: string, params: Kv[], environments: EnvironmentKey[]): string {
  let url = previewEnvironment(raw.trim(), environments).resolved
  const pairs = params.filter((p) => p.name.trim())
  if (pairs.length > 0) {
    const qs = new URLSearchParams()
    for (const p of pairs) qs.append(p.name.trim(), previewEnvironment(p.value, environments).resolved)
    url += (url.includes('?') ? '&' : '?') + qs.toString()
  }
  return url
}

function toCurl(method: string, url: string, headers: Kv[], body: string, tenant: string): string {
  const absolute = /^https?:\/\//i.test(url) ? url : window.location.origin + url
  const parts = [`curl -X ${method} '${absolute.replace(/'/g, "'\\''")}'`]
  parts.push(`-H '${TENANT_HEADER}: ${tenant}'`)
  for (const h of headers.filter((x) => x.name.trim())) {
    parts.push(`-H '${h.name.trim()}: ${h.value.replace(/'/g, "'\\''")}'`)
  }
  if (body && !BODYLESS.has(method)) parts.push(`-d '${body.replace(/'/g, "'\\''")}'`)
  return parts.join(' \\\n  ')
}

/** Key-value rows in the visual language of the editor's matcher rows. */
function KvRows({ rows, onChange, keyPlaceholder, addLabel, removeLabel }: {
  rows: Kv[]
  onChange: (rows: Kv[]) => void
  keyPlaceholder: string
  addLabel: string
  removeLabel: string
}) {
  return (
    <div className="space-y-2">
      {rows.map((row, i) => (
        <div key={i} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)_auto] items-center gap-2">
          <Input value={row.name} placeholder={keyPlaceholder} className="font-mono"
            onChange={(e) => { const next = [...rows]; next[i] = { ...next[i], name: e.target.value }; onChange(next) }} />
          <Input value={row.value} placeholder="value" className="font-mono"
            onChange={(e) => { const next = [...rows]; next[i] = { ...next[i], value: e.target.value }; onChange(next) }} />
          <Button variant="ghost" size="iconSm" aria-label={removeLabel} onClick={() => onChange(rows.filter((_, j) => j !== i))}><X /></Button>
        </div>
      ))}
      <Button variant="ghost" onClick={() => onChange([...rows, { name: '', value: '' }])}><Plus />{addLabel}</Button>
    </div>
  )
}

/**
 * The stub test runner (#203): a centered dialog that fires a REAL request at this host — through
 * the serving pipeline, so environment keys resolve server-side, scenarios advance, and the call
 * lands in the request journal like any other traffic. Seeded from the editor's current request
 * half on every open; closing discards everything.
 */
export function TestRequestDialog({ open, onOpenChange, seed, tenant, environments }: {
  open: boolean
  onOpenChange: (o: boolean) => void
  seed: () => TestSeed
  tenant: string
  environments: EnvironmentKey[]
}) {
  const { t } = useTranslation()
  const [method, setMethod] = useState('GET')
  const [url, setUrl] = useState('')
  const [params, setParams] = useState<Kv[]>([])
  const [headers, setHeaders] = useState<Kv[]>([])
  const [body, setBody] = useState('')
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [response, setResponse] = useState<TestResponse | null>(null)

  // Re-seed from the stub's CURRENT configuration on every open (predictable reopen, per the issue).
  useEffect(() => {
    if (!open) return
    const s = seed()
    setMethod(METHODS.includes(s.method) ? s.method : 'GET')
    setUrl(s.url)
    setParams(s.params.length > 0 ? s.params : [])
    setHeaders(s.headers)
    setBody(s.body)
    setError(null)
    setResponse(null)
    setSending(false)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const finalUrl = useMemo(() => buildUrl(url, params, environments), [url, params, environments])
  const urlPreview = previewEnvironment(url, environments)
  const bodyPretty = (() => { try { JSON.parse(body); return true } catch { return false } })()

  const send = async () => {
    const target = /^https?:\/\//i.test(finalUrl) ? finalUrl : MOCK_BASE + (finalUrl.startsWith('/') ? finalUrl : `/${finalUrl}`)
    const requestHeaders: Record<string, string> = { [TENANT_HEADER]: tenant }
    for (const h of headers.filter((x) => x.name.trim())) {
      requestHeaders[h.name.trim()] = previewEnvironment(h.value, environments).resolved
    }
    const resolvedBody = previewEnvironment(body, environments).resolved
    setSending(true)
    setError(null)
    setResponse(null)
    const controller = new AbortController()
    const timeout = window.setTimeout(() => controller.abort(), 30_000)
    const started = performance.now()
    try {
      const res = await fetch(target, {
        method,
        headers: requestHeaders,
        body: !BODYLESS.has(method) && resolvedBody ? resolvedBody : undefined,
        signal: controller.signal,
      })
      const text = await res.text()
      setResponse({
        status: res.status,
        durationMs: Math.round(performance.now() - started),
        bytes: new Blob([text]).size,
        headers: [...res.headers.entries()].map(([name, value]) => ({ name, value })),
        body: text,
      })
    } catch (e) {
      setError(e instanceof DOMException && e.name === 'AbortError' ? t('test.timeout') : e instanceof Error ? e.message : String(e))
    } finally {
      window.clearTimeout(timeout)
      setSending(false)
    }
  }

  const copy = (text: string) => {
    void navigator.clipboard.writeText(text).then(() => toast.success(t('test.copied')))
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/40 data-[state=open]:animate-in data-[state=open]:fade-in-0" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[84vh] w-[92vw] max-w-[880px] -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-border bg-background shadow-2xl outline-none data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95">
          <div className="flex items-center gap-2 border-b border-border px-5 py-3.5">
            <FlaskConical className="size-4 text-violet" />
            <Dialog.Title className="text-[15px] font-semibold">{t('test.title')}</Dialog.Title>
            <Dialog.Description className="sr-only">{t('test.hint')}</Dialog.Description>
            <Dialog.Close className="ms-auto rounded-lg p-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"><X className="size-4" /></Dialog.Close>
          </div>

          <div className="scroll-area min-h-0 flex-1 overflow-y-auto">
            <div className="space-y-4 p-5">
              <p className="text-xs leading-relaxed text-muted-foreground">{t('test.hint')}</p>

              <div className="flex items-start gap-2">
                <NativeSelect value={method} onChange={(e) => setMethod(e.target.value)} className="w-[110px] shrink-0 font-mono">
                  {METHODS.map((m) => <option key={m}>{m}</option>)}
                </NativeSelect>
                <div className="min-w-0 flex-1">
                  <Input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="/api/v2/…" className="font-mono" autoFocus />
                  {(urlPreview.changed || urlPreview.unknown.length > 0) && (
                    <div className="mt-1 space-y-0.5">
                      {urlPreview.changed && <p className="break-all font-mono text-[11.5px] text-success">→ {urlPreview.resolved}</p>}
                      {urlPreview.unknown.map((name) => (
                        <p key={name} className="text-[11.5px] text-warning">{t('env.unknown', { name })}</p>
                      ))}
                    </div>
                  )}
                </div>
                <Button variant="primary" onClick={() => void send()} disabled={sending || !url.trim()}>
                  <Send />{sending ? t('test.sending') : t('test.send')}
                </Button>
              </div>

              <Tabs defaultValue="params">
                <TabsList>
                  <TabsTrigger value="params">{t('test.params')}{params.some((p) => p.name.trim()) ? ` (${params.filter((p) => p.name.trim()).length})` : ''}</TabsTrigger>
                  <TabsTrigger value="headers">{t('test.headers')}{headers.some((h) => h.name.trim()) ? ` (${headers.filter((h) => h.name.trim()).length})` : ''}</TabsTrigger>
                  <TabsTrigger value="body">{t('test.body')}</TabsTrigger>
                </TabsList>
                <TabsContent value="params" className="pt-3">
                  <KvRows rows={params} onChange={setParams} keyPlaceholder="page" addLabel={t('test.addParam')} removeLabel={t('common.remove')} />
                </TabsContent>
                <TabsContent value="headers" className="pt-3">
                  <KvRows rows={headers} onChange={setHeaders} keyPlaceholder="Content-Type" addLabel={t('test.addHeader')} removeLabel={t('common.remove')} />
                </TabsContent>
                <TabsContent value="body" className="pt-3">
                  {BODYLESS.has(method) ? (
                    <p className="rounded-lg border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">{t('test.bodySkipped', { method })}</p>
                  ) : (
                    <div className="space-y-2">
                      <JsonField value={body} onChange={setBody} height={180} lint={false} minimal />
                      <Button variant="ghost" disabled={!bodyPretty} onClick={() => setBody(JSON.stringify(JSON.parse(body), null, 2))}>
                        <WandSparkles />{t('test.beautify')}
                      </Button>
                    </div>
                  )}
                </TabsContent>
              </Tabs>

              <div className="border-t border-border pt-4">
                {!response && !error && !sending && (
                  <p className="py-6 text-center text-sm text-muted-foreground">{t('test.empty')}</p>
                )}
                {sending && (
                  <div className="space-y-3 py-2">{Array.from({ length: 3 }).map((_, i) => <div key={i} className="h-6 animate-pulse rounded bg-muted" />)}</div>
                )}
                {error && (
                  <p className="flex items-start gap-1.5 rounded-lg border border-danger-border bg-danger-bg px-3 py-2 text-xs text-danger">
                    <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />{t('test.failed')}: {error}
                  </p>
                )}
                {response && (
                  <div className="space-y-4">
                    <div className="flex flex-wrap items-center gap-2">
                      <h3 className="text-xs font-semibold uppercase tracking-wide text-faint">{t('test.response')}</h3>
                      <StatusChip status={response.status} />
                      <span className={cn('font-mono text-[11.5px]', response.durationMs > 1000 ? 'text-warning' : 'text-muted-foreground')}>{response.durationMs} ms</span>
                      <span className="font-mono text-[11.5px] text-muted-foreground">{response.bytes} B</span>
                      <div className="ms-auto flex gap-1.5">
                        <Button variant="ghost" size="sm" disabled={!response.body} onClick={() => copy(response.body)}><Copy />{t('test.copyBody')}</Button>
                        <Button variant="ghost" size="sm" onClick={() => copy(toCurl(method, finalUrl, headers, previewEnvironment(body, environments).resolved, tenant))}>
                          <Terminal />{t('test.copyCurl')}
                        </Button>
                      </div>
                    </div>
                    <HeadersView headers={response.headers} label={t('journal.headers')} />
                    <BodyView body={response.body} label={t('journal.body')} empty={t('journal.noBody')} />
                  </div>
                )}
              </div>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
