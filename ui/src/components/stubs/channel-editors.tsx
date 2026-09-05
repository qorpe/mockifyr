import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { Braces, Cable, FileJson, Globe, Workflow } from 'lucide-react'
import { fetchGrpcDescriptors, importOpenApi, saveMessageMapping, saveStub } from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button, TabStrip } from '@qorpe/ui'
import { Input, Label } from '@/components/ui/field'
import { Select } from '@qorpe/ui'
import { selectOptions } from '@/components/ui/select-field'
import { JsonEditor, JsonField } from '@/components/ui/json-field'
import { Switch } from '@qorpe/ui'
import { StubEditorForm } from '@/components/stubs/stub-editor'

// The stub channels the Add flow offers (ADR 0010). HTTP renders the classic editor; the others are
// thin projections that always emit the exact JSON the host dialect expects — the JSON preview *is*
// the source of truth, the form only writes it.
export type StubChannel = 'http' | 'grpc' | 'graphql' | 'websocket' | 'openapi'

const CHANNELS: { id: StubChannel; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: 'http', icon: Globe },
  { id: 'grpc', icon: Workflow },
  { id: 'graphql', icon: Braces },
  { id: 'websocket', icon: Cable },
  { id: 'openapi', icon: FileJson },
]

/**
 * The "New stub" workspace (G18-pre): a channel choice first, then the channel's editor. The HTTP
 * channel is the unchanged classic Form/JSON editor; gRPC, GraphQL and WebSocket generate their
 * dialect JSON from a focused form with a live preview.
 */
export function NewStubWorkspace({ active, prefillUrl, onSaved, onDirtyChange }: {
  active: boolean
  prefillUrl?: string
  onSaved: (saved: boolean) => void
  onDirtyChange?: (dirty: boolean) => void
}) {
  const { t } = useTranslation()
  const [channel, setChannel] = useState<StubChannel>('http')
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* Same pilled-tabs language as every other view switcher (Form/JSON) — one segmented style. */}
      <div className="flex items-center gap-3 border-b border-border px-4 py-2">
        <span className="text-xs font-semibold text-muted-foreground">{t('channels.pick')}</span>
        <TabStrip
          label={t('channels.pick')}
          scope="channel"
          items={CHANNELS.map(({ id }) => ({ id, label: t(`channels.${id}`) }))}
          activeId={channel}
          onSelect={(id) => setChannel(id as StubChannel)}
        />
      </div>
      {channel === 'http' && (
        <StubEditorForm editing={null} initialTab="form" prefillUrl={prefillUrl} active={active}
          onSaved={() => onSaved(true)} onCancel={() => onSaved(false)} onDirtyChange={onDirtyChange} />
      )}
      {channel === 'grpc' && <GrpcStubForm onSaved={onSaved} />}
      {channel === 'graphql' && (
        <StubEditorForm editing={null} initialTab="form" active={active}
          template={{
            method: 'POST', urlValue: '/graphql', graphqlQuery: '{ hero { id name } }',
            responseBody: '{\n  "data": { "hero": { "id": "1", "name": "R2-D2" } }\n}', responseJsonBody: true,
          }}
          onSaved={() => onSaved(true)} onCancel={() => onSaved(false)} onDirtyChange={onDirtyChange} />
      )}
      {channel === 'websocket' && <WsMappingForm onSaved={onSaved} />}
      {channel === 'openapi' && <OpenApiImportForm onSaved={onSaved} />}
    </div>
  )
}

/**
 * OpenAPI import (G19c): paste a 3.x document (JSON or YAML), optionally wire resource-shaped path
 * pairs to the sandbox state directive, and import — the generated stubs are ordinary mappings.
 */
