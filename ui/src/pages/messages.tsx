import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Inbox, Mail, MessageSquareText, Paperclip, RefreshCw, Trash2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import {
  type CapturedMessage, deleteMessage, fetchMessages, messageAttachmentUrl, type MessageChannel, resetMessages,
} from '@/lib/api'
import { Button } from '@/components/ui/button'
import { SearchBox } from '@/components/ui/search-box'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'

// "26 B" for tiny payloads, "1.4 KB" above — a 26-byte attachment must not read as "0.0 KB".
const formatSize = (bytes: number) => (bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`)

// The default OTP shape (G18d): 4–8 consecutive digits. Extraction is display-side sugar; the
// server-side verify endpoint (G18f) is the API tests use.
const extractOtp = (body: string) => /\b\d{4,8}\b/.exec(body)?.[0] ?? null

// The captured-message inbox (G18c, ADR 0009): mail and SMS the application under test actually
// sent, tenant-scoped. Messages are traffic (like the journal), not stubs — so the page is a
// reader: list + filters on the left, the selected message on the right.
export function MessagesPage() {
  const { t, i18n } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const [channel, setChannel] = useState<MessageChannel | null>(null)
  const [search, setSearch] = useState('')
  const { data, isLoading, refetch } = useQuery({
    queryKey: ['messages', tenant],
    queryFn: () => fetchMessages(tenant),
    refetchInterval: 5000,
  })
  const all = useMemo(() => data?.messages ?? [], [data])
  const messages = useMemo(() => all.filter((m) => {
    if (channel && m.channel !== channel) return false
    if (!search.trim()) return true
    const q = search.toLowerCase()
    return [m.subject ?? '', m.body, m.from, ...m.to].some((v) => v.toLowerCase().includes(q))
  }), [all, channel, search])

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const selected = messages.find((m) => m.id === selectedId) ?? null
  const [confirmClear, setConfirmClear] = useState(false)

  // SMS mode is a thread view: one row per recipient number, the conversation on the right.
  const smsMode = channel === 'sms'
  const [selectedNumber, setSelectedNumber] = useState<string | null>(null)
  const threads = useMemo(() => {
    if (!smsMode) return []
    const byNumber = new Map<string, CapturedMessage[]>()
    for (const m of messages) {
      const key = m.to[0] ?? '—'
      byNumber.set(key, [...(byNumber.get(key) ?? []), m])
    }
    return [...byNumber.entries()].map(([number, list]) => ({ number, list }))
  }, [smsMode, messages])
  const thread = threads.find((x) => x.number === selectedNumber) ?? null

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['messages', tenant] })

  async function remove(id: string) {
    await deleteMessage(tenant, id)
    if (selectedId === id) setSelectedId(null)
    refresh()
    toast.success(t('messages.deleted'))
  }

  async function clearAll() {
    await resetMessages(tenant)
    setSelectedId(null)
    refresh()
    toast.success(t('messages.cleared'))
  }

  const counts = useMemo(() => ({
    email: all.filter((m) => m.channel === 'email').length,
    sms: all.filter((m) => m.channel === 'sms').length,
  }), [all])

  return (
    <div className="flex h-full min-h-0 overflow-hidden">
      <aside className="flex w-[340px] shrink-0 flex-col overflow-hidden">
        <div className="flex flex-col gap-2.5 p-3">
          <div className="flex items-center gap-2">
            <h1 className="text-sm font-semibold">{t('nav.messages')}</h1>
            <span className="rounded-full bg-muted px-1.5 text-[11px] tabular-nums text-muted-foreground">{all.length}</span>
            <div className="ms-auto flex gap-0.5">
              <Button variant="ghost" size="iconSm" aria-label={t('common.refresh')} onClick={() => void refetch()}><RefreshCw /></Button>
              <Button variant="ghost" size="iconSm" aria-label={t('messages.clearAll')} disabled={!all.length}
                onClick={() => setConfirmClear(true)}><Trash2 /></Button>
            </div>
          </div>
          <SearchBox value={search} onCommit={setSearch} placeholder={t('messages.filter')} className="flex-none bg-background" />
          <div className="flex gap-1.5">
            {([null, 'email', 'sms'] as const).map((c) => (
              <button key={c ?? 'all'} onClick={() => setChannel(c)}
                className={cn('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11.5px] font-semibold transition-colors',
                  channel === c ? 'border-border-strong bg-muted text-foreground' : 'border-border text-muted-foreground hover:bg-muted/60')}>
                {c === 'email' ? <Mail className="size-3" /> : c === 'sms' ? <MessageSquareText className="size-3" /> : null}
                {c === null ? t('stubs.all') : t(`messages.${c}`)}
                <span className="tabular-nums text-faint">{c === null ? all.length : counts[c]}</span>
              </button>
            ))}
          </div>
        </div>
        <div className="scroll-area min-h-0 flex-1 overflow-y-auto px-1.5 pb-2">
          {isLoading ? (
            <div className="space-y-2 p-2">{Array.from({ length: 5 }).map((_, i) => <div key={i} className="h-12 animate-pulse rounded bg-muted" />)}</div>
          ) : messages.length === 0 ? (
            <p className="p-3 text-sm text-faint">{all.length === 0 ? t('messages.empty') : t('messages.noMatch')}</p>
          ) : smsMode ? threads.map(({ number, list }) => (
            <div key={number} onClick={() => setSelectedNumber(number)}
              className={cn('group cursor-pointer rounded-lg px-2.5 py-2 transition-colors',
                selectedNumber === number ? 'bg-muted' : 'hover:bg-muted/60')}>
              <div className="flex items-center gap-2">
                <MessageSquareText className="size-3.5 shrink-0 text-warning" />
                <span className="min-w-0 flex-1 truncate font-mono text-[13px] font-medium">{number}</span>
                <span className="shrink-0 rounded-full bg-muted px-1.5 text-[11px] tabular-nums text-muted-foreground">{list.length}</span>
              </div>
              <div className="mt-0.5 truncate ps-[22px] text-[11.5px] text-muted-foreground">{list[0].body}</div>
            </div>
          )) : messages.map((m) => (
            <MessageRow key={m.id} message={m} active={m.id === selectedId} locale={i18n.language}
              onOpen={() => setSelectedId(m.id)} onDelete={() => void remove(m.id)} />
          ))}
        </div>
        {data?.mock && <div className="p-2 text-center"><span className="rounded-full border border-warning-border bg-warning-bg px-2 py-0.5 text-[11px] font-medium text-warning">{t('stubs.sample')}</span></div>}
      </aside>

      <div className="w-px shrink-0 bg-border" />

      <section className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background">
        {smsMode && thread ? (
          <SmsThread number={thread.number} list={thread.list} locale={i18n.language} onDelete={(id) => void remove(id)} />
        ) : !smsMode && selected ? (
          <MessageDetail message={selected} locale={i18n.language} onDelete={() => void remove(selected.id)} />
        ) : (
          <EmptyState art={<Inbox className="size-16 text-faint" />} title={t('messages.pick')} body={t('messages.pickBody')} />
        )}
      </section>

      <ConfirmDialog open={confirmClear} onOpenChange={setConfirmClear}
        title={t('messages.clearConfirmTitle')} body={t('messages.clearConfirmBody')}
        confirmLabel={t('messages.clearAll')} cancelLabel={t('editor.cancel')} destructive
        onConfirm={() => void clearAll()} />
    </div>
  )
}

function MessageRow({ message, active, locale, onOpen, onDelete }: {
  message: CapturedMessage
  active: boolean
  locale: string
  onOpen: () => void
  onDelete: () => void
}) {
  const Icon = message.channel === 'email' ? Mail : MessageSquareText
  return (
    <div onClick={onOpen}
      className={cn('group cursor-pointer rounded-lg px-2.5 py-2 transition-colors',
        active ? 'bg-muted' : 'hover:bg-muted/60')}>
      <div className="flex items-center gap-2">
        <Icon className={cn('size-3.5 shrink-0', message.channel === 'email' ? 'text-info' : 'text-warning')} />
        <span className="min-w-0 flex-1 truncate text-[13px] font-medium">
          {message.subject ?? message.body.slice(0, 60) ?? '—'}
        </span>
        {message.attachments.length > 0 && <Paperclip className="size-3 shrink-0 text-faint" />}
        <button aria-label="Delete" onClick={(e) => { e.stopPropagation(); onDelete() }}
          className="shrink-0 rounded p-0.5 text-faint opacity-0 transition-opacity hover:bg-danger-bg hover:text-danger group-hover:opacity-100"><Trash2 className="size-3.5" /></button>
      </div>
      <div className="mt-0.5 flex items-center gap-2 ps-[22px] text-[11.5px] text-muted-foreground">
        <span className="min-w-0 truncate">{message.to.join(', ')}</span>
        <span className="ms-auto shrink-0 tabular-nums text-faint">{new Date(message.receivedAt).toLocaleTimeString(locale)}</span>
      </div>
    </div>
  )
}

// One recipient number's conversation, newest at the bottom like a phone, with OTP badges.
function SmsThread({ number, list, locale, onDelete }: {
  number: string
  list: CapturedMessage[]
  locale: string
  onDelete: (id: string) => void
}) {
  const { t } = useTranslation()
  const chronological = [...list].reverse()
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className="flex items-center gap-2 border-b border-border px-5 py-4">
        <MessageSquareText className="size-4 text-warning" />
        <h2 className="font-mono text-[15px] font-semibold">{number}</h2>
        <span className="rounded-full bg-muted px-1.5 text-[11px] tabular-nums text-muted-foreground">{list.length}</span>
      </header>
      <div className="scroll-area min-h-0 flex-1 space-y-3 overflow-y-auto p-5">
        {chronological.map((m) => {
          const otp = extractOtp(m.body)
          return (
            <div key={m.id} className="group flex max-w-[560px] flex-col gap-1">
              <div className="rounded-2xl rounded-bl-sm border border-border bg-muted/40 px-3.5 py-2.5 text-[13.5px]">
                {m.body}
              </div>
              <div className="flex items-center gap-2 ps-1 text-[11px] text-faint">
                <span className="font-mono">{m.from}</span>
                <span>{new Date(m.receivedAt).toLocaleString(locale)}</span>
                {otp && (
                  <button onClick={() => { void navigator.clipboard.writeText(otp); toast.success(t('messages.otpCopied')) }}
                    title={t('messages.otpCopy')}
                    className="inline-flex items-center gap-1 rounded-full border border-success-border bg-success-bg px-2 py-px font-mono font-bold text-success transition-opacity hover:opacity-80">
                    OTP {otp}
                  </button>
                )}
                <button aria-label="Delete" onClick={() => onDelete(m.id)}
                  className="rounded p-0.5 opacity-0 transition-opacity hover:bg-danger-bg hover:text-danger group-hover:opacity-100"><Trash2 className="size-3" /></button>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function MessageDetail({ message, locale, onDelete }: { message: CapturedMessage; locale: string; onDelete: () => void }) {
  const { t } = useTranslation()
  const hasHtml = !!message.htmlBody
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className="border-b border-border px-5 py-4">
        <div className="flex items-start gap-3">
          <div className="min-w-0 flex-1">
            <h2 className="truncate text-[15px] font-semibold">{message.subject ?? t('messages.noSubject')}</h2>
            <dl className="mt-1.5 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-[12.5px]">
              <dt className="text-faint">{t('messages.from')}</dt><dd className="truncate font-mono">{message.from || '—'}</dd>
              <dt className="text-faint">{t('messages.to')}</dt><dd className="truncate font-mono">{message.to.join(', ')}</dd>
              <dt className="text-faint">{t('messages.received')}</dt>
              <dd>{new Date(message.receivedAt).toLocaleString(locale)}</dd>
            </dl>
          </div>
          <Button variant="ghost" size="iconSm" aria-label={t('stubs.delete')} onClick={onDelete}><Trash2 /></Button>
        </div>
        {message.attachments.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-2">
            {message.attachments.map((a, i) => (
              <a key={i} href={messageAttachmentUrl(message.id, i)} download={a.name}
                className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-muted/40 px-2.5 py-1 text-xs transition-colors hover:bg-muted">
                <Paperclip className="size-3" />
                <span className="font-mono">{a.name}</span>
                <span className="text-faint">{formatSize(a.size)}</span>
              </a>
            ))}
          </div>
        )}
      </header>
      <Tabs defaultValue={hasHtml ? 'preview' : 'text'} className="flex min-h-0 flex-1 flex-col">
        <TabsList className="mx-5 mt-3">
          {hasHtml && <TabsTrigger value="preview">{t('messages.preview')}</TabsTrigger>}
          <TabsTrigger value="text">{t('messages.text')}</TabsTrigger>
          <TabsTrigger value="meta">{t('messages.details')}</TabsTrigger>
        </TabsList>
        {hasHtml && (
          <TabsContent value="preview" className="min-h-0 flex-1 overflow-hidden p-5 pt-3">
            {/* Sandboxed: no scripts, no navigation — captured HTML is untrusted content. */}
            <iframe title="preview" sandbox="" srcDoc={message.htmlBody!} className="h-full w-full rounded-xl border border-border bg-white" />
          </TabsContent>
        )}
        <TabsContent value="text" className="min-h-0 flex-1 overflow-y-auto p-5 pt-3">
          <pre className="whitespace-pre-wrap rounded-xl border border-border bg-muted/30 p-4 font-mono text-[12.5px]">{message.body || '—'}</pre>
        </TabsContent>
        <TabsContent value="meta" className="min-h-0 flex-1 overflow-y-auto p-5 pt-3">
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-[12.5px]">
            <dt className="text-faint">{t('messages.channel')}</dt><dd>{message.channel}</dd>
            <dt className="text-faint">id</dt><dd className="font-mono">{message.id}</dd>
            {Object.entries(message.meta).map(([k, v]) => (
              <div key={k} className="contents"><dt className="text-faint">{k}</dt><dd className="break-all font-mono">{v}</dd></div>
            ))}
          </dl>
        </TabsContent>
      </Tabs>
    </div>
  )
}
