import { lazy, Suspense } from 'react'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from 'sonner'
import { AppShell } from '@/components/layout/app-shell'
import { useUi } from '@/components/providers'

// Route pages are code-split so the initial bundle stays lean; each screen loads on first visit.
const DashboardPage = lazy(() => import('@/pages/dashboard').then((m) => ({ default: m.DashboardPage })))
const StubsPage = lazy(() => import('@/pages/stubs').then((m) => ({ default: m.StubsPage })))
const JournalPage = lazy(() => import('@/pages/journal').then((m) => ({ default: m.JournalPage })))
const MessagesPage = lazy(() => import('@/pages/messages').then((m) => ({ default: m.MessagesPage })))
const ScenariosPage = lazy(() => import('@/pages/scenarios').then((m) => ({ default: m.ScenariosPage })))
const RecordingsPage = lazy(() => import('@/pages/recordings').then((m) => ({ default: m.RecordingsPage })))
const EnvironmentsPage = lazy(() => import('@/pages/environments').then((m) => ({ default: m.EnvironmentsPage })))
const ResourcesPage = lazy(() => import('@/pages/resources').then((m) => ({ default: m.ResourcesPage })))
const AccessPage = lazy(() => import('@/pages/access').then((m) => ({ default: m.AccessPage })))
const AuditPage = lazy(() => import('@/pages/audit').then((m) => ({ default: m.AuditPage })))
const ExtensionsPage = lazy(() => import('@/pages/extensions').then((m) => ({ default: m.ExtensionsPage })))
const SettingsPage = lazy(() => import('@/pages/settings').then((m) => ({ default: m.SettingsPage })))

function Page({ children }: { children: React.ReactNode }) {
  return <Suspense fallback={<div className="h-40 animate-pulse rounded-2xl bg-muted" />}>{children}</Suspense>
}

// The base path the app is served under — '/' in dev, or the embedded prefix (e.g. '/__mockifyr/')
// when the .NET host serves the built dashboard. Vite injects it from its `base` config.
const router = createBrowserRouter([
  {
    path: '/',
    element: <AppShell />,
    children: [
      { index: true, element: <Page><DashboardPage /></Page> },
      { path: 'stubs', element: <Page><StubsPage /></Page> },
      { path: 'journal', element: <Page><JournalPage /></Page> },
      { path: 'messages', element: <Page><MessagesPage /></Page> },
      { path: 'scenarios', element: <Page><ScenariosPage /></Page> },
      { path: 'recordings', element: <Page><RecordingsPage /></Page> },
      { path: 'environments', element: <Page><EnvironmentsPage /></Page> },
      { path: 'resources', element: <Page><ResourcesPage /></Page> },
      { path: 'access', element: <Page><AccessPage /></Page> },
      { path: 'audit', element: <Page><AuditPage /></Page> },
      { path: 'extensions', element: <Page><ExtensionsPage /></Page> },
      { path: 'settings', element: <Page><SettingsPage /></Page> },
    ],
  },
], { basename: import.meta.env.BASE_URL.replace(/\/$/, '') || '/' })

export default function App() {
  const { theme } = useUi()
  return (
    <>
      <RouterProvider router={router} />
      {/* Top-right + short-lived so a toast never covers the editor's Save/Update bar (bottom of the workspace). */}
      <Toaster theme={theme} position="top-right" toastOptions={{ duration: 2500, style: { borderRadius: '10px' } }} />
    </>
  )
}
