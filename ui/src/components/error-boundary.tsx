import { Component, type ReactNode } from 'react'
import { useRouteError } from 'react-router'
import { AlertTriangle } from 'lucide-react'

function Fallback({ onReload }: { onReload: () => void }) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 rounded-2xl border border-dashed border-border bg-background px-6 py-16 text-center">
      <AlertTriangle className="size-8 text-warning" />
      <p className="text-sm font-semibold">Something went wrong on this screen.</p>
      <p className="max-w-sm text-sm text-muted-foreground">The rest of the app is fine. Reload this view to try again.</p>
      <button onClick={onReload} className="mt-1 rounded-lg bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground hover:opacity-90">
        Reload
      </button>
    </div>
  )
}

/**
 * Isolates a subtree so a render error there can never freeze the whole app. Used around the command
 * palette (always mounted) and available as a route errorElement for pages.
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, { hasError: boolean }> {
  state = { hasError: false }
  static getDerivedStateFromError() { return { hasError: true } }
  componentDidCatch(error: unknown) { console.error('UI error boundary caught:', error) }
  render() {
    if (this.state.hasError) return <Fallback onReload={() => this.setState({ hasError: false })} />
    return this.props.children
  }
}

/** Route-level error element (React Router) — a page crash shows this instead of a blank/frozen app. */
export function RouteError() {
  const error = useRouteError()
  console.error('Route error:', error)
  return (
    <div className="mx-auto max-w-[1360px]">
      <Fallback onReload={() => window.location.reload()} />
    </div>
  )
}