function OpenApiImportForm({ onSaved }: { onSaved: (saved: boolean) => void }) {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const [spec, setSpec] = useState('')
  const [stateful, setStateful] = useState(true)
  const [busy, setBusy] = useState(false)

  const runImport = async () => {
    setBusy(true)
    try {
      const { imported, mock } = await importOpenApi(tenant, spec, stateful)
      if (mock) { toast.message(t('editor.savedSample')); return }
      toast.success(t('openapi.imported', { count: imported }))
      void queryClient.invalidateQueries({ queryKey: ['stubs', tenant] })
      void queryClient.invalidateQueries({ queryKey: ['scenarios', tenant] })
      onSaved(true)
    } catch (e) {
      toast.error(t('openapi.failed') + ': ' + (e instanceof Error ? e.message : String(e)))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scroll-area flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-6 py-5">
      <p className="text-sm text-muted-foreground">{t('openapi.hint')}</p>
      <div className="min-h-[320px] flex-1">
        <JsonField fill value={spec} onChange={setSpec} lint={false} />
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <label className="flex items-center gap-2.5 text-sm">
          <Switch checked={stateful} onCheckedChange={setStateful} />
          {t('openapi.stateful')}
        </label>
        <span className="text-xs text-muted-foreground">{t('openapi.statefulHint')}</span>
        <div className="ms-auto">
          <Button variant="primary" onClick={() => void runImport()} disabled={busy || !spec.trim()}>
            {t('openapi.import')}
          </Button>
        </div>
      </div>
    </div>
  )
}

// Shared two-pane scaffold: the channel form on the left, the emitted dialect JSON live on the right.
function ChannelScaffold({ children, json, onJsonChange, onSave, onCancel, hint }: {
  children: React.ReactNode
  json: string
  onJsonChange: (next: string) => void
  onSave: () => void
  onCancel?: () => void
  hint: string
}) {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-4 overflow-y-auto p-4 lg:grid-cols-2">
        <div className="space-y-4">{children}</div>
        <div className="flex min-h-64 flex-col">
          <Label>{t('channels.emitted')}</Label>
          <p className="mb-2 text-xs text-muted-foreground">{hint}</p>
          <JsonEditor value={json} onChange={onJsonChange} className="min-h-48 flex-1" />
        </div>
      </div>
      <div className="flex justify-end gap-2 border-t border-border px-4 py-3">
        {onCancel && <Button variant="ghost" onClick={onCancel}>{t('editor.cancel')}</Button>}
        <Button variant="primary" onClick={onSave}>{t('editor.save')}</Button>
      </div>
    </div>
  )
}

// A gRPC stub is an ordinary stub (G13): urlPath /pkg.Service/Method + equalToJson → jsonBody. The
// method list comes from the host's loaded descriptors; without any, the form says how to add one.
function GrpcStubForm({ onSaved }: { onSaved: (saved: boolean) => void }) {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const { data } = useQuery({ queryKey: ['grpc-descriptors'], queryFn: fetchGrpcDescriptors })
  const methods = useMemo(
    () => (data?.grpc.services ?? []).flatMap((s) => s.methods.map((m) => ({ ...m, service: s.service }))),
    [data],
  )
  const [path, setPath] = useState('')
  const [request, setRequest] = useState('{\n  "name": "Tom"\n}')
  const [response, setResponse] = useState('{\n  "message": "Hello Tom"\n}')
  const [override, setOverride] = useState<string | null>(null)

  const generated = useMemo(() => {
    const compact = (s: string) => { try { return JSON.stringify(JSON.parse(s)) } catch { return null } }
    const reply = (() => { try { return JSON.parse(response) } catch { return null } })()
    return JSON.stringify({
      request: {
        method: 'POST',
        urlPath: path || methods[0]?.path || '/pkg.Service/Method',
        ...(compact(request) ? { bodyPatterns: [{ equalToJson: compact(request) }] } : {}),
      },
      response: { status: 200, jsonBody: reply ?? {} },
    }, null, 2)
  }, [path, methods, request, response])

  const json = override ?? generated

  async function save() {
    try { JSON.parse(json) } catch { toast.error(t('editor.invalidJson')); return }
    const { mock } = await saveStub(tenant, json)
    toast[mock ? 'message' : 'success'](mock ? t('editor.savedSample') : t('editor.saved'))
    void queryClient.invalidateQueries({ queryKey: ['stubs', tenant] })
    onSaved(true)
  }

  return (
    <ChannelScaffold json={json} onJsonChange={setOverride} onSave={() => void save()} hint={t('channels.grpcHint')}>
      <div>
        <Label>{t('channels.grpcMethod')}</Label>
        {methods.length > 0 ? (
          <Select aria-label={t('channels.grpcMethod')} value={path || methods[0]?.path || ''}
            onChange={(v) => { setPath(v); setOverride(null) }}
            options={methods.map((m) => ({ value: m.path, label: `${m.service}/${m.method}` }))} />
        ) : (
          <>
            <Input value={path} placeholder="/pkg.Service/Method" onChange={(e) => { setPath(e.target.value); setOverride(null) }} />
            <p className="mt-1.5 text-xs text-muted-foreground">{t('channels.noDescriptors')}</p>
          </>
        )}
      </div>
      <div>
        <Label>{t('channels.grpcRequest')}</Label>
        <JsonField height={140} value={request} onChange={(v) => { setRequest(v); setOverride(null) }} />
      </div>
      <div>
        <Label>{t('channels.grpcResponse')}</Label>
        <JsonField height={140} value={response} onChange={(v) => { setResponse(v); setOverride(null) }} />
      </div>
    </ChannelScaffold>
  )
}

