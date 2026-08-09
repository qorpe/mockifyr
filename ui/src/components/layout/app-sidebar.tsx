import { NavLink, useLocation } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import {
  Activity, Bug, ChevronsRight, Database, Disc, Globe, Inbox, KeyRound, LayoutDashboard, LayoutGrid,
  ListTree, LogOut, Moon, Search, Settings, SlidersHorizontal, Waypoints,
  ScrollText,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { clearAdminAuth, fetchJournal, fetchMessages, fetchScenarios, fetchStubs, hasAdminAuth } from '@/lib/api'
import { LOCALES } from '@/lib/i18n'
import { BrandMark } from '@/components/ui/brand-mark'
import { TenantSwitcher } from './tenant-switcher'
import {
  DropdownMenu, DropdownMenuCheckItem, DropdownMenuContent, DropdownMenuItem,
  DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger,
} from '@qorpe/ui'
import { Switch, Tooltip } from '@qorpe/ui'

interface NavItem { to: string; key: string; icon: React.ComponentType<{ className?: string }>; badge?: string }

const GROUPS: { label: string; items: NavItem[] }[] = [
  { label: 'nav.overview', items: [{ to: '/', key: 'nav.dashboard', icon: LayoutDashboard }] },
  { label: 'nav.mocking', items: [
    { to: '/stubs', key: 'nav.stubs', icon: ListTree },
    { to: '/journal', key: 'nav.journal', icon: Activity },
    { to: '/messages', key: 'nav.messages', icon: Inbox },
    { to: '/scenarios', key: 'nav.scenarios', icon: Waypoints },
    { to: '/recordings', key: 'nav.recordings', icon: Disc },
    { to: '/environments', key: 'nav.environments', icon: Globe },
  ] },
  { label: 'nav.sandbox', items: [
    { to: '/resources', key: 'nav.resources', icon: Database },
    { to: '/access', key: 'nav.access', icon: KeyRound },
  ] },
  { label: 'nav.platform', items: [
    { to: '/audit', key: 'nav.audit', icon: ScrollText },
    { to: '/extensions', key: 'nav.extensions', icon: LayoutGrid },
    { to: '/settings', key: 'nav.settings', icon: Settings },
  ] },
]

const openCommand = () => window.dispatchEvent(new Event('open-command'))

// Compact count for a nav badge (1000 → "1k", 1234 → "1.2k"); nothing shown for 0/undefined.
function badgeCount(n?: number): string | undefined {
  if (!n) return undefined
  if (n < 1000) return String(n)
  return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`
}

export function AppSidebar() {
  const { t } = useTranslation()
  const { collapsed, toggleCollapsed, tenant } = useUi()

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

  return (
    <nav className="flex h-full w-full select-none flex-col overflow-hidden">
        {/* Header: collapsed → expand button on top, logo below; expanded → logo row with toggle at the end */}
        {collapsed ? (
          <div className="shrink-0 px-2 pb-1 pt-[18px]">
            <IconButton label={t('common.expand')} onClick={toggleCollapsed}>
              <ChevronsRight className="size-[18px] text-muted-foreground" />
            </IconButton>
            <div className="pt-2">
              <IconButton label={t('brand.name')} to="/">
                <BrandMark className="w-7" />
              </IconButton>
            </div>
          </div>
        ) : (
          <div className="flex shrink-0 items-center gap-1 px-2 pb-1 pt-[18px]">
            <NavLink to="/" className="flex h-11 flex-1 items-center gap-2.5 rounded-lg px-2 transition-colors hover:bg-muted">
              <BrandMark className="w-10 shrink-0" />
              <span className="min-w-0">
                <span className="block truncate text-sm font-semibold leading-tight">{t('brand.name')}</span>
                <span className="block truncate text-xs leading-tight text-muted-foreground">{t('brand.sub')}</span>
              </span>
            </NavLink>
            <button onClick={toggleCollapsed} aria-label={t('common.collapse')} className="shrink-0 rounded-lg p-1.5 text-faint transition-colors hover:bg-muted hover:text-foreground">
              <ChevronsRight className="size-4 rotate-180 rtl:rotate-0" />
            </button>
          </div>
        )}

        {/* Search — opens the command palette (⌘K) */}
        <div className="px-3 pb-2 pt-1">
          {collapsed ? (
            <IconButton label={t('common.search')} onClick={openCommand}>
              <Search className="size-[18px] text-muted-foreground" />
            </IconButton>
          ) : (
            <button onClick={openCommand} className="flex h-9 w-full items-center gap-2.5 rounded-lg border border-border bg-muted/60 px-3 text-sm text-muted-foreground transition-colors hover:border-border-strong">
              <Search className="size-4" />
              <span>{t('common.search')}</span>
              <kbd className="ms-auto rounded-md border border-border bg-background px-1.5 font-mono text-[11px]">⌘K</kbd>
            </button>
          )}
        </div>

        {/* Navigation */}
        <div className="scroll-area min-h-0 flex-1 overflow-y-auto px-3">
          {GROUPS.map((group) => (
            <div key={group.label} className="mb-2">
              {collapsed ? (
                <div className="mx-3 my-2 h-px bg-border" />
              ) : (
                <div className="px-2.5 pb-1 pt-2.5 text-[10.5px] font-semibold uppercase tracking-wider text-faint">{t(group.label)}</div>
              )}
              {group.items.map((item) => (
                <NavRow key={item.to} item={item} collapsed={collapsed} label={t(item.key)} badge={badges[item.to]} />
              ))}
            </div>
          ))}
        </div>

        {/* Tenant switcher (multi-tenancy) + preferences menu (language submenu + dark toggle) */}
        <div className="flex shrink-0 flex-col gap-2 p-3">
          <TenantSwitcher collapsed={collapsed} />
          <PreferencesMenu collapsed={collapsed} />
        </div>
    </nav>
  )
}

function NavRow({ item, collapsed, label, badge }: { item: NavItem; collapsed: boolean; label: string; badge?: string }) {
  const Icon = item.icon
  // Compute active from the location and pass a STRING className. A function className is stringified
  // by Radix's Slot when the link is wrapped by TooltipTrigger asChild (collapsed mode), which silently
  // dropped the collapsed centering — so we resolve it ourselves.
  const { pathname } = useLocation()
  const isActive = item.to === '/' ? pathname === '/' : pathname === item.to || pathname.startsWith(`${item.to}/`)
  const link = (
    <NavLink
      to={item.to}
      end={item.to === '/'}
      className={cn(
        'group relative mb-0.5 flex h-9 items-center rounded-lg text-sm font-medium transition-colors',
        collapsed ? 'mx-auto w-10 justify-center' : 'gap-2.5 px-2.5',
        isActive
          ? 'bg-sidebar-accent text-sidebar-accent-foreground font-semibold'
          : 'text-muted-foreground hover:bg-muted hover:text-foreground',
      )}
    >
      {isActive && !collapsed && <span className="absolute inset-y-1.5 start-0 w-[3px] rounded-full bg-primary" />}
      <Icon className="size-[18px] shrink-0" />
      {!collapsed && <span className="truncate">{label}</span>}
      {!collapsed && badge && (
        <span className={cn('ms-auto rounded-md px-1.5 py-0.5 text-[11px] font-medium tabular-nums text-muted-foreground', isActive ? 'bg-background' : 'bg-muted')}>
          {badge}
        </span>
      )}
    </NavLink>
  )

  if (!collapsed) return link
  return (
    <Tooltip label={label} side="right">
      {link}
    </Tooltip>
  )
}

// There is no per-user identity in the platform (auth is a single host-level admin credential), so the
// sidebar footer is a neutral preferences menu — the tenant switcher above it carries the context.
function PreferencesMenu({ collapsed }: { collapsed: boolean }) {
  const { t } = useTranslation()
  const { theme, setTheme, locale, setLocale } = useUi()
  const active = LOCALES.find((l) => l.code === locale) ?? LOCALES[0]
  // Re-read on every sidebar render; a successful login invalidates all queries (re-render), so the
  // item appears without a reload. Losing it after sign-out matters less — the login gate covers the app.
  const authed = hasAdminAuth()

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button className={cn('flex w-full items-center gap-2.5 rounded-lg border border-border bg-muted/60 text-sm text-muted-foreground transition-colors hover:border-border-strong hover:text-foreground', collapsed ? 'justify-center p-2.5' : 'px-3 py-2')}>
          <SlidersHorizontal className="size-4 shrink-0" />
          {!collapsed && <span className="min-w-0 flex-1 truncate text-start">{t('common.preferences')}</span>}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent side="top" align="start" className="w-[--radix-dropdown-menu-trigger-width] min-w-56">
        <DropdownMenuSub>
          <DropdownMenuSubTrigger>
            <Globe className="size-4 text-muted-foreground" />
            {t('common.language')}
            <span className="ms-auto text-xs text-muted-foreground">{active.native}</span>
          </DropdownMenuSubTrigger>
          <DropdownMenuSubContent>
            {LOCALES.map((l) => (
              <DropdownMenuCheckItem key={l.code} checked={l.code === locale} onSelect={() => setLocale(l.code)}>
                {l.name}
              </DropdownMenuCheckItem>
            ))}
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        <DropdownMenuItem onSelect={(e) => { e.preventDefault(); setTheme(theme === 'dark' ? 'light' : 'dark') }}>
          <Moon className="size-4 text-muted-foreground" />
          {t('common.darkMode')}
          <Switch checked={theme === 'dark'} className="ms-auto" tabIndex={-1} />
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <a href="https://github.com/qorpe/mockifyr/issues" target="_blank" rel="noreferrer">
            <Bug className="size-4 text-muted-foreground" />{t('common.reportIssue')}
          </a>
        </DropdownMenuItem>
        {/* Sign out only exists when the host runs with admin credentials; on an open host there is no session to end. */}
        {authed && (
          <DropdownMenuItem
            onSelect={(e) => {
              e.preventDefault()
              clearAdminAuth()
              window.dispatchEvent(new Event('mockifyr-auth-required'))
            }}
          >
            <LogOut className="size-4 text-muted-foreground" />{t('common.signOut')}
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/** A centered 40×36 icon button with a right-side tooltip — the collapsed-rail affordance. */
function IconButton({ label, to, onClick, children }: { label: string; to?: string; onClick?: () => void; children: React.ReactNode }) {
  const inner = to ? (
    <NavLink to={to} className="mx-auto flex h-9 w-10 items-center justify-center rounded-lg transition-colors hover:bg-muted">{children}</NavLink>
  ) : (
    <button onClick={onClick} aria-label={label} className="mx-auto flex h-9 w-10 items-center justify-center rounded-lg transition-colors hover:bg-muted">{children}</button>
  )
  return (
    <Tooltip label={label} side="right">
      {inner}
    </Tooltip>
  )
}
