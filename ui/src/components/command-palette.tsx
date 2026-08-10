import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import {
  Activity, BookOpen, Database, Disc, Globe, KeyRound, LayoutDashboard, LayoutGrid,
  ListTree, Moon, Plus, Settings, Sun, Waypoints,
} from 'lucide-react'
import { CommandPalette as KitCommandPalette } from '@qorpe/ui'
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

/**
 * The global palette, on the family kit (ADR 0014 M4). ⌘K and the rail's search trigger both
 * reach it through the kit's own event, so the trigger and the palette never know each other.
 * What stays local is the only part that is mockifyr's: which destinations and actions exist.
 */
export function CommandPalette() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { theme, setTheme } = useUi()

  return (
    <KitCommandPalette
      label={t('common.search')}
      groups={[
        {
          heading: t('common.goTo'),
          items: NAV.map((n) => {
            const Icon = n.icon
            return { id: n.to, label: t(n.key), icon: <Icon className="size-4 text-muted-foreground" />, run: () => navigate(n.to) }
          }),
        },
        {
          heading: t('common.actions'),
          items: [
            { id: 'new-stub', label: t('stubs.newStub'), icon: <Plus className="size-4 text-muted-foreground" />, run: () => navigate('/stubs?new=1') },
            { id: 'helpers', label: t('editor.helpers'), icon: <BookOpen className="size-4 text-muted-foreground" />, run: openHelpers },
            {
              id: 'theme',
              label: t('common.darkMode'),
              icon: theme === 'dark'
                ? <Sun className="size-4 text-muted-foreground" />
                : <Moon className="size-4 text-muted-foreground" />,
              run: () => setTheme(theme === 'dark' ? 'light' : 'dark'),
            },
          ],
        },
      ]}
    />
  )
}
