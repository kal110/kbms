import { api } from './client'
import type { DevTokenResponse, DevUser } from './types'

// Development sign-in (backend DevEndpoints): only mapped when the API runs in Development without
// an identity provider. Production sign-in will go through the university IdP instead.

export const listDevUsers = (signal?: AbortSignal) => api.get<DevUser[]>('/dev/users', { anonymous: true, signal })

export const requestDevToken = (externalId: string) =>
  api.post<DevTokenResponse>('/dev/token', { externalId }, { anonymous: true })
