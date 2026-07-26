import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  type ColumnDef, flexRender, getCoreRowModel, getPaginationRowModel,
  getSortedRowModel, type SortingState, useReactTable,
} from '@tanstack/react-table'
import {
  ArrowUpDown, Check, ChevronLeft, ChevronRight, Clock, Copy, Inbox, Mail, MessageSquareText,
  Paperclip, RefreshCw, Rows2, Rows3, SlidersHorizontal, Trash2,
} from 'lucide-react'
import { toast } from 'sonner'
import { cn, formatDateTime, timeAgo } from '@/lib/utils'
import { useUi } from '@/components/providers'
import {
  type CapturedMessage, defaultBehaviors, deleteMessage, fetchMessageBehaviors, fetchMessageRaw, fetchMessages,
  messageAttachmentUrl, type MessageBehaviors, type MessageChannel, resetMessageBehaviors, resetMessages,
  saveMessageBehaviors,
} from '@/lib/api'
import { Button } from '@/components/ui/button'
import { SearchBox } from '@/components/ui/search-box'
import { EmptyState } from '@/components/ui/empty-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Sheet, SheetContent, SheetHeader } from '@/components/ui/sheet'
import { Input, Label, NativeSelect } from '@/components/ui/field'

// "26 B" for tiny payloads, "1.4 KB" above — a 26-byte attachment must not read as "0.0 KB".
const formatSize = (bytes: number) => (bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`)

// The default OTP shape (G18d): 4–8 consecutive digits. Extraction is display-side sugar; the
// server-side verify endpoint (G18f) is the API tests use.
const extractOtp = (body: string) => /\b\d{4,8}\b/.exec(body)?.[0] ?? null

const EMPTY_MESSAGES: CapturedMessage[] = []

/**
 * The captured-message inbox (G18c/e polish, ADR 0009), in the Journal's page shape — messages are
 * traffic, and the two traffic screens read as one family: channel switcher, page header, one card
 * holding toolbar + sortable table + pagination footer, row click → detail sheet. The inbox holds
 * OUTBOUND traffic: what the application under test sent; Mockifyr answered like the real SMTP
 * server / SMS provider and delivered nothing.
 */
export function MessagesPage() {
  const { t, i18n } = useTranslation()
  const { tenant } = useUi()
  const queryClient = useQueryClient()
  const [channel, setChannel] = useState<MessageChannel | null>(null)
  const [search, setSearch] = useState('')
  const [sorting, setSorting] = useState<SortingState>([{ id: 'receivedAt', desc: true }])
  const [dense, setDense] = useState(false)
  const [detailId, setDetailId] = useState<string | null>(null)
  const [confirmClear, setConfirmClear] = useState(false)
  const [behaviorsOpen, setBehaviorsOpen] = useState(false)

  const { data, isLoading, isFetching, refetch } = useQuery({
    queryKey: ['messages', tenant],
    queryFn: () => fetchMessages(tenant),
    refetchInterval: (query) => (query.state.data?.mock ? 15000 : 5000),
    refetchIntervalInBackground: true,
  })
  const all = data?.messages ?? EMPTY_MESSAGES
  const rows = useMemo(() => all.filter((m) => {
    if (channel && m.channel !== channel) return false
    if (!search.trim()) return true
    const q = search.toLowerCase()
    return [m.subject ?? '', m.body, m.from, ...m.to].some((v) => v.toLowerCase().includes(q))
  }), [all, channel, search])

  const counts = useMemo(() => ({
    email: all.filter((m) => m.channel === 'email').length,
    sms: all.filter((m) => m.channel === 'sms').length,
  }), [all])

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['messages', tenant] })

  async function remove(id: string) {
    await deleteMessage(tenant, id)
    if (detailId === id) setDetailId(null)
    refresh()
    toast.success(t('messages.deleted'))
  }

  async function clearAll() {
    await resetMessages(tenant)
    setDetailId(null)
    refresh()
    toast.success(t('messages.cleared'))
  }

  const columns = useMemo<ColumnDef<CapturedMessage>[]>(() => [
    {
      accessorKey: 'channel', header: () => t('messages.channel'),
      cell: ({ getValue }) => getValue<MessageChannel>() === 'email'
        ? <span className="inline-flex items-center gap-1.5 text-[12px] font-medium text-info"><Mail className="size-3.5" />{t('messages.email')}</span>
        : <span className="inline-flex items-center gap-1.5 text-[12px] font-medium text-warning"><MessageSquareText className="size-3.5" />{t('messages.sms')}</span>,
    },
    {
      accessorKey: 'to', header: () => t('messages.to'),
      cell: ({ getValue }) => {
        const to = getValue<string[]>()
        // A bulk send must not explode the row: first address + a count chip, full list in the sheet.
        return (
          <span className="inline-flex max-w-[260px] items-center gap-1.5">
            <span className="min-w-0 truncate font-mono text-[12.5px]">{to[0] ?? '—'}</span>
            {to.length > 1 && <span className="shrink-0 rounded-full bg-muted px-1.5 text-[10.5px] font-semibold tabular-nums text-muted-foreground">+{to.length - 1}</span>}
          </span>
        )
      },
    },
    {
      id: 'content', accessorFn: (m) => m.subject ?? m.body, header: () => t('messages.content'),
      cell: ({ row }) => {
        const m = row.original
        const otp = m.channel === 'sms' ? extractOtp(m.body) : null
        return (
          <span className="inline-flex max-w-[420px] items-center gap-2">
            <span className="min-w-0 truncate text-[13px]">{m.subject ?? m.body}</span>
            {otp && <span className="shrink-0 rounded-full border border-success-border bg-success-bg px-1.5 py-px font-mono text-[10.5px] font-bold text-success">OTP {otp}</span>}
            {m.attachments.length > 0 && (
              <span className="inline-flex shrink-0 items-center gap-1 text-[11px] text-faint"><Paperclip className="size-3" />{m.attachments.length}</span>
            )}
          </span>
        )
      },
    },
    {
      accessorKey: 'from', header: () => t('messages.from'),
      cell: ({ getValue }) => <span className="font-mono text-[12px] text-muted-foreground">{getValue<string>() || '—'}</span>,
    },
    {
      accessorKey: 'receivedAt', header: () => t('messages.received'),
      cell: ({ getValue }) => {
        const iso = getValue<string>()
        return (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground">
            <Clock className="size-3.5" />
            <span className="font-mono tabular-nums text-foreground">{formatDateTime(iso)}</span>
            <span className="text-faint">{timeAgo(iso)}</span>
          </span>
        )
      },
    },
  ], [t])

  const table = useReactTable({
    data: rows,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    autoResetPageIndex: false,
    initialState: { pagination: { pageSize: 12 } },
  })

  useEffect(() => { table.setPageIndex(0) }, [search, channel, table])

  const detail = all.find((m) => m.id === detailId) ?? null

  return (
    <div className="mx-auto max-w-[1360px]">
      {/* Channel switcher — the Journal's All/Unmatched pill, one visual language. */}
      <div className="mb-6 inline-flex gap-1 rounded-xl bg-muted p-1">
        {([null, 'email', 'sms'] as const).map((c) => (
          <button key={c ?? 'all'} onClick={() => setChannel(c)}
            className={cn('inline-flex items-center gap-1.5 rounded-lg px-3.5 py-1.5 text-sm font-semibold transition-colors',
              channel === c ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground')}>
            {c === 'email' ? <Mail className="size-4" /> : c === 'sms' ? <MessageSquareText className="size-4" /> : null}
            {c === null ? t('stubs.all') : t(`messages.${c}`)}
            <span className="tabular-nums text-faint">{c === null ? all.length : counts[c]}</span>
          </button>
        ))}
      </div>

      <header className="mb-5">
        <h1 className="text-[22px] font-bold tracking-tight">{t('nav.messages')}</h1>
        <p className="mt-1 max-w-[72ch] text-sm text-muted-foreground">{t('messages.positioning')}</p>
      </header>

      <div className="overflow-hidden rounded-2xl border border-border bg-background shadow-surface">
        <div className="flex flex-wrap items-center gap-2 border-b border-border p-3">
          <SearchBox value={search} onCommit={setSearch} placeholder={t('messages.filter')} />
          <Button variant="outline" className="ms-auto" onClick={() => setBehaviorsOpen(true)}>
            <SlidersHorizontal />{t('messages.behaviors')}
          </Button>
          <Button variant="outline" onClick={() => refetch()} disabled={isFetching}>
            <RefreshCw className={cn(isFetching && 'animate-spin')} />{t('common.refresh')}
          </Button>
          <Button variant="outline" onClick={() => setDense((d) => !d)}>
            {dense ? <Rows3 /> : <Rows2 />}{t('stubs.density')}
          </Button>
          <Button variant="outline" onClick={() => setConfirmClear(true)} disabled={!all.length}>
            <Trash2 />{t('messages.clearAll')}
          </Button>
        </div>

        <div className="scroll-area overflow-x-auto">
          <table className="w-full min-w-[860px] border-collapse">
            <thead>
              {table.getHeaderGroups().map((hg) => (
                <tr key={hg.id}>
                  {hg.headers.map((h) => (
                    <th key={h.id} className="border-b border-border bg-muted/40 px-4 py-2.5 text-start text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                      <button onClick={h.column.getToggleSortingHandler()} className="inline-flex items-center gap-1.5 hover:text-foreground">
                        {flexRender(h.column.columnDef.header, h.getContext())}
                        <ArrowUpDown className="size-3" />
                      </button>
                    </th>
                  ))}
                </tr>
              ))}
            </thead>
            <tbody>
              {isLoading ? (
                Array.from({ length: 6 }).map((_, i) => (
                  <tr key={i}><td colSpan={columns.length} className="px-4 py-3.5"><div className="h-4 w-full animate-pulse rounded bg-muted" /></td></tr>
                ))
              ) : all.length === 0 ? (
                <tr><td colSpan={columns.length}><QuickStart /></td></tr>
              ) : table.getRowModel().rows.length === 0 ? (
                <tr><td colSpan={columns.length}><EmptyState art={<Inbox className="size-14 text-faint" />} title={t('messages.noMatch')} className="py-16" /></td></tr>
              ) : (
                table.getRowModel().rows.map((row) => (
                  <tr key={row.id} onClick={() => setDetailId(row.original.id)}
                    className="cursor-pointer border-b border-border transition-colors hover:bg-muted/40">
                    {row.getVisibleCells().map((cell) => (
                      <td key={cell.id} className={cn('px-4 align-middle', dense ? 'py-2' : 'py-3')}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>
                    ))}
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <div className="flex flex-wrap items-center gap-3 border-t border-border bg-muted/30 px-4 py-3 text-[12.5px] text-muted-foreground">
          {data?.mock && (
            <span className="inline-flex items-center gap-1.5 rounded-full border border-warning-border bg-warning-bg px-2.5 py-0.5 text-[11.5px] font-medium text-warning">{t('stubs.sample')}</span>
          )}
          <span>{t('stubs.showing')} <b className="tabular-nums">{table.getRowModel().rows.length}</b> {t('stubs.of')} <b className="tabular-nums">{rows.length}</b></span>
          <div className="ms-auto flex items-center gap-1.5">
            <Button variant="outline" size="iconSm" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()} aria-label="Previous"><ChevronLeft className="rtl:rotate-180" /></Button>
            <span className="px-1 tabular-nums">{table.getState().pagination.pageIndex + 1} / {Math.max(1, table.getPageCount())}</span>
            <Button variant="outline" size="iconSm" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()} aria-label="Next"><ChevronRight className="rtl:rotate-180" /></Button>
          </div>
        </div>
      </div>

      <MessageDetailSheet message={detail} thread={detail?.channel === 'sms' ? all.filter((m) => m.channel === 'sms' && m.to[0] === detail.to[0]) : EMPTY_MESSAGES}
        locale={i18n.language} onClose={() => setDetailId(null)} onDelete={(id) => void remove(id)} />

      <BehaviorsSheet open={behaviorsOpen} onOpenChange={setBehaviorsOpen} tenant={tenant} />

      <ConfirmDialog open={confirmClear} onOpenChange={setConfirmClear}
        title={t('messages.clearConfirmTitle')} body={t('messages.clearConfirmBody')}
        confirmLabel={t('messages.clearAll')} cancelLabel={t('editor.cancel')} destructive
        onConfirm={() => void clearAll()} />
    </div>
  )
}

/** Row-click detail, in the Journal's sheet pattern: mail = Preview/Text/Details tabs; SMS = the thread. */
function MessageDetailSheet({ message, thread, locale, onClose, onDelete }: {
  message: CapturedMessage | null
  thread: CapturedMessage[]
  locale: string
  onClose: () => void
  onDelete: (id: string) => void
}) {
  const { t } = useTranslation()
  const hasHtml = !!message?.htmlBody
  return (
    <Sheet open={message !== null} onOpenChange={(o) => { if (!o) onClose() }}>
      <SheetContent>
        {message && (
          <>
            {/* The subject and the from→to line copy on hover — both travel into tests and bug
                reports constantly (#194 polish). */}
            <div className="border-b border-border px-6 py-4">
              <HoverCopy className="text-base font-semibold"
                text={message.subject ?? (message.channel === 'sms' ? message.to[0] : t('messages.noSubject'))} />
              <HoverCopy className="mt-0.5 text-sm text-muted-foreground"
                text={`${message.from || '—'} → ${message.to.join(', ')}`} />
            </div>
            {message.channel === 'sms' ? (
              <SmsThread list={thread} locale={locale} onDelete={onDelete} />
            ) : (
              <div className="flex min-h-0 flex-1 flex-col">
                {message.attachments.length > 0 && (
                  <div className="flex flex-wrap gap-2 px-6 pt-4">
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
                <Tabs defaultValue={hasHtml ? 'preview' : 'text'} className="flex min-h-0 flex-1 flex-col">
                  <TabsList className="mx-6 mt-4">
                    {hasHtml && <TabsTrigger value="preview">{t('messages.preview')}</TabsTrigger>}
                    <TabsTrigger value="text">{t('messages.text')}</TabsTrigger>
                    <TabsTrigger value="source">{t('messages.source')}</TabsTrigger>
                    <TabsTrigger value="meta">{t('messages.details')}</TabsTrigger>
                  </TabsList>
                  {hasHtml && (
                    <TabsContent value="preview" className="flex min-h-0 flex-1 flex-col overflow-hidden p-6 pt-3">
                      <div className="mb-2 flex justify-end"><CopyButton text={message.htmlBody!} label={t('messages.copyHtml')} /></div>
                      {/* Sandboxed: no scripts, no navigation — captured HTML is untrusted content. */}
                      <iframe title="preview" sandbox="" srcDoc={message.htmlBody!} className="min-h-0 w-full flex-1 rounded-xl border border-border bg-white" />
                    </TabsContent>
                  )}
                  <TabsContent value="text" className="min-h-0 flex-1 overflow-y-auto p-6 pt-3">
                    <div className="mb-2 flex justify-end"><CopyButton text={message.body} label={t('editor.copy')} /></div>
                    <pre className="whitespace-pre-wrap rounded-xl border border-border bg-muted/30 p-4 font-mono text-[12.5px]">{message.body || '—'}</pre>
                  </TabsContent>
                  <TabsContent value="source" className="min-h-0 flex-1 overflow-y-auto p-6 pt-3">
                    <RawSource id={message.id} />
                  </TabsContent>
                  <TabsContent value="meta" className="min-h-0 flex-1 overflow-y-auto p-6 pt-3">
                    <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-6 gap-y-3 text-[12.5px]">
                      <dt className="text-faint">{t('messages.received')}</dt><dd>{new Date(message.receivedAt).toLocaleString(locale)}</dd>
                      <dt className="text-faint">id</dt><dd><CopyableValue value={message.id} /></dd>
                      {Object.entries(message.meta).map(([k, v]) => (
                        <div key={k} className="contents"><dt className="text-faint">{k}</dt><dd><CopyableValue value={v} /></dd></div>
                      ))}
                    </dl>
                  </TabsContent>
                </Tabs>
                <div className="flex justify-end border-t border-border px-6 py-3">
                  <Button variant="ghost" onClick={() => onDelete(message.id)}><Trash2 />{t('stubs.delete')}</Button>
                </div>
              </div>
            )}
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}

// One recipient number's conversation, newest at the bottom like a phone, with OTP badges.
function SmsThread({ list, locale, onDelete }: { list: CapturedMessage[]; locale: string; onDelete: (id: string) => void }) {
  const { t } = useTranslation()
  const [sourceFor, setSourceFor] = useState<string | null>(null)
  const chronological = [...list].reverse()
  return (
    <div className="scroll-area min-h-0 flex-1 space-y-3 overflow-y-auto p-6">
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
              <button onClick={() => setSourceFor(sourceFor === m.id ? null : m.id)}
                className={cn('rounded px-1 font-mono text-[10px] transition-colors hover:bg-muted hover:text-foreground', sourceFor === m.id && 'bg-muted text-foreground')}>
                {t('messages.source')}
              </button>
              <button aria-label="Delete" onClick={() => onDelete(m.id)}
                className="rounded p-0.5 opacity-0 transition-opacity hover:bg-danger-bg hover:text-danger group-hover:opacity-100"><Trash2 className="size-3" /></button>
            </div>
            {sourceFor === m.id && <RawSource id={m.id} />}
          </div>
        )
      })}
    </div>
  )
}

/** The raw wire payload (#194, Mailpit-style): fetched on demand, shown byte-for-byte. */
function RawSource({ id }: { id: string }) {
  const { t } = useTranslation()
  const { tenant } = useUi()
  const { data, isLoading } = useQuery({ queryKey: ['message-raw', tenant, id], queryFn: () => fetchMessageRaw(tenant, id) })
  if (isLoading) return <div className="h-16 animate-pulse rounded-lg bg-muted" />
  return (
    <div>
      <div className="mb-2 flex justify-end"><CopyButton text={data ?? ''} label={t('editor.copy')} /></div>
      {/* Deliberately NOT beautified: the source pane's promise is byte-for-byte wire truth. */}
      <pre className="scroll-area overflow-x-auto whitespace-pre-wrap break-all rounded-xl border border-border bg-muted/30 p-4 font-mono text-[11.5px] leading-relaxed">{data ?? '—'}</pre>
    </div>
  )
}

/** A text line that copies on click, with the icon appearing on hover — for titles and address lines. */
function HoverCopy({ text, className }: { text: string; className?: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      onClick={() => { void navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1500) }}
      className={cn('group flex max-w-full items-center gap-2 text-start', className)} title={text}>
      <span className="min-w-0 truncate">{text}</span>
      {copied ? <Check className="size-3.5 shrink-0 text-success" /> : <Copy className="size-3.5 shrink-0 text-faint opacity-0 transition-opacity group-hover:opacity-100" />}
    </button>
  )
}

/** A value that copies on click — meta ids and provider fields travel into tests constantly. */
function CopyableValue({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      onClick={() => { void navigator.clipboard.writeText(value); setCopied(true); setTimeout(() => setCopied(false), 1500) }}
      className="group inline-flex max-w-full items-center gap-1.5 break-all text-start font-mono hover:text-foreground"
      title={value}>
      <span className="break-all">{value}</span>
      {copied ? <Check className="size-3 shrink-0 text-success" /> : <Copy className="size-3 shrink-0 text-faint opacity-0 transition-opacity group-hover:opacity-100" />}
    </button>
  )
}

// A small copy control used across the detail tabs (#194 polish): every pane's content is one click away.
function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      onClick={() => { void navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1500) }}
      className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-background px-2.5 py-1 text-xs font-medium text-muted-foreground transition-colors hover:bg-muted hover:text-foreground">
      {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}{label}
    </button>
  )
}

// A copyable one-liner for the quick-start cards.
function CommandLine({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2">
      <code className="min-w-0 flex-1 truncate font-mono text-[12px]">{text}</code>
      <button
        onClick={() => { void navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1500) }}
        className="shrink-0 rounded p-1 text-faint transition-colors hover:bg-muted hover:text-foreground"
        aria-label="Copy">
        {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
      </button>
    </div>
  )
}

/**
 * First-run guidance (G18c polish): this inbox captures OUTBOUND traffic — Mockifyr stands in for
 * the real SMTP server / SMS provider, answers like the real thing, and delivers nothing. The two
 * cards say exactly how to point an application here.
 */
function QuickStart() {
  const { t } = useTranslation()
  return (
    <div className="mx-auto max-w-[560px] px-6 py-12">
      <Inbox className="size-10 text-faint" />
      <h2 className="mt-3 text-[17px] font-bold">{t('messages.qsTitle')}</h2>
      <p className="mt-1.5 max-w-[52ch] text-sm text-muted-foreground">{t('messages.qsBody')}</p>

      <div className="mt-6 space-y-5">
        <div>
          <div className="mb-2 flex items-center gap-2 text-[13px] font-semibold"><Mail className="size-4 text-info" />{t('messages.qsMail')}</div>
          <div className="space-y-1.5">
            <CommandLine text="mockifyr --port 8080 --smtp-port 1025" />
            <CommandLine text="Smtp Host=localhost Port=1025   # point your app's mail settings here" />
          </div>
          <p className="mt-1.5 text-xs text-muted-foreground">{t('messages.qsMailHint')}</p>
        </div>
        <div>
          <div className="mb-2 flex items-center gap-2 text-[13px] font-semibold"><MessageSquareText className="size-4 text-warning" />{t('messages.qsSms')}</div>
          <div className="space-y-1.5">
            <CommandLine text="mockifyr --port 8080 --sms-profile twilio" />
            <CommandLine text="Twilio base URL -> http://localhost:8080   # the official SDK works" />
          </div>
          <p className="mt-1.5 text-xs text-muted-foreground">{t('messages.qsSmsHint')}</p>
        </div>
        <p className="text-xs text-muted-foreground">{t('messages.qsVerify')} <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px]">GET /__admin/messages/otp?recipient=…</code></p>
      </div>
    </div>
  )
}

/**
 * Channel behaviors (G18e): the screen for /__admin/messages/behaviors — SMTP fault directives,
 * simulated SMS provider errors, and the capture webhook, per tenant.
 */
function BehaviorsSheet({ open, onOpenChange, tenant }: { open: boolean; onOpenChange: (o: boolean) => void; tenant: string }) {
  const { t } = useTranslation()
  const [form, setForm] = useState<MessageBehaviors>(defaultBehaviors)
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    if (!open) return
    setLoaded(false)
    void fetchMessageBehaviors(tenant).then(({ behaviors }) => { setForm(behaviors); setLoaded(true) })
  }, [open, tenant])

  async function save() {
    const { ok, error } = await saveMessageBehaviors(tenant, form)
    if (ok) { toast.success(t('messages.behaviorsSaved')); onOpenChange(false) }
    else toast.error(error ?? t('editor.invalidJson'))
  }

  async function reset() {
    await resetMessageBehaviors(tenant)
    setForm(defaultBehaviors)
    toast.success(t('messages.behaviorsReset'))
  }

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="max-w-[480px]">
        <SheetHeader title={t('messages.behaviors')} description={t('messages.behaviorsHint')} />
        <div className={cn('min-h-0 flex-1 space-y-5 overflow-y-auto px-6 py-5', !loaded && 'pointer-events-none opacity-50')}>
          <div>
            <div className="mb-2 flex items-center gap-2 text-[13px] font-semibold"><Mail className="size-4 text-info" />{t('messages.qsMail')}</div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <Label>{t('messages.smtpFault')}</Label>
                <NativeSelect value={form.smtpFault} onChange={(e) => setForm({ ...form, smtpFault: e.target.value as MessageBehaviors['smtpFault'] })}>
                  <option value="none">{t('messages.faultNone')}</option>
                  <option value="reject">{t('messages.faultReject')}</option>
                  <option value="drop">{t('messages.faultDrop')}</option>
                </NativeSelect>
              </div>
              <div>
                <Label>{t('messages.smtpDelay')}</Label>
                <Input type="number" min={0} value={form.smtpDelayMs || ''}
                  placeholder="0" onChange={(e) => setForm({ ...form, smtpDelayMs: Number(e.target.value) || 0 })} />
              </div>
            </div>
            <p className="mt-1.5 text-xs text-muted-foreground">{t('messages.smtpFaultHint')}</p>
          </div>

          <div>
            <div className="mb-2 flex items-center gap-2 text-[13px] font-semibold"><MessageSquareText className="size-4 text-warning" />{t('messages.qsSms')}</div>
            <Label>{t('messages.smsError')}</Label>
            <Input type="number" placeholder="21211" value={form.smsErrorCode ?? ''}
              onChange={(e) => setForm({ ...form, smsErrorCode: e.target.value ? Number(e.target.value) : null })} />
            <p className="mt-1.5 text-xs text-muted-foreground">{t('messages.smsErrorHint')}</p>
          </div>

          <div>
            <Label>{t('messages.captureWebhook')}</Label>
            <Input placeholder="https://…/hook" value={form.webhookUrl ?? ''}
              onChange={(e) => setForm({ ...form, webhookUrl: e.target.value || null })} />
            <p className="mt-1.5 text-xs text-muted-foreground">{t('messages.captureWebhookHint')}</p>
          </div>
        </div>
        <div className="flex justify-between gap-2 border-t border-border px-6 py-3">
          <Button variant="ghost" onClick={() => void reset()}>{t('messages.behaviorsResetBtn')}</Button>
          <div className="flex gap-2">
            <Button variant="ghost" onClick={() => onOpenChange(false)}>{t('editor.cancel')}</Button>
            <Button variant="primary" onClick={() => void save()}>{t('messages.save')}</Button>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  )
}
