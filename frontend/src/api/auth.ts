import { api } from './client'
import type { DevTokenResponse, DevUser, SignInOptions, TokenResponse } from './types'

// Signing in (backend AuthEndpoints). Without an identity provider the API checks passwords and
// signs its own tokens; in Development it also offers the pick-a-person sign-in (DevEndpoints).

export const getSignInOptions = (signal?: AbortSignal) => api.get<SignInOptions>('/auth/options', { anonymous: true, signal })

/**
 * A temporary password (set by an administrator) fails with 422 `password-change-required`:
 * send it again with `newPassword` to choose your own and sign in.
 */
export const requestPasswordToken = (login: string, password: string, newPassword?: string) =>
  api.post<TokenResponse>('/auth/sign-in', { login, password, newPassword: newPassword ?? null }, { anonymous: true })

/** Changes the signed-in account's password. Its other sessions end; this one gets the returned token. */
export const changePassword = (currentPassword: string, newPassword: string) =>
  api.post<TokenResponse>('/auth/password', { currentPassword, newPassword })

export const listDevUsers = (signal?: AbortSignal) => api.get<DevUser[]>('/dev/users', { anonymous: true, signal })

export const requestDevToken = (externalId: string) =>
  api.post<DevTokenResponse>('/dev/token', { externalId }, { anonymous: true })
