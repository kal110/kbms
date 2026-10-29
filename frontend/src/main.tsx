import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import './index.css'
import App from './App.tsx'
import { resolveDemoAccount } from './app/navigation'
import { SessionProvider } from './auth/SessionContext'
import { isUsable, sessionStore } from './auth/sessionStore'

// Demo links: ?as=supervisor (or an account id) opens the page as that person. A signed-in account
// is switched to right away; any other is signed in first (development sign-in only).
const demoAccount = resolveDemoAccount(new URLSearchParams(window.location.search).get('as'))
const demoSignedIn = isUsable(sessionStore.getState().accounts.find((a) => a.externalId === demoAccount))
if (demoAccount && demoSignedIn) sessionStore.switchTo(demoAccount)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <SessionProvider signInAs={demoSignedIn ? null : demoAccount}>
        <App />
      </SessionProvider>
    </BrowserRouter>
  </StrictMode>,
)
