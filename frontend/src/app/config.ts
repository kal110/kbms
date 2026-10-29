// App settings from .env. Read import.meta.env here only, so the rest of the app gets typed values.

const env = import.meta.env

export const config = {
  /** Base URL of KnowledgeBase.Api, e.g. http://localhost:5259 (requests go to `${apiBaseUrl}/api/...`). */
  apiBaseUrl: (env.VITE_API_BASE_URL ?? '').replace(/\/+$/, ''),
} as const
