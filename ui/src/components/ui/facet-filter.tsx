import * as DropdownMenuPrimitive from '@radix-ui/react-dropdown-menu'
import { Check, ListFilter } from 'lucide-react'
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { cn } from '@/lib/utils'

export interface FacetOption { value: string; label?: string; count?: number }

/**
 * A multi-select facet dropdown. Selecting values keeps the menu open (preventDefault on select) so
 * several can be toggled at once; the trigger shows the active count. Filtering semantics live in
 * `@/lib/faceted` (OR within a facet, AND across facets).
 */
export function FacetFilter({
  label, options, selected, onToggle, onClear, clearLabel, compact = false, className,
}: {
  label: string
  className?: string
  options: FacetOption[]
  selected: Set<string>
  onToggle: (value: string) => void
  onClear: () => void
  clearLabel: string
  compact?: boolean
}) {
  const n = selected.size
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          className={cn(
            'inline-flex items-center gap-1.5 rounded-lg border font-medium transition-colors hover:bg-muted',
            compact ? 'h-8 px-2.5 text-[13px]' : 'h-9 px-3 text-sm',
            n > 0 ? 'border-primary/50 bg-background text-foreground' : 'border-border bg-background text-muted-foreground',
            className,
          )}
        >
          <ListFilter className={compact ? 'size-3.5' : 'size-4'} />
          {label}
          {n > 0 && (
            <span className="rounded-md bg-primary px-1.5 py-0.5 text-[11px] font-semibold tabular-nums text-primary-foreground">{n}</span>
          )}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="min-w-52">
        {options.length === 0 ? (
          <div className="px-2.5 py-2 text-sm text-muted-foreground">—</div>
        ) : (
          options.map((o) => {
            const on = selected.has(o.value)
            return (
              <DropdownMenuPrimitive.Item
                key={o.value}
                onSelect={(e) => { e.preventDefault(); onToggle(o.value) }}
                className="flex cursor-pointer select-none items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm outline-none transition-colors data-[highlighted]:bg-muted"
              >
                <span className={cn('flex size-4 shrink-0 items-center justify-center rounded border', on ? 'border-primary bg-primary text-primary-foreground' : 'border-border-strong')}>
                  {on && <Check className="size-3" />}
                </span>
                <span className="flex-1 truncate">{o.label ?? o.value}</span>
                {o.count != null && <span className="tabular-nums text-xs text-faint">{o.count}</span>}
              </DropdownMenuPrimitive.Item>
            )
          })
        )}
        {n > 0 && (
          <>
            <DropdownMenuPrimitive.Separator className="my-1.5 h-px bg-border" />
            <DropdownMenuPrimitive.Item
              onSelect={(e) => { e.preventDefault(); onClear() }}
              className="cursor-pointer rounded-lg px-2.5 py-2 text-center text-xs font-medium text-muted-foreground outline-none transition-colors data-[highlighted]:bg-muted"
            >
              {clearLabel}
            </DropdownMenuPrimitive.Item>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
