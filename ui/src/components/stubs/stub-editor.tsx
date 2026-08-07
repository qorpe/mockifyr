import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useForm, useFieldArray, type Resolver } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { useTranslation } from 'react-i18next'
import { FlaskConical, Plus, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { fetchEnvironments, importMappings, saveStub, type Stub } from '@/lib/api'
import { BODY_OPS, BODY_SUB_OPS, emptyStub, FAULTS, fromMapping, MATCH_OPS, stubSchema, suggestName, toJson, URL_MATCH, type StubForm } from '@/lib/stub-schema'
import { previewEnvironment, type EnvironmentKey } from '@/lib/environments'
import { Sheet, SheetContent, SheetHeader } from '@/components/ui/sheet'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ProtocolChip } from '@/components/ui/badges'
import { Input, Label, NativeSelect, Textarea } from '@/components/ui/field'
import { Switch } from '@qorpe/ui'
import { Button } from '@qorpe/ui'
import { JsonField } from '@/components/ui/json-editor'
import { HelpersButton } from '@/components/templating/helpers-dialog'
import { TestRequestDialog, type TestSeed } from '@/components/stubs/test-request-dialog'

function seedFrom(stub: Stub | null, prefillUrl?: string, template?: Partial<StubForm>): StubForm {
  if (!stub) return { ...emptyStub, ...(template ?? {}), ...(prefillUrl ? { urlValue: prefillUrl } : {}) }
  // Prefer a full reverse-map of the mapping the host returned (no field is lost on edit); fall back to
  // the projected fields when only those are available (e.g. sample mode).
  if (stub.raw) return fromMapping(stub.raw)
  return { ...emptyStub, name: stub.name ?? '', method: stub.method === 'ANY' ? 'GET' : stub.method, urlValue: stub.url, priority: stub.priority, scenarioName: stub.scenario ?? '' }
}

/**
 * Live preview for a field that may reference environments (#165). Shows what the SERVER will
 * substitute at serve time and flags references naming no key. This is display only — the value
 * stored in the mapping is always the raw {{key}} expression.
 */
function EnvPreview({ url, environments, t }: { url: string; environments: EnvironmentKey[]; t: (k: string, o?: Record<string, unknown>) => string }) {
  const { resolved, unknown, changed } = previewEnvironment(url ?? '', environments)
  if (!changed && unknown.length === 0) return null
  return (
    <div className="mt-1 space-y-0.5">
      {changed && <p className="break-all font-mono text-[11.5px] text-success">→ {resolved}</p>}
      {unknown.map((name) => (
        <p key={name} className="text-[11.5px] text-warning">{t('env.unknown', { name })}</p>
      ))}
    </div>
  )
}

/**
 * Sheet wrapper kept for deep-link / standalone use. The tabbed Stubs workspace embeds
 * {@link StubEditorForm} directly instead.
 */
export function StubEditor({ open, onOpenChange, editing, onSaved, initialTab = 'form' }: {
  open: boolean
  onOpenChange: (o: boolean) => void
  editing: Stub | null
  onSaved: () => void
  initialTab?: 'form' | 'json'
}) {
  const { t } = useTranslation()
  const importing = !editing && initialTab === 'json'
  const title = editing ? t('editor.editTitle') : importing ? t('editor.importTitle') : t('editor.newTitle')
  const description = importing ? t('editor.importDesc') : t('stubs.subtitle')
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent>
        <SheetHeader title={title} description={description} />
        <StubEditorForm editing={editing} initialTab={initialTab} onSaved={() => { onSaved(); onOpenChange(false) }} onCancel={() => onOpenChange(false)} />
      </SheetContent>
    </Sheet>
  )
}

/**
 * The stub editor body — Form + JSON tabs, validation, and Save. Renders inline (no Sheet) so the
 * Stubs workspace can host one per open tab. Reports unsaved state via onDirtyChange for the tab dot.
 */
