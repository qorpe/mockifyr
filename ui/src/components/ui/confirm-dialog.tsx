import { Button, Dialog } from '@qorpe/ui'

/**
 * A small centered confirmation dialog for irreversible actions (delete). Cancel, Escape, and an
 * outside click all dismiss without confirming — only the explicit confirm button fires onConfirm.
 * Built ON the kit's Dialog since 2026-09-05 (closure audit: it duplicated the kit's modal with
 * raw Radix); the API is unchanged, the chrome is the family's.
 */
export function ConfirmDialog({ open, onOpenChange, title, body, confirmLabel, cancelLabel, onConfirm, destructive = false, children }: {
  open: boolean
  onOpenChange: (o: boolean) => void
  title: string
  body?: string
  confirmLabel: string
  cancelLabel: string
  onConfirm: () => void
  destructive?: boolean
  children?: React.ReactNode
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={title} description={body} closeLabel={cancelLabel}>
      {children}
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>{cancelLabel}</Button>
        <Button variant={destructive ? 'danger' : 'primary'} size="sm" onClick={onConfirm}>{confirmLabel}</Button>
      </div>
    </Dialog>
  )
}
