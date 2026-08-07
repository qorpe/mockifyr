import * as DialogPrimitive from '@radix-ui/react-dialog'
import { Button } from '@qorpe/ui'

/**
 * A small centered confirmation dialog for irreversible actions (delete). Cancel, Escape, and an
 * outside click all dismiss without confirming — only the explicit confirm button fires onConfirm.
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
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/40 data-[state=open]:animate-in data-[state=open]:fade-in-0" />
        <DialogPrimitive.Content
          className="fixed left-1/2 top-1/2 z-50 w-full max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl border border-border bg-background p-6 shadow-2xl outline-none data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95"
        >
          <DialogPrimitive.Title className="text-base font-semibold">{title}</DialogPrimitive.Title>
          {body && <DialogPrimitive.Description className="mt-1.5 text-sm text-muted-foreground">{body}</DialogPrimitive.Description>}
          {children}
          <div className="mt-5 flex justify-end gap-2">
            <DialogPrimitive.Close asChild><Button variant="ghost" size="sm">{cancelLabel}</Button></DialogPrimitive.Close>
            <Button variant={destructive ? 'danger' : 'primary'} size="sm" onClick={onConfirm}>{confirmLabel}</Button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}
