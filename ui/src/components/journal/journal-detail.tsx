import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { AlertTriangle, ArrowUpRight, Clock } from 'lucide-react'
import { fetchJournalDetail, fetchStubs, type JournalWebhook } from '@/lib/api'
import { Sheet } from '@qorpe/ui'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { MethodChip } from '@/components/ui/badges'
// Shared with the stub test runner (#203) so both surfaces render HTTP traffic identically.
import { BodyView as Body, HeadersView as Headers, StatusChip } from '@/components/ui/http-view'

/**
 * One callback delivery: the outbound request as actually sent (templates rendered) and, when the
 * target answered, its response. A callback not yet recorded (in flight / delayed) shows the
 * configured template with a "pending" note; a failed delivery shows the error.
 */
function WebhookCard({ webhook, t }: { webhook: JournalWebhook; t: (k: string) => string }) {
  return (
    <div className="space-y-4 rounded-xl border border-border p-4">
      <div className="flex items-center gap-2">
        <MethodChip method={webhook.method} />
        <span className="min-w-0 flex-1 break-all font-mono text-[12.5px] text-foreground">{webhook.url}</span>
        {webhook.response && <StatusChip status={webhook.response.status} />}
      </div>

      {!webhook.delivered && !webhook.error && (
        <p className="flex items-center gap-1.5 rounded-lg border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
          <Clock className="size-3.5 shrink-0" />{t('journal.callbackPending')}
        </p>
      )}
      {webhook.error && (
        <p className="flex items-center gap-1.5 rounded-lg border border-danger-border bg-danger-bg px-3 py-2 text-xs text-danger">
          <AlertTriangle className="size-3.5 shrink-0" />{t('journal.callbackFailed')}: {webhook.error}
        </p>
      )}

      <Headers headers={webhook.headers} label={t('journal.headers')} />
      <Body body={webhook.body ?? ''} label={t('journal.body')} empty={t('journal.noBody')} />

      {webhook.response && (
        <div className="space-y-4 border-t border-border pt-4">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-faint">{t('journal.callbackResponse')}</h4>
          <Headers headers={webhook.response.headers} label={t('journal.headers')} />
          <Body body={webhook.response.body ?? ''} label={t('journal.body')} empty={t('journal.noBody')} />
        </div>
      )}
    </div>
  )
}

/**
 * The "which stub answered this?" strip under the sheet header (#156). Resolved by stub id — never by
 * URL, since many stubs share a URL and differ only by header/body matchers. Three states: a clickable
 * reference that opens the exact stub in the Stubs editor, a "no longer exists" note when the stub was
 * deleted after the request was logged, and a "no stub matched" note for unmatched requests.
 */
function MatchedStubRow({ stubId, stubs, onOpen, t }: {
  stubId: string | null
  /** The tenant's stubs for name resolution, or null while loading / in sample mode (can't verify existence). */
  stubs: { id: string; name: string | null; url: string }[] | null
  onOpen: (stubId: string) => void
  t: (k: string) => string
}) {
  const matched = stubId && stubs ? stubs.find((s) => s.id === stubId) : undefined
  const gone = !!stubId && !!stubs && !matched
  return (
    <div className="flex items-center gap-2 border-b border-border bg-muted/30 px-6 py-2">
      <span className="text-[11px] font-semibold uppercase tracking-wide text-faint">{t('journal.matchedStub')}</span>
      {!stubId ? (
        <span className="text-xs text-muted-foreground">{t('journal.noStubMatched')}</span>
      ) : gone ? (
        <span className="text-xs text-muted-foreground">{t('journal.stubGone')}</span>
      ) : (
        <button
          onClick={() => onOpen(stubId)}
          className="inline-flex min-w-0 items-center gap-1 text-xs font-medium text-info hover:underline"
        >
          <span className="truncate font-mono">{matched?.name || matched?.url || stubId}</span>
          <ArrowUpRight className="size-3.5 shrink-0" />
        </button>
      )}
    </div>
  )
}

/**
 * Slide-over detail for one journal entry: Request / Response / Callback tabs with headers + bodies
 * (#122). Opens when `id` is set; the detail is fetched on demand so the list stays lean.
 */
