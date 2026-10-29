// Types for the variables in .env (Vite exposes them on import.meta.env as strings).
interface ImportMetaEnv {
  readonly VITE_API_BASE_URL: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