export function StubEditorForm({ editing, initialTab = 'form', prefillUrl, template, active = true, onSaved, onCancel, onDirtyChange }: {
  editing: Stub | null
  initialTab?: 'form' | 'json'
  prefillUrl?: string
  /** Seed values for a NEW stub (channel templates, e.g. GraphQL — ADR 0010). */
  template?: Partial<StubForm>
  /** Only the active (visible) tab's editor listens for the save shortcut — every open tab stays mounted. */
  active?: boolean
  onSaved: (saved: boolean) => void
  onCancel?: () => void
  onDirtyChange?: (dirty: boolean) => void
}) {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  // The tenant's keys, for the live preview only. Same query key as the environments page, so an
  // edit there is reflected here without a reload.
  const { data: environmentData } = useQuery({
    queryKey: ['environments', tenant],
    queryFn: () => fetchEnvironments(tenant),
  })
  const environments = environmentData?.environments ?? []
  // An UNKNOWN custom matcher (a G10 extension) has no form representation — a Form-tab save would
  // rebuild the mapping from the form and silently DROP it, so such stubs edit as JSON only.
  // GraphQL's graphql-body-matcher is first-class in the form (ADR 0010) and stays editable.
  const rawMatcher = (editing?.raw as { request?: { customMatcher?: { name?: string } } } | undefined)?.request?.customMatcher
  const jsonOnly = typeof rawMatcher === 'object' && !!rawMatcher &&
    (rawMatcher.name ?? '').toLowerCase() !== 'graphql-body-matcher'
  const [tab, setTab] = useState(jsonOnly ? 'json' : initialTab)
  const [rawJson, setRawJson] = useState('')
  const [saving, setSaving] = useState(false)
  const [testOpen, setTestOpen] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  // zodResolver's inferred generic clashes with the coerce()'d number fields (and pnpm's duplicate RHF
  // types), so cast the resolver — the schema still validates at runtime.
  const form = useForm<StubForm>({ resolver: zodResolver(stubSchema) as unknown as Resolver<StubForm>, defaultValues: emptyStub })
  const { register, control, reset, getValues, watch, handleSubmit, formState: { errors } } = form

  const initialJson = useRef('')
  useEffect(() => {
    const seed = seedFrom(editing, prefillUrl, template)
    reset(seed)
    // When editing, the JSON tab shows the exact mapping the host returned (so nothing is lost even if
    // the form doesn't surface a field); for a new stub it mirrors the form.
    const seeded = editing?.raw ? JSON.stringify(editing.raw, null, 2) : toJson(seed)
    setRawJson(seeded)
    initialJson.current = seeded
    setTab(jsonOnly ? 'json' : initialTab)
  }, [editing, reset, initialTab, prefillUrl, template, jsonOnly])

  // Report unsaved state for the tab's dot: form edits (RHF isDirty) or a raw JSON change.
  const dirty = form.formState.isDirty || (tab === 'json' && rawJson !== initialJson.current)
  useEffect(() => { onDirtyChange?.(dirty) }, [dirty, onDirtyChange])

  // Test runner (#203): only stubs a plain HTTP request can exercise. New stubs are HTTP.
  const testable = (editing?.protocol ?? 'http') === 'http' || editing?.protocol === 'graphql'

  // Seeds the runner from the form's CURRENT values (unsaved edits included): the exact-match
  // matchers become a request that would hit this stub — equalTo headers/params and the first
  // equalTo/equalToJson body pattern; looser matchers (contains, regex…) can't be inverted and are
  // left for the user to fill in.
  const testSeed = (): TestSeed => {
    const v = getValues()
    const headers = v.headers.filter((h) => h.name.trim() && h.operator === 'equalTo').map((h) => ({ name: h.name, value: h.value }))
    const body = v.bodyPatterns.find((b) => b.operator === 'equalTo' || b.operator === 'equalToJson')?.value ?? ''
    if (body && !headers.some((h) => h.name.toLowerCase() === 'content-type')) {
      try { JSON.parse(body); headers.push({ name: 'Content-Type', value: 'application/json' }) } catch { /* not JSON — send as-is */ }
    }
    return {
      method: v.method === 'ANY' ? 'GET' : v.method,
      url: v.urlValue,
      params: v.queryParams.filter((q) => q.name.trim() && q.operator === 'equalTo').map((q) => ({ name: q.name, value: q.value })),
      headers,
      body,
    }
  }

  // Keep the JSON preview live while editing the form (form is the source of truth on the Form tab).
  useEffect(() => {
    const sub = watch((values) => { if (tab === 'form') setRawJson(toJson(values as StubForm)) })
    return () => sub.unsubscribe()
  }, [watch, tab])

  const headers = useFieldArray({ control, name: 'headers' })
  const bodyPatterns = useFieldArray({ control, name: 'bodyPatterns' })
  const responseHeaders = useFieldArray({ control, name: 'responseHeaders' })
  const webhookHeaders = useFieldArray({ control, name: 'webhookHeaders' })

  async function persist() {
    let json = rawJson
    if (tab === 'form') {
      // Environment references are stored VERBATIM (#165). They used to be resolved here, which
      // froze today's value into the mapping; the engine now substitutes at serve time, so changing
      // a key's active value changes what this stub serves without touching it again.
      const values = { ...getValues() }
      for (const field of ['webhookUrl', 'proxyBaseUrl'] as const) {
        // Still warn about a reference that names no key: it would reach the server as literal text.
        for (const name of previewEnvironment(values[field] ?? '', environments).unknown) {
          toast.warning(t('env.unknown', { name }))
        }
      }
      json = toJson(values)
    }
    let parsed: unknown
    try { parsed = JSON.parse(json) } catch { toast.error(t('editor.invalidJson')); return }
    // A bundle export — either a {"mappings":[…]} wrapper or a bare top-level array — goes through the
    // bulk-import endpoint; a single mapping is a create/update. Editing an existing stub is always the latter.
    const isBundle = !editing && (Array.isArray(parsed) || (typeof parsed === 'object' && parsed !== null && Array.isArray((parsed as { mappings?: unknown }).mappings)))
    setSaving(true)
    const { mock } = isBundle ? await importMappings(tenant, json) : await saveStub(tenant, json, editing?.id)
    // A bundle may have carried an environments section (#198) — drop the cached keys so the
    // Environments page and the {{key}} previews pick up what the import just restored.
    if (isBundle && !mock) void queryClient.invalidateQueries({ queryKey: ['environments', tenant] })
    setSaving(false)
    toast[mock ? 'message' : 'success'](mock ? t('editor.savedSample') : t('editor.saved'))
    initialJson.current = json
    onSaved(!mock)
  }

  function onPickFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (file) void file.text().then(setRawJson)
    event.target.value = '' // allow re-picking the same file
  }

  // Live JSON validity for the JSON tab: surfaces an inline error, red border, and blocks Save.
  const jsonError = (() => {
    if (tab !== 'json') return null
    try { JSON.parse(rawJson); return null } catch { return t('editor.invalidJson') }
  })()

  // Ctrl+S / Cmd+S saves via the same path as the Save button (browser save dialog suppressed). The ref
  // re-captures the latest closure every render so the window listener binds once per `active` flip.
  const shortcutSave = useRef(() => {})
  shortcutSave.current = () => { if (!saving && !jsonError) void handleSubmit(persist, () => setTab('form'))() }
  useEffect(() => {
    if (!active) return
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 's') {
        e.preventDefault()
        shortcutSave.current()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active])

  return (
    <div className="flex h-full min-h-0 flex-col">
        <Tabs value={tab} onValueChange={(v) => setTab(v as 'form' | 'json')} className="flex min-h-0 flex-1 flex-col">
          <div className="flex items-center gap-3 px-6 pt-4">
            <TabsList>
              <TabsTrigger value="form" disabled={jsonOnly}>{t('editor.form')}</TabsTrigger>
              <TabsTrigger value="json">JSON</TabsTrigger>
            </TabsList>
            {editing && <ProtocolChip protocol={editing.protocol} />}
            {jsonOnly && <span className="text-xs text-muted-foreground">{t('editor.jsonOnly')}</span>}
            {/* Test runner (#203) — HTTP-transported stubs only: a plain HTTP probe cannot exercise
                a gRPC or WebSocket stub, so the button hides rather than sending something misleading. */}
            {testable && (
              <Button variant="outline" size="sm" className="ms-auto" onClick={() => setTestOpen(true)}>
                <FlaskConical />{t('test.button')}
              </Button>
            )}
          </div>

          <TabsContent value="form" className="scroll-area min-h-0 flex-1 space-y-6 overflow-y-auto px-6 py-5">
            {/* Details — friendly name + description (WireMock name + metadata.description). */}
            <Section title={t('editor.details')}>
              <div><Label>{t('editor.name')}</Label><Input {...register('name')} placeholder={suggestName(watch('method'), watch('urlValue')) || 'Get users'} /></div>
              <div><Label>{t('editor.description')}</Label><Textarea rows={2} {...register('description')} placeholder={t('editor.descriptionHint')} /></div>
            </Section>

            {/* Request */}
            <Section title={t('editor.request')}>
              <div className="grid grid-cols-[110px_1fr] gap-3">
                <div><Label>{t('stubs.method')}</Label><NativeSelect {...register('method')}>{['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS', 'ANY'].map((m) => <option key={m}>{m}</option>)}</NativeSelect></div>
                <div><Label>{t('editor.urlMatch')}</Label>
                  <div className="grid grid-cols-[130px_1fr] gap-2">
                    <NativeSelect {...register('urlMatchType')}>{URL_MATCH.map((u) => <option key={u} value={u}>{u}</option>)}</NativeSelect>
                    <Input {...register('urlValue')} placeholder="/api/v2/…" className={cn('font-mono', errors.urlValue && 'border-danger')} />
                  </div>
                  <FieldError msg={errors.urlValue?.message} />
                </div>
              </div>
              <Rows label={t('editor.headerMatchers')} fields={headers.fields} onAdd={() => headers.append({ name: '', operator: 'equalTo', value: '' })} onRemove={headers.remove}
                render={(i) => (<>
                  <Input {...register(`headers.${i}.name`)} placeholder="Header" />
                  <NativeSelect {...register(`headers.${i}.operator`)}>{MATCH_OPS.map((o) => <option key={o}>{o}</option>)}</NativeSelect>
                  <Input {...register(`headers.${i}.value`)} placeholder={t('editor.value')} />
                </>)} />
              <Rows label={t('editor.bodyMatchers')} fields={bodyPatterns.fields} onAdd={() => bodyPatterns.append({ operator: 'equalToJson', value: '', subOperator: '', subValue: '' })} onRemove={bodyPatterns.remove}
                render={(i) => {
                  // Path matchers get the object form's fields: expression + optional sub-matcher
                  // (e.g. $.Header.activityName equalTo "DSL_Change"). Other operators are a single value.
                  const op = watch(`bodyPatterns.${i}.operator`)
                  const isPath = op === 'matchesJsonPath' || op === 'matchesXPath'
                  return (<>
                    <NativeSelect {...register(`bodyPatterns.${i}.operator`)}>{BODY_OPS.map((o) => <option key={o}>{o}</option>)}</NativeSelect>
                    {isPath ? (
                      <div className="grid grid-cols-[minmax(0,1.3fr)_120px_minmax(0,1fr)] gap-2">
                        <Input {...register(`bodyPatterns.${i}.value`)} placeholder={op === 'matchesXPath' ? '//node' : '$.path.to.field'} className="font-mono" />
                        <NativeSelect {...register(`bodyPatterns.${i}.subOperator`)}>{BODY_SUB_OPS.map((o) => <option key={o} value={o}>{o || t('editor.none')}</option>)}</NativeSelect>
                        <Input {...register(`bodyPatterns.${i}.subValue`)} placeholder={t('editor.value')} className="font-mono" />
                      </div>
                    ) : (
                      <Input {...register(`bodyPatterns.${i}.value`)} placeholder={t('editor.value')} className="font-mono" />
                    )}
                  </>)
                }} twoCol />
            </Section>

            {/* Response */}
            {(watch('graphqlQuery') ?? '').trim() !== '' && (
              <Section title="GraphQL">
                <Label>{t('channels.graphqlQuery')}</Label>
                <Textarea rows={5} className="font-mono text-[12.5px]" {...register('graphqlQuery')} />
                <p className="mt-1.5 text-xs text-muted-foreground">{t('channels.graphqlNormalize')}</p>
                <div className="mt-3 grid grid-cols-2 gap-3">
                  <div>
                    <Label>{t('channels.graphqlVariables')}</Label>
                    <Textarea rows={3} className="font-mono text-[12.5px]" placeholder="{ }" {...register('graphqlVariables')} />
                  </div>
                  <div>
                    <Label>{t('channels.graphqlOperation')}</Label>
                    <Input {...register('graphqlOperationName')} />
                  </div>
                </div>
              </Section>
            )}

            <Section title={t('editor.response')}>
              <div className="grid grid-cols-2 gap-3">
                <div><Label>{t('editor.statusCode')}</Label><Input type="number" {...register('responseStatus')} className={cn(errors.responseStatus && 'border-danger')} /><FieldError msg={errors.responseStatus?.message} /></div>
                <div><Label>{t('editor.priority')}</Label><Input type="number" {...register('priority')} className={cn(errors.priority && 'border-danger')} /><FieldError msg={errors.priority?.message} /></div>
              </div>
              <Rows label={t('editor.responseHeaders')} fields={responseHeaders.fields} onAdd={() => responseHeaders.append({ name: '', value: '' })} onRemove={responseHeaders.remove}
                render={(i) => (<>
                  <Input {...register(`responseHeaders.${i}.name`)} placeholder="Header" />
                  <Input {...register(`responseHeaders.${i}.value`)} placeholder={t('editor.value')} />
                </>)} twoCol />
              <div><Label>{t('editor.body')}</Label>
                <JsonField value={watch('responseBody') ?? ''} onChange={(v) => form.setValue('responseBody', v, { shouldDirty: true })} height={360} lint={false} minimal />
              </div>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <label className="flex items-center gap-2.5 text-sm">
                  <Switch checked={watch('useTemplating')} onCheckedChange={(v) => form.setValue('useTemplating', v)} />
                  {t('editor.templating')}
                </label>
                <HelpersButton />
              </div>
            </Section>

            {/* Behavior */}
            <Section title={t('editor.behavior')}>
              <div className="grid grid-cols-2 gap-3">
                <div><Label>{t('editor.delay')}</Label><Input type="number" {...register('fixedDelayMs')} placeholder="0" className={cn(errors.fixedDelayMs && 'border-danger')} /><FieldError msg={errors.fixedDelayMs?.message} /></div>
                <div><Label>{t('editor.fault')}</Label><NativeSelect {...register('fault')}>{FAULTS.map((f) => <option key={f} value={f}>{f || t('editor.none')}</option>)}</NativeSelect></div>
              </div>
              <div>
                <Label>{t('editor.proxy')}</Label><Input {...register('proxyBaseUrl')} placeholder="https://upstream.example.com" className="font-mono" />
                <EnvPreview url={watch('proxyBaseUrl')} environments={environments} t={t} />
              </div>
              <div className="grid grid-cols-3 gap-3">
                <div><Label>{t('stubs.scenario')}</Label><Input {...register('scenarioName')} placeholder="Checkout" className={cn(errors.scenarioName && 'border-danger')} /><FieldError msg={errors.scenarioName?.message} /></div>
                <div><Label>{t('editor.requiredState')}</Label><Input {...register('requiredScenarioState')} placeholder="Started" /></div>
                <div><Label>{t('editor.newState')}</Label><Input {...register('newScenarioState')} placeholder="Paid" /></div>
              </div>
            </Section>

            {/* Webhook / callback */}
            <Section title={t('editor.webhook')}>
              <p className="-mt-1 text-xs text-muted-foreground">{t('editor.webhookHint')}</p>
              <div className="grid grid-cols-[110px_1fr_120px] gap-3">
                <div><Label>{t('stubs.method')}</Label><NativeSelect {...register('webhookMethod')}>{['POST', 'PUT', 'GET', 'DELETE', 'PATCH'].map((m) => <option key={m}>{m}</option>)}</NativeSelect></div>
                <div>
                  <Label>{t('editor.webhookUrl')}</Label><Input {...register('webhookUrl')} placeholder="https://callback.example.com/hook" className="font-mono" />
                  <EnvPreview url={watch('webhookUrl')} environments={environments} t={t} />
                </div>
                <div><Label>{t('editor.delay')}</Label><Input type="number" {...register('webhookDelayMs')} placeholder="0" className={cn(errors.webhookDelayMs && 'border-danger')} /><FieldError msg={errors.webhookDelayMs?.message} /></div>
              </div>
              <Rows label={t('editor.webhookHeaders')} fields={webhookHeaders.fields} onAdd={() => webhookHeaders.append({ name: '', value: '' })} onRemove={webhookHeaders.remove}
                render={(i) => (<>
                  <Input {...register(`webhookHeaders.${i}.name`)} placeholder="Header" />
                  <Input {...register(`webhookHeaders.${i}.value`)} placeholder={t('editor.value')} />
                </>)} twoCol />
              <div><Label>{t('editor.webhookBody')}</Label>
                <JsonField value={watch('webhookBody') ?? ''} onChange={(v) => form.setValue('webhookBody', v, { shouldDirty: true })} height={300} lint={false} minimal />
              </div>
            </Section>
          </TabsContent>

          <TabsContent value="json" className="flex min-h-0 flex-1 flex-col gap-2 px-6 py-5">
            <span className={cn('text-xs', jsonError ? 'text-danger' : 'text-faint')}>{jsonError ?? 'JSON'}</span>
            <input ref={fileRef} type="file" accept=".json,application/json" hidden onChange={onPickFile} />
            <div className="min-h-[420px] flex-1">
              <JsonField fill value={rawJson} onChange={setRawJson} invalid={!!jsonError} onUpload={() => fileRef.current?.click()} />
            </div>
          </TabsContent>
        </Tabs>

        <div className="flex items-center justify-end gap-2 border-t border-border px-6 py-4">
          {onCancel && <Button variant="ghost" onClick={onCancel}>{t('editor.cancel')}</Button>}
          <Button variant="primary" onClick={handleSubmit(persist, () => setTab('form'))} disabled={saving || !!jsonError}>{t('editor.save')}</Button>
        </div>

        <TestRequestDialog open={testOpen} onOpenChange={setTestOpen} seed={testSeed} tenant={tenant} environments={environments} />
    </div>
  )
}