export function JournalDetailSheet({ id, tenant, onClose }: { id: string | null; tenant: string; onClose: () => void }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  // The stub list resolves the matched stub's display name and whether it still exists (#156). It is
  // the same cached query the Stubs page uses, so this is usually a cache hit.
  const { data: stubsData } = useQuery({ queryKey: ['stubs', tenant], queryFn: () => fetchStubs(tenant), enabled: !!id })
  const { data, isLoading } = useQuery({
    queryKey: ['journal-detail', tenant, id],
    queryFn: () => fetchJournalDetail(tenant, id!),
    enabled: !!id,
    // A callback fires after its serve event is journaled (and may be delayed); keep the open sheet
    // fresh until every configured callback has a recorded outcome.
    refetchInterval: (query) => {
      const d = query.state.data
      return d && d.webhooks.some((w) => w.delivered ? !w.response && !w.error : !w.error) ? 2000 : false
    },
  })

  return (
    <Sheet
      open={!!id}
      onOpenChange={(o) => { if (!o) onClose() }}
      maxWidth={720}
      // The accessible name is the URL — what an operator would call this entry. While the fetch
      // is in flight there is no URL yet, so the screen's own noun stands in rather than an empty
      // string, which would leave the dialog unannounceable at exactly the wrong moment.
      title={data ? data.request.url : t('nav.journal')}
      // The header is a live strip — method chip, URL, status chip — not a string.
      header={data && (
        <div className="flex items-center gap-2.5 pe-10">
          <MethodChip method={data.request.method} />
          <span className="min-w-0 flex-1 truncate font-mono text-[13px] font-medium">{data.request.url}</span>
          {data.response && <StatusChip status={data.response.status} />}
        </div>
      )}
      // Each tab scrolls its own pane, so the panel must not wrap them in a second scroller.
      body="bleed"
      closeLabel={t('common.close')}
    >
        {isLoading || !data ? (
          <div className="space-y-3 p-6">{Array.from({ length: 6 }).map((_, i) => <div key={i} className="h-6 animate-pulse rounded bg-muted" />)}</div>
        ) : (
          <>
            <MatchedStubRow
              stubId={data.wasMatched ? data.stubId : null}
              stubs={stubsData?.mock ? null : stubsData?.stubs ?? null}
              onOpen={(sid) => { onClose(); void navigate(`/stubs?open=${sid}`) }}
              t={t}
            />
            <Tabs defaultValue="request" className="flex min-h-0 flex-1 flex-col">
              <div className="px-6 pt-4">
                <TabsList>
                  <TabsTrigger value="request">{t('journal.tabRequest')}</TabsTrigger>
                  <TabsTrigger value="response">{t('journal.tabResponse')}</TabsTrigger>
                  <TabsTrigger value="webhook">{t('journal.tabCallback')}{data.webhooks.length > 0 ? ` (${data.webhooks.length})` : ''}</TabsTrigger>
                </TabsList>
              </div>

              <TabsContent value="request" className="scroll-area min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-5">
                <Headers headers={data.request.headers} label={t('journal.headers')} />
                <Body body={data.request.body} label={t('journal.body')} empty={t('journal.noBody')} />
              </TabsContent>

              <TabsContent value="response" className="scroll-area min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-5">
                {data.response ? (
                  <>
                    <Headers headers={data.response.headers} label={t('journal.headers')} />
                    <Body body={data.response.body} label={t('journal.body')} empty={t('journal.noBody')} />
                  </>
                ) : (
                  <p className="text-sm text-muted-foreground">{t('journal.noResponse')}</p>
                )}
              </TabsContent>

              <TabsContent value="webhook" className="scroll-area min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-5">
                {data.webhooks.length === 0 ? (
                  <p className="text-sm text-muted-foreground">{t('journal.noCallback')}</p>
                ) : (
                  data.webhooks.map((w, i) => <WebhookCard key={i} webhook={w} t={t} />)
                )}
              </TabsContent>
            </Tabs>
          </>
        )}
    </Sheet>
  )
}
