import { api, send } from './client'
import type {
  AssistantAnswer,
  AssistantSource,
  AssistantStatus,
  ChatRequest,
  ConversationDetail,
  PagedResultOfConversationSummary,
  PassageQuery,
  RetrievalResult,
} from './types'

// The AI assistant and semantic search (frames 5, 10). Answers stream as server-sent events:
// "sources" (the passages the model may cite) → "delta"… (provisional text) → "done" (the final,
// citation-mapped answer, which replaces the streamed text), or "error" if the model fails midway.
// Errors before the stream starts (validation, rate limit, index not ready) are ordinary ApiErrors.

export const getAssistantStatus = (signal?: AbortSignal) => api.get<AssistantStatus>('/knowledge/assistant/status', { signal })

/** Passages that match a question, without a generated answer. */
export const searchPassages = (query: PassageQuery, signal?: AbortSignal) =>
  api.get<RetrievalResult>('/knowledge/passages', { query: { ...query }, signal })

// ---- Conversations: the caller's own, kept per account (nobody else can open them)

export const listConversations = (signal?: AbortSignal) =>
  api.get<PagedResultOfConversationSummary>('/knowledge/conversations', { query: { pageSize: 50 }, signal })

export const getConversation = (id: string, signal?: AbortSignal) => api.get<ConversationDetail>(`/knowledge/conversations/${id}`, { signal })

export const deleteConversation = (id: string) => api.delete(`/knowledge/conversations/${id}`)

/** "sources" names the conversation the answer is saved to: a new one unless the request gave one. */
export type AssistantEvent =
  | { type: 'sources'; scope: string; sources: AssistantSource[]; sufficient: boolean; conversationId: string }
  | { type: 'delta'; text: string }
  | { type: 'done'; answer: AssistantAnswer }
  | { type: 'error'; message: string }

/** Asks the assistant and yields its events as they arrive. */
export async function* askAssistant(body: ChatRequest, signal?: AbortSignal): AsyncGenerator<AssistantEvent> {
  const response = await send('POST', '/knowledge/chat', { body, signal }, 'text/event-stream')
  for await (const message of readEventStream(response)) {
    if (!message.data) continue
    yield JSON.parse(message.data) as AssistantEvent
  }
}

interface EventMessage {
  event: string
  data: string
}

/** Minimal text/event-stream parser (event + data fields; comments and ids are ignored). */
async function* readEventStream(response: Response): AsyncGenerator<EventMessage> {
  if (!response.body) return
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  try {
    for (;;) {
      const { value, done } = await reader.read()
      if (done) break
      buffer += value
      for (let end = /\r?\n\r?\n/.exec(buffer); end; end = /\r?\n\r?\n/.exec(buffer)) {
        const block = buffer.slice(0, end.index)
        buffer = buffer.slice(end.index + end[0].length)
        let event = 'message'
        const data: string[] = []
        for (const line of block.split(/\r?\n/)) {
          if (line.startsWith('event:')) event = line.slice(6).trim()
          else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''))
        }
        yield { event, data: data.join('\n') }
      }
    }
  } finally {
    reader.releaseLock()
  }
}
