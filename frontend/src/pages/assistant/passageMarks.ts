// Finds a cited passage on a page, so the citation preview can mark it. The passage comes from the
// server's text extraction and the page text from pdf.js (or the stored page text), and the two
// differ in spacing, hyphenation, ligatures and line order. So both are reduced to their letters and
// digits, lower-cased, and the passage is matched in short anchors that are chained in order: what
// lies between two anchors that agree is marked too, and edges are stretched while the text agrees.

/** Characters [from, to) of one text piece to mark. */
export interface PieceMark {
  piece: number
  from: number
  to: number
}

export interface PassageMarks {
  marks: PieceMark[]
  /** How much of the passage was found here (0–1); a passage that runs over pages finds part of itself on each. */
  found: number
}

const ANCHOR = 16
const WORD_CHAR = /[\p{L}\p{N}]/u

interface Hit {
  page: number
  at: number
  length: number
}

interface Run {
  start: number
  end: number
  at: number
  atEnd: number
  anchors: number
}

const fold = (ch: string) => ch.normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase()

/** The letters and digits of a text, folded, each with the piece and offset it came from. */
function stream(pieces: string[]) {
  let text = ''
  const at: { piece: number; offset: number }[] = []
  pieces.forEach((piece, p) => {
    let offset = 0
    for (const ch of piece) {
      if (WORD_CHAR.test(ch)) {
        for (const c of fold(ch)) {
          text += c
          at.push({ piece: p, offset })
        }
      }
      offset += ch.length
    }
  })
  return { text, at }
}

const lettersOf = (text: string) => stream([text]).text

export function locatePassage(pieces: string[], passage: string): PassageMarks {
  const page = stream(pieces)
  const wanted = lettersOf(passage)
  if (!wanted || !page.text) return { marks: [], found: 0 }

  const step = Math.min(ANCHOR, wanted.length)
  const anchors: { at: number; text: string }[] = []
  for (let at = 0; at < wanted.length; at += step) {
    const text = wanted.slice(at, at + step)
    if (text.length < Math.min(6, step)) break
    anchors.push({ at, text })
  }

  // The anchors after a first one, in passage order, each after the last and not unreasonably far from it.
  const chainFrom = (first: number, index: number) => {
    const hits: Hit[] = [{ page: index, at: anchors[first].at, length: anchors[first].text.length }]
    for (const anchor of anchors.slice(first + 1)) {
      const last = hits[hits.length - 1]
      const from = last.page + last.length
      const found = page.text.indexOf(anchor.text, from)
      if (found < 0 || found - from > (anchor.at - (last.at + last.length)) * 2 + 240) continue
      hits.push({ page: found, at: anchor.at, length: anchor.text.length })
    }
    return hits
  }

  // A few starting points, keeping the longest chain, then the tightest: the passage may begin on
  // the page before, or its first words may also appear earlier on this page (say, in its heading).
  const span = (chain: Hit[]) => chain[chain.length - 1].page + chain[chain.length - 1].length - chain[0].page
  let hits: Hit[] = []
  let starts = 0
  for (let a = 0; a < anchors.length && starts < 8 && hits.length < anchors.length - a; a++) {
    for (let i = page.text.indexOf(anchors[a].text); i >= 0 && starts < 8; i = page.text.indexOf(anchors[a].text, i + 1)) {
      starts++
      const chain = chainFrom(a, i)
      if (chain.length > hits.length || (chain.length === hits.length && span(chain) < span(hits))) hits = chain
    }
  }
  if (hits.length === 0) return { marks: [], found: 0 }

  // Join anchors whose gap on the page is about the gap in the passage, then stretch each run's edges.
  let runs: Run[] = []
  for (const hit of hits) {
    const run = runs.at(-1)
    if (run && hit.page - run.end <= hit.at - run.atEnd + 24) {
      run.end = hit.page + hit.length
      run.atEnd = hit.at + hit.length
      run.anchors++
    } else {
      runs.push({ start: hit.page, end: hit.page + hit.length, at: hit.at, atEnd: hit.at + hit.length, anchors: 1 })
    }
  }
  // A lone anchor beside a real run is more likely a phrase that happens to repeat.
  if (runs.some((r) => r.anchors >= 3)) runs = runs.filter((r) => r.anchors > 1)

  let found = 0
  for (const run of runs) {
    while (run.atEnd < wanted.length && run.end < page.text.length && page.text[run.end] === wanted[run.atEnd]) {
      run.end++
      run.atEnd++
    }
    while (run.at > 0 && run.start > 0 && page.text[run.start - 1] === wanted[run.at - 1]) {
      run.start--
      run.at--
    }
    found += run.atEnd - run.at
  }

  // Back to pieces: one mark per piece a run touches, from its first to its last marked character.
  const marks: PieceMark[] = []
  for (const run of runs) {
    for (let i = run.start; i < run.end; i++) {
      const { piece, offset } = page.at[i]
      const mark = marks.at(-1)
      const end = offset + pieces[piece].slice(offset).match(/^[\s\S]/u)![0].length
      if (mark && mark.piece === piece) mark.to = Math.max(mark.to, end)
      else marks.push({ piece, from: offset, to: end })
    }
    // The closing punctuation goes with the words ("Affairs." rather than "Affairs").
    const last = marks[marks.length - 1]
    const after = /^[^\s\p{L}\p{N}]+/u.exec(pieces[last.piece].slice(last.to))
    if (after) last.to += after[0].length
  }
  return { marks, found: Math.min(1, found / wanted.length) }
}
