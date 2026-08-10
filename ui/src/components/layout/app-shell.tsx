import { Outlet, useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import {
  Activity, Database, Disc, Globe, Inbox, KeyRound, LayoutDashboard, LayoutGrid,
  ListTree, ScrollText, Settings, Waypoints,
} from 'lucide-react'
import { AppShell as KitAppShell, openCommand, type ShellNavItem } from '@qorpe/ui'
import { CommandPalette } from '@/components/command-palette'
import { HelpersDialog } from '@/components/templating/helpers-dialog'
import { LoginGate } from '@/components/login-gate'
import { ErrorBoundary } from '@/components/error-boundary'
import { BrandMark } from '@/components/ui/brand-mark'
import { useUi } from '@/components/providers'
import { fetchJournal, fetchMessages, fetchScenarios, fetchStubs } from '@/lib/api'
import { TenantSwitcher } from './tenant-switcher'
import { PreferencesMenu } from './preferences-menu'

interface NavDef { to: string; key: string; group: string; icon: React.ComponentType<{ className?: string }> }

const NAV: NavDef[] = [
  { to: '/', key: 'nav.dashboard', group: 'nav.overview', icon: LayoutDashboard },
  { to: '/stubs', key: 'nav.stubs', group: 'nav.mocking', icon: ListTree },
  { to: '/journal', key: 'nav.journal', group: 'nav.mocking', icon: Activity },
  { to: '/messages', key: 'nav.messages', group: 'nav.mocking', icon: Inbox },
  { to: '/scenarios', key: 'nav.scenarios', group: 'nav.mocking', icon: Waypoints },
  { to: '/recordings', key: 'nav.recordings', group: 'nav.mocking', icon: Disc },
  { to: '/environments', key: 'nav.environments', group: 'nav.mocking', icon: Globe },
  { to: '/resources', key: 'nav.resources', group: 'nav.sandbox', icon: Database },
  { to: '/access', key: 'nav.access', group: 'nav.sandbox', icon: KeyRound },
  { to: '/audit', key: 'nav.audit', group: 'nav.platform', icon: ScrollText },
  { to: '/extensions', key: 'nav.extensions', group: 'nav.platform', icon: LayoutGrid },
  { to: '/settings', key: 'nav.settings', group: 'nav.platform', icon: Settings },
]

/** Compact count for a nav badge (1000 → "1k", 1234 → "1.2k"); nothing shown for 0/undefined. */
function badgeCount(n?: number): string | undefined {
  if (!n) return undefined
  if (n < 1000) return String(n)
  return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`
}

/**
 * The shell, on the family kit (ADR 0014 M4). What used to be ~690 lines of local layout is
 * now configuration: the kit owns the rail, the collapse, the grouping, the tooltips and the
 * one scrolling surface, and mockifyr supplies what only mockifyr knows — its mark, its
 * routes, its live per-tenant counts, and the two domain cards in the rail foot.
 */
export function AppShell() {
  const { t } = useTranslation()
  const { collapsed, toggleCollapsed, tenant } = useUi()
  const navigate = useNavigate()
  const { pathname } = useLocation()

  // Live per-tenant counts. Same query keys as the pages, so TanStack Query serves them from cache.
  const stubs = useQuery({ queryKey: ['stubs', tenant], queryFn: () => fetchStubs(tenant) })
  const journal = useQuery({ queryKey: ['journal', tenant, false], queryFn: () => fetchJournal(tenant, false) })
  const messages = useQuery({ queryKey: ['messages', tenant], queryFn: () => fetchMessages(tenant) })
  const scenarios = useQuery({ queryKey: ['scenarios', tenant], queryFn: () => fetchScenarios(tenant) })
  const badges: Record<string, string | undefined> = {
    '/stubs': badgeCount(stubs.data?.stubs.length),
    '/journal': badgeCount(journal.data?.total),
    '/messages': badgeCount(messages.data?.messages.length),
    '/scenarios': badgeCount(scenarios.data?.scenarios.length),
  }

  // The Stubs screen is a full-bleed workspace (tree + tabs) that scrolls its own panes, so it
  // takes the surface whole rather than sitting inside a second scroller.
  const bleed = pathname.endsWith('/stubs')

  const activeId =
    NAV.find((n) => (n.to === '/' ? pathname === '/' : pathname === n.to || pathname.startsWith(`${n.to}/`)))?.to ?? '/'

  const nav: ShellNavItem[] = NAV.map((n) => {
    const Icon = n.icon
    return {
      id: n.to,
      label: t(n.key),
      group: t(n.group),
      icon: <Icon />,
      badge: badges[n.to],
      // Real links: ⌘-click opens the journal in a second tab while stubs stay open here —
      // an ordinary thing to want in a console. onSelect keeps it a client-side route.
      href: `/__mockifyr${n.to === '/' ? '' : n.to}`,
      onSelect: ((event?: React.MouseEvent) => {
        // Let the browser do its job for the gestures that MEAN "somewhere else": a modified
        // click is a request for a new tab, not for this router.
        if (event && (event.metaKey || event.ctrlKey || event.shiftKey || event.button === 1)) return
        event?.preventDefault()
        navigate(n.to)
      }) as () => void,
    }
  })

  return (
    <>
      <KitAppShell
        brand={<BrandMark className="w-10" />}
        title={t('brand.name')}
        subtitle={t('brand.sub')}
        nav={nav}
        activeId={activeId}
        collapsed={collapsed}
        onToggleCollapsed={toggleCollapsed}
        onSearch={openCommand}
        onHome={() => navigate('/')}
        surface={bleed ? 'bleed' : 'padded'}
        labels={{
          sections: t('nav.sections'),
          search: t('common.search'),
          collapse: t('common.collapse'),
          expand: t('common.expand'),
        }}
        footer={(isCollapsed) => (
          <div className="flex flex-col gap-2">
            <TenantSwitcher collapsed={isCollapsed} />
            <PreferencesMenu collapsed={isCollapsed} />
          </div>
        )}
      >
        <ErrorBoundary>
          <Outlet />
        </ErrorBoundary>
      </KitAppShell>
      <ErrorBoundary>
        <CommandPalette />
      </ErrorBoundary>
      <HelpersDialog />
      <LoginGate />
    </>
  )
}
