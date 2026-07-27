import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { Command } from 'cmdk'
import { Globe,
  Activity, BookOpen, Database, Disc, KeyRound, LayoutDashboard, LayoutGrid, ListTree, Moon, Plus, Search, Settings, Sun, Waypoints,
} from 'lucide-react'
import { useUi } from '@/components/providers'
import { openHelpers } from '@/components/templating/helpers-dialog'

const NAV = [
  { to: '/', key: 'nav.dashboard', icon: LayoutDashboard },
  { to: '/stubs', key: 'nav.stubs', icon: ListTree },
  { to: '/journal', key: 'nav.journal', icon: Activity },
  { to: '/scenarios', key: 'nav.scenarios', icon: Waypoints },
  { to: '/recordings', key: 'nav.recordings', icon: Disc },
  { to: '/environments', key: 'nav.environments', icon: Globe },
  { to: '/resources', key: 'nav.resources', icon: Database },
  { to: '/access', key: 'nav.access', icon: KeyRound },
  { to: '/extensions', key: 'nav.extensions', icon: LayoutGrid },
  { to: '/settings', key: 'nav.settings', icon: Settings },
]

/** Global command palette — open with ⌘K/Ctrl-K or the sidebar search (dispatches `open-command`). */
export function CommandPalette() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { theme, setTheme } = useUi()
  const [open, setOpen] = useState(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setOpen((o) => !o) }
    }
    const onOpen = () => setOpen(true)
    document.addEventListener('keydown', onKey)
    window.addEventListener('open-command', onOpen)
    return () => { document.removeEventListener('keydown', onKey); window.removeEventListener('open-command', onOpen) }
  }, [])

  const run = (fn: () => void) => { setOpen(false); fn() }

  return (
    <Command.Dialog open={open} onOpenChange={setOpen} label={t('common.search')}
      className="fixed inset-0 z-[100] grid place-items-start justify-center pt-[16vh]">
      <div className="fixed inset-0 bg-black/40" onClick={() => setOpen(false)} />
      <div className="relative w-[640px] max-w-[calc(100vw-2rem)] overflow-hidden rounded-2xl border border-border bg-background shadow-[0_16px_50px_rgb(24_24_27/0.25)]">
        <div className="flex items-center gap-2.5 border-b border-border px-4">
          <Search className="size-4 text-muted-foreground" />
          <Command.Input autoFocus placeholder={t('common.commandHint')} className="h-12 w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground" />
        </div>
        <Command.List className="max-h-[340px] overflow-y-auto p-2">
          <Command.Empty className="px-3 py-6 text-center text-sm text-muted-foreground">—</Command.Empty>
          <Command.Group heading={t('common.goTo')} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[10.5px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-faint">
            {NAV.map((n) => {
              const Icon = n.icon
              return (
                <Command.Item key={n.to} value={t(n.key)} onSelect={() => run(() => navigate(n.to))}
                  className="flex cursor-pointer items-center gap-2.5 rounded-lg px-3 py-2 text-sm data-[selected=true]:bg-muted">
                  <Icon className="size-4 text-muted-foreground" />{t(n.key)}
                </Command.Item>
              )
            })}
          </Command.Group>
          <Command.Group heading={t('common.actions')} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[10.5px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-faint">
            <Command.Item value={t('stubs.newStub')} onSelect={() => run(() => navigate('/stubs?new=1'))}
              className="flex cursor-pointer items-center gap-2.5 rounded-lg px-3 py-2 text-sm data-[selected=true]:bg-muted">
              <Plus className="size-4 text-muted-foreground" />{t('stubs.newStub')}
            </Command.Item>
            <Command.Item value={t('editor.helpers')} onSelect={() => run(openHelpers)}
              className="flex cursor-pointer items-center gap-2.5 rounded-lg px-3 py-2 text-sm data-[selected=true]:bg-muted">
              <BookOpen className="size-4 text-muted-foreground" />{t('editor.helpers')}
            </Command.Item>
            <Command.Item value={t('common.darkMode')} onSelect={() => run(() => setTheme(theme === 'dark' ? 'light' : 'dark'))}
              className="flex cursor-pointer items-center gap-2.5 rounded-lg px-3 py-2 text-sm data-[selected=true]:bg-muted">
              {theme === 'dark' ? <Sun className="size-4 text-muted-foreground" /> : <Moon className="size-4 text-muted-foreground" />}
              {t('common.darkMode')}
            </Command.Item>
          </Command.Group>
        </Command.List>
      </div>
    </Command.Dialog>
  )
}
