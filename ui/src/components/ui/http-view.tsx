import { cn } from '@/lib/utils'
import { StatusCode } from './badges'
import { JsonField } from '@/components/ui/json-field'

/** A header name/value pair as displayed in request/response views. */
export interface HeaderPairView { name: string; value: string }

/** Pretty-print a body when it parses as JSON; otherwise show it verbatim. */
function prettyBody(body: string): string {
  if (!body) return ''
  try { return JSON.stringify(JSON.parse(body), null, 2) } catch { return body }
}

/** ONE status-code chip per app (B-series dedup): the badges implementation is it. */
export function StatusChip({ status }: { status: number }) {
  return <StatusCode code={status} />
}

export function HeadersView({ headers, label }: { headers: HeaderPairView[]; label: string }) {
  return (
    <div>
      <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-faint">{label}</h4>
      {headers.length === 0 ? (
        <p className="text-xs text-faint">—</p>
      ) : (
        <dl className="overflow-hidden rounded-lg border border-border">
          {headers.map((h, i) => (
            <div key={i} className={cn('grid grid-cols-[minmax(120px,220px)_1fr] gap-3 px-3 py-1.5 text-[12.5px]', i > 0 && 'border-t border-border')}>
              <dt className="truncate font-medium text-muted-foreground">{h.name}</dt>
              <dd className="break-all font-mono text-foreground">{h.value}</dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  )
}

/**
 * A read-only body pane: the CodeMirror JSON field (syntax highlighting, folding, copy) over the
 * pretty-printed body. Height hugs the content up to a cap so short bodies don't leave a void.
 */
export function BodyView({ body, label, empty }: { body: string; label: string; empty: string }) {
  const value = prettyBody(body)
  const height = Math.min(340, Math.max(60, (value.split('\n').length + 1) * 20 + 16))
  return (
    <div>
      <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-faint">{label}</h4>
      {body ? (
        <JsonField value={value} readOnly lint={false} minimal height={height} />
      ) : (
        <p className="text-xs text-faint">{empty}</p>
      )}
    </div>
  )
}
