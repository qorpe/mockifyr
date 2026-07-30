import { Outlet, useLocation } from 'react-router'
import { AppSidebar } from './app-sidebar'
import { CommandPalette } from '@/components/command-palette'
import { HelpersDialog } from '@/components/templating/helpers-dialog'
import { LoginGate } from '@/components/login-gate'
import { ErrorBoundary } from '@/components/error-boundary'
import { useUi } from '@/components/providers'
import { cn } from '@/lib/utils'

// App-shell: the page never scrolls (bg-app ground). The sidebar sits flush; the content lives in a
// fixed, rounded surface and only that surface scrolls (scroll-area = auto-hiding scrollbar). Mirrors
// the Praxis layout so the frame stays put while content moves.
export function AppShell() {
  const { collapsed } = useUi()
  // The Stubs screen is a full-bleed workspace (tree + tabs): it fills the rounded surface edge-to-edge
  // and manages its own scrolling, so we drop the surface padding and let it be the single oval.
  const bleed = useLocation().pathname.endsWith('/stubs')
  return (
    <div className="flex h-dvh overflow-hidden bg-app">
      <aside
        className={cn(
          'shrink-0 bg-sidebar transition-[width] duration-300 ease-[cubic-bezier(.4,0,.2,1)]',
          collapsed ? 'w-[74px]' : 'w-[252px]',
        )}
      >
        <AppSidebar />
      </aside>
      <div className="min-w-0 flex-1 p-3 pe-3 ps-0">
        <main className={cn(
          'h-full rounded-2xl border border-border bg-surface shadow-surface',
          bleed ? 'overflow-hidden' : 'scroll-area overflow-y-auto p-6 md:p-7',
        )}>
          <ErrorBoundary>
            <Outlet />
          </ErrorBoundary>
        </main>
      </div>
      <ErrorBoundary>
        <CommandPalette />
      </ErrorBoundary>
      <HelpersDialog />
      <LoginGate />
    </div>
  )
}
