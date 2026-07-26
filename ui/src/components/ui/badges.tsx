import { cn } from '@/lib/utils'
import type { Protocol, StubStatus } from '@/lib/api'

// Method chips and status pills draw from the semantic token ramp — configurable in one place, kept
// separate from the accent so status always reads at a glance.
const METHOD_TONE: Record<string, string> = {
  GET: 'text-info bg-info-bg border-info-border',
  POST: 'text-success bg-success-bg border-success-border',
  PUT: 'text-warning bg-warning-bg border-warning-border',
  DELETE: 'text-danger bg-danger-bg border-danger-border',
  PATCH: 'text-violet bg-violet-bg border-violet-border',
}

export function MethodChip({ method }: { method: string }) {
  return (
    <span className={cn('inline-flex rounded-md border px-2 py-0.5 font-mono text-[11px] font-bold', METHOD_TONE[method] ?? 'text-muted-foreground bg-muted border-border')}>
      {method}
    </span>
  )
}

// Non-HTTP protocols get a chip (ADR 0010); HTTP is the default and stays unmarked so the tree
// doesn't drown in badges. Tones are distinct from the method ramp so the two never read as one.
const PROTOCOL_TONE: Record<Exclude<Protocol, 'http'>, { label: string; tone: string }> = {
  grpc: { label: 'gRPC', tone: 'text-violet bg-violet-bg border-violet-border' },
  graphql: { label: 'GraphQL', tone: 'text-info bg-info-bg border-info-border' },
  websocket: { label: 'WS', tone: 'text-warning bg-warning-bg border-warning-border' },
}

export function ProtocolChip({ protocol }: { protocol: Protocol }) {
  if (protocol === 'http') return null
  const p = PROTOCOL_TONE[protocol]
  return (
    <span className={cn('inline-flex rounded-md border px-1.5 py-0.5 font-mono text-[10px] font-bold', p.tone)}>
      {p.label}
    </span>
  )
}

const STATUS: Record<StubStatus, { tone: string; dot: string; key: string }> = {
  live: { tone: 'text-success bg-success-bg border-success-border', dot: 'bg-success', key: 'status.live' },
  proxy: { tone: 'text-info bg-info-bg border-info-border', dot: 'bg-info', key: 'status.proxy' },
  draft: { tone: 'text-muted-foreground bg-muted border-border', dot: 'bg-faint', key: 'status.draft' },
}

// HTTP response-code chip for the stub tree: 2xx green, 3xx blue, 4xx amber, 5xx red, unknown grey.
export function StatusCode({ code }: { code: number | null }) {
  const tone = code == null ? 'text-muted-foreground bg-muted border-border'
    : code < 300 ? 'text-success bg-success-bg border-success-border'
    : code < 400 ? 'text-info bg-info-bg border-info-border'
    : code < 500 ? 'text-warning bg-warning-bg border-warning-border'
    : 'text-danger bg-danger-bg border-danger-border'
  return (
    <span className={cn('inline-flex min-w-[2.5rem] justify-center rounded-md border px-1.5 py-0.5 font-mono text-[11px] font-bold tabular-nums', tone)}>
      {code ?? '—'}
    </span>
  )
}

export function StatusPill({ status, label }: { status: StubStatus; label: string }) {
  const s = STATUS[status]
  return (
    <span className={cn('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11.5px] font-semibold', s.tone)}>
      <span className={cn('size-1.5 rounded-full', s.dot)} />
      {label}
    </span>
  )
}