// A WebSocket message-mapping (G15d): trigger (body matcher | connection) + send actions. Posts to
// /__admin/message-mappings — a separate resource from request/response stubs.
function WsMappingForm({ onSaved }: { onSaved: (saved: boolean) => void }) {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const [onConnect, setOnConnect] = useState(false)
  const [op, setOp] = useState('equalTo')
  const [match, setMatch] = useState('ping')
  const [reply, setReply] = useState('pong')
  const [broadcast, setBroadcast] = useState(false)
  const [override, setOverride] = useState<string | null>(null)

  const generated = useMemo(() => JSON.stringify({
    trigger: onConnect ? { type: 'connection' } : { message: { body: { [op]: match } } },
    actions: [{
      type: 'send',
      message: { body: { data: reply } },
      ...(broadcast ? { channelTarget: { type: 'broadcast' } } : {}),
    }],
  }, null, 2), [onConnect, op, match, reply, broadcast])

  const json = override ?? generated

  async function save() {
    try { JSON.parse(json) } catch { toast.error(t('editor.invalidJson')); return }
    const { mock } = await saveMessageMapping(tenant, json)
    toast[mock ? 'message' : 'success'](mock ? t('editor.savedSample') : t('channels.wsSaved'))
    void queryClient.invalidateQueries({ queryKey: ['message-mappings', tenant] })
    onSaved(true)
  }

  return (
    <ChannelScaffold json={json} onJsonChange={setOverride} onSave={() => void save()} hint={t('channels.wsHint')}>
      <div className="flex items-center gap-2">
        <input id="ws-onconnect" type="checkbox" checked={onConnect}
          onChange={(e) => { setOnConnect(e.target.checked); setOverride(null) }} className="size-3.5 accent-[var(--accent)]" />
        <label htmlFor="ws-onconnect" className="text-sm">{t('channels.wsOnConnect')}</label>
      </div>
      {!onConnect && (
        <div className="grid grid-cols-[8rem_1fr] gap-3">
          <div>
            <Label>{t('channels.wsMatcher')}</Label>
            <Select aria-label={t('channels.wsMatcher')} value={op}
              onChange={(v) => { setOp(v); setOverride(null) }}
              options={selectOptions(['equalTo', 'contains', 'matches', 'equalToJson', 'matchesJsonPath'])} />
          </div>
          <div>
            <Label>{t('editor.value')}</Label>
            <Input className="font-mono text-[12.5px]" value={match}
              onChange={(e) => { setMatch(e.target.value); setOverride(null) }} />
          </div>
        </div>
      )}
      <div>
        <Label>{t('channels.wsReply')}</Label>
        {/* The framed JSON editor for consistency (#193); lint off — a reply may be plain text. */}
        <JsonField height={120} lint={false} value={reply} onChange={(v) => { setReply(v); setOverride(null) }} />
        <p className="mt-1.5 text-xs text-muted-foreground">{t('channels.wsTemplating')}</p>
      </div>
      <div className="flex items-center gap-2">
        <input id="ws-broadcast" type="checkbox" checked={broadcast}
          onChange={(e) => { setBroadcast(e.target.checked); setOverride(null) }} className="size-3.5 accent-[var(--accent)]" />
        <label htmlFor="ws-broadcast" className="text-sm">{t('channels.wsBroadcast')}</label>
      </div>
    </ChannelScaffold>
  )
}
