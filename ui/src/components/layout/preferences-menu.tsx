import { useTranslation } from 'react-i18next'
import { SUPPORT_URL } from '@/lib/host-config'
import { Bug, Globe, LogOut, Moon, SlidersHorizontal } from 'lucide-react'
import {
  DropdownMenu, DropdownMenuCheckItem, DropdownMenuContent, DropdownMenuItem,
  DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger,
  DropdownMenuTrigger, Switch,
} from '@qorpe/ui'
import { cn } from '@/lib/utils'
import { useUi } from '@/components/providers'
import { clearAdminAuth, hasAdminAuth } from '@/lib/api'
import { LOCALES } from '@/lib/i18n'

// There is no per-user identity in the platform (auth is a single host-level admin credential), so the
// sidebar footer is a neutral preferences menu — the tenant switcher above it carries the context.
export function PreferencesMenu({ collapsed }: { collapsed: boolean }) {
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
          <a href={SUPPORT_URL} target="_blank" rel="noreferrer">
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
