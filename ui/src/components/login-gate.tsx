import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQueryClient } from '@tanstack/react-query'
import { Lock } from 'lucide-react'
import { fetchHealth, verifyAdminAuth } from '@/lib/api'
import { beginLogin, completeLoginIfRedirected } from '@/lib/oidc'
import { Button } from '@qorpe/ui'
import { Input, Label } from '@/components/ui/field'

/**
 * Full-screen login overlay for hosts started with --admin-user/--admin-pass. It stays dormant until an
 * admin call comes back 401 (adminFetch dispatches a `mockifyr-auth-required` window event), then blocks
 * the app until valid credentials are entered. On success it stores the Basic token and invalidates every
 * query so the dashboard refetches with auth. Hosts without admin auth never emit the event, so the gate
 * never shows.
 */
export function LoginGate() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [user, setUser] = useState('')
  const [pass, setPass] = useState('')
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState(false)

  const signIn = async () => {
    if (!oidc) return
    setBusy(true)
    setOidcError(null)
    try {
      await beginLogin(oidc)
    } catch (e) {
      // A provider that cannot be reached must say so here, not leave a dead button.
      setOidcError((e as Error).message)
      setBusy(false)
    }
  }

  // How this host wants people to sign in. Read once, unauthenticated — a login screen cannot
  // authenticate before it knows where to send the user (#251).
  const [oidc, setOidc] = useState<{ authority: string; clientId: string } | null>(null)
  const [oidcError, setOidcError] = useState<string | null>(null)

  useEffect(() => {
    const show = () => setOpen(true)
    window.addEventListener('mockifyr-auth-required', show)
    return () => window.removeEventListener('mockifyr-auth-required', show)
  }, [])

  useEffect(() => {
    let cancelled = false
    void fetchHealth('default').then(async ({ health }) => {
      const auth = health.auth
      if (cancelled || auth?.mode !== 'oidc' || !auth.authority || !auth.clientId) return
      const config = { authority: auth.authority, clientId: auth.clientId }
      setOidc(config)
      // Coming back from the provider with ?code=: finish the exchange and refetch everything, so the
      // user lands where they were rather than on a login screen they already passed.
      if (await completeLoginIfRedirected(config)) {
        setOpen(false)
        void queryClient.invalidateQueries()
      }
    }).catch(() => {
      // An unreachable host is not a sign-in problem; the regular fetch path will surface it.
    })
    return () => { cancelled = true }
  }, [queryClient])

  if (!open) return null

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(false)
    const ok = await verifyAdminAuth(user.trim(), pass)
    setBusy(false)
    if (!ok) {
      setError(true)
      return
    }
    setPass('')
    await queryClient.invalidateQueries()
    setOpen(false)
  }

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center bg-app/80 p-4 backdrop-blur-sm">
      <form
        onSubmit={submit}
        className="w-[min(92vw,380px)] rounded-2xl border border-border bg-surface p-7 shadow-surface"
      >
        <div className="mb-6 flex flex-col items-center gap-2 text-center">
          <div className="flex size-11 items-center justify-center rounded-xl bg-primary/10 text-primary">
            <Lock className="size-5" />
          </div>
          <h1 className="text-lg font-semibold">{t('login.title')}</h1>
          <p className="text-sm text-muted-foreground">{t('login.subtitle')}</p>
        </div>
        {oidc ? (
          // The host authenticates people through an identity provider, so there is nothing to type
          // here — asking for a username would be asking for the wrong credential.
          <div className="space-y-3">
            <Button
              type="button"
              variant="primary"
              className="w-full"
              disabled={busy}
              onClick={() => void signIn()}
            >
              {busy ? t('login.redirecting') : t('login.signInWithProvider')}
            </Button>
            {oidcError && <p className="text-sm text-danger">{oidcError}</p>}
            <p className="text-center text-xs text-muted-foreground">{t('login.providerHint')}</p>
          </div>
        ) : (
        <div className="space-y-3">
          <div>
            <Label htmlFor="login-user">{t('login.username')}</Label>
            <Input
              id="login-user"
              autoFocus
              autoComplete="username"
              value={user}
              onChange={(event) => setUser(event.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="login-pass">{t('login.password')}</Label>
            <Input
              id="login-pass"
              type="password"
              autoComplete="current-password"
              value={pass}
              onChange={(event) => setPass(event.target.value)}
            />
          </div>
          {error && <p className="text-sm text-danger">{t('login.invalid')}</p>}
          <Button type="submit" variant="primary" className="w-full" disabled={busy || !user || !pass}>
            {busy ? t('login.signingIn') : t('login.signIn')}
          </Button>
        </div>
        )}
      </form>
    </div>
  )
}
