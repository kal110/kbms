import { Fragment } from 'react'
import { Quote, Sparkles } from 'lucide-react'
import type { SearchMode } from '../../api/library'
import type { SearchHit } from '../../api/types'
import { phraseOf } from './searchModes'
import styles from './searchUi.module.css'

// The search box's mode switch and what a hit shows of where it matched (modes: searchModes.ts).

/** Smart / Exact, inside the search box. */
export function SearchModeToggle({ mode, onChange, quoted }: { mode: SearchMode; onChange: (mode: SearchMode) => void; quoted: boolean }) {
  const shown = quoted ? 'exact' : mode
  return (
    <span className={styles.toggle} role="radiogroup" aria-label="Search mode">
      <button
        type="button"
        role="radio"
        aria-checked={shown === 'smart'}
        className={shown === 'smart' ? styles.on : ''}
        disabled={quoted}
        title="Smart: words in the title, number and description, and passages that mean the same thing"
        onClick={() => onChange('smart')}
      >
        <Sparkles size={12} aria-hidden />
        Smart
      </button>
      <button
        type="button"
        role="radio"
        aria-checked={shown === 'exact'}
        className={shown === 'exact' ? styles.on : ''}
        title={quoted ? 'A search in quotes is always exact' : 'Exact: these words in this order, anywhere in the text. Tip: put a search in "quotes".'}
        onClick={() => onChange('exact')}
      >
        <Quote size={12} aria-hidden />
        Exact
      </button>
    </span>
  )
}

/** Where a hit matched: the page and passage for text and meaning matches. */
export function HitSnippet({ hit, query }: { hit: SearchHit | undefined; query: string }) {
  if (!hit?.snippet || hit.match === 'Details' || !hit.match) return null
  const where = hit.page ? `p. ${hit.page}` : null
  const label =
    hit.match === 'Text'
      ? ['Exact words', where, hit.pageMatches > 1 ? `on ${hit.pageMatches} pages` : null]
      : ['Close in meaning', where]
  return (
    <span className={styles.snippet}>
      <span className={hit.match === 'Text' ? styles.labelExact : styles.labelMeaning}>
        {hit.match === 'Text' ? <Quote size={11} aria-hidden /> : <Sparkles size={11} aria-hidden />}
        {label.filter(Boolean).join(' · ')}
      </span>
      <span className={styles.passage}>{hit.match === 'Text' ? <Highlight text={hit.snippet} phrase={phraseOf(query)} /> : hit.snippet}</span>
    </span>
  )
}

/** The phrase marked wherever it occurs, ignoring case and spacing. */
function Highlight({ text, phrase }: { text: string; phrase: string }) {
  const words = phrase.split(' ').filter(Boolean).map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
  if (words.length === 0) return <>{text}</>
  const parts = text.split(new RegExp(`(${words.join('\\s+')})`, 'gi'))
  return (
    <>
      {parts.map((part, i) => (i % 2 === 1 ? <mark key={i}>{part}</mark> : <Fragment key={i}>{part}</Fragment>))}
    </>
  )
}
