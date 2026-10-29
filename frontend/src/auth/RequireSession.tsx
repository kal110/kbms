import { Link, Navigate, Outlet, useLocation } from 'react-router'
import { ErrorState, Loading } from '../components/AsyncState'
import { AuthLayout } from './AuthLayout'
import { useSession } from './SessionContext'

/** Layout route: the app shell only renders for a tab with a signed-in account whose profile loaded. */
export function RequireSession() {
  const { status, account, error, retry } = useSession()
  const location = useLocation()
  const next = encodeURIComponent(location.pathname + location.search)

  if (status === 'signed-out') return <Navigate to={`/signin?next=${next}`} replace />
  if (status === 'expired' && account) {
    return <Navigate to={`/signin?account=${encodeURIComponent(account.externalId)}&next=${next}`} replace />
  }
  if (status === 'loading') {
    return (
      <AuthLayout>
        <Loading label={account ? `Signing in as ${account.displayName}…` : 'Signing in…'} />
      </AuthLayout>
    )
  }
  if (status === 'error') {
    return (
      <AuthLayout title="Couldn't open your account" description={account?.displayName}>
        <ErrorState error={error} onRetry={retry} />
        <Link to={`/signin?next=${next}`}>Use another account</Link>
      </AuthLayout>
    )
  }
  return <Outlet />
}
