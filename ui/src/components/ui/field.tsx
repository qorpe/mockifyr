import { cn } from '@/lib/utils'

// The family's text controls come from the kit (ui-standard v1.3 §7: one look, one
// implementation). A local Input/Textarea pair duplicated the kit's until 2026-09-05 —
// the closure audit's "local component overlaps" item; this file is now the re-export
// plus the one primitive the kit does not ship (a bare Label — the kit wraps label +
// control in `Field`, which is the better home for new forms).
export { Input, Textarea } from '@qorpe/ui'

export function Label({ className, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return <label className={cn('mb-1.5 block text-xs font-semibold text-muted-foreground', className)} {...props} />
}
