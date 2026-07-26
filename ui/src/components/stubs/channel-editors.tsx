import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { toast } from 'sonner'
import { Braces, Cable, Globe, Workflow } from 'lucide-react'
import { fetchGrpcDescriptors, saveMessageMapping, saveStub } from '@/lib/api'
import { useUi } from '@/components/providers'
import { Button } from '@/components/ui/button'
import { Input, Label, NativeSelect, Textarea } from '@/components/ui/field'
import { JsonEditor, JsonField } from '@/components/ui/json-editor'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { StubEditorForm } from '@/components/stubs/stub-editor'

// The stub channels the Add flow offers (ADR 0010). HTTP renders the classic editor; the others are
// thin projections that always emit the exact JSON the host dialect expects — the JSON preview *is*
// the source of truth, the form only writes it.
export type StubChannel = 'http' | 'grpc' | 'graphql' | 'websocket'

const CHANNELS: { id: StubChannel; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: 'http', icon: Globe },
  { id: 'grpc', icon: Workflow },
  { id: 'graphql', icon: Braces },
  { id: 'websocket', icon: Cable },
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
        <Tabs value={channel} onValueChange={(v) => setChannel(v as StubChannel)}>
          <TabsList>
            {CHANNELS.map(({ id, icon: Icon }) => (
              <TabsTrigger key={id} value={id} className="inline-flex items-center gap-1.5">
                <Icon className="size-3.5" />{t(`channels.${id}`)}
              </TabsTrigger>
            ))}
          </TabsList>
        </Tabs>
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
          <NativeSelect value={path || methods[0]?.path} onChange={(e) => { setPath(e.target.value); setOverride(null) }}>
            {methods.map((m) => <option key={m.path} value={m.path}>{m.service}/{m.method}</option>)}
          </NativeSelect>
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
            <NativeSelect value={op} onChange={(e) => { setOp(e.target.value); setOverride(null) }}>
              {['equalTo', 'contains', 'matches', 'equalToJson', 'matchesJsonPath'].map((o) => <option key={o} value={o}>{o}</option>)}
            </NativeSelect>
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
        <Textarea rows={4} className="font-mono text-[12.5px]" value={reply}
          onChange={(e) => { setReply(e.target.value); setOverride(null) }} />
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