function FieldError({ msg }: { msg?: string }) {
  return msg ? <p className="mt-1 text-xs text-danger">{msg}</p> : null
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="space-y-3">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-faint">{title}</h3>
      {children}
    </section>
  )
}

function Rows({ label, fields, onAdd, onRemove, render, twoCol }: {
  label: string
  fields: { id: string }[]
  onAdd: () => void
  onRemove: (i: number) => void
  render: (i: number) => React.ReactNode
  twoCol?: boolean
}) {
  return (
    <div>
      <div className="mb-1.5 flex items-center justify-between">
        <Label className="mb-0">{label}</Label>
        <Button type="button" variant="ghost" size="iconSm" onClick={onAdd} aria-label="Add"><Plus /></Button>
      </div>
      <div className="space-y-2">
        {fields.map((f, i) => (
          <div key={f.id} className={`grid ${twoCol ? 'grid-cols-[minmax(0,1fr)_minmax(0,2fr)_auto]' : 'grid-cols-[minmax(0,1fr)_130px_minmax(0,1fr)_auto]'} items-center gap-2`}>
            {render(i)}
            <Button type="button" variant="ghost" size="iconSm" onClick={() => onRemove(i)} className="text-muted-foreground"><Trash2 /></Button>
          </div>
        ))}
        {fields.length === 0 && <p className="text-xs text-faint">—</p>}
      </div>
    </div>
  )
}
