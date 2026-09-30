import { useEffect, useState } from 'react'
import { api, type EventHint } from '../api/client'

interface LogEvent { id: number; time: string; level: string; source: string; message: string }

let hintsCache: EventHint[] | null = null
let hintsPromise: Promise<EventHint[]> | null = null
function loadHints(): Promise<EventHint[]> {
  if (hintsCache) return Promise.resolve(hintsCache)
  hintsPromise ??= api.eventHints().then(h => (hintsCache = h)).catch(() => (hintsCache = []))
  return hintsPromise
}

/** Подсказка по событию: сначала точное совпадение источника и кода, потом — подсказка на весь источник. */
export function findHint(hints: EventHint[], source: string, id: number): EventHint | undefined {
  const src = source.toLowerCase()
  const bySource = hints.filter(h => src === h.source.toLowerCase() || src.endsWith(h.source.toLowerCase()) || h.source.toLowerCase().endsWith(src))
  return bySource.find(h => h.ids.includes(id)) ?? bySource.find(h => h.ids.length === 0)
}

/**
 * События журнала из результата проверки: одинаковые (источник + код) свёрнуты в одну строку с количеством,
 * к известным — причина и что делать из базы подсказок.
 */
export function EventLogDetails({ details }: { details: unknown }) {
  const [hints, setHints] = useState<EventHint[]>(hintsCache ?? [])
  const [open, setOpen] = useState<string | null>(null)
  useEffect(() => { loadHints().then(setHints) }, [])

  const events = ((details as { events?: LogEvent[] } | undefined)?.events ?? [])
  if (events.length === 0) return <div className="muted small">Событий в окне проверки нет.</div>

  const groups = new Map<string, { source: string; id: number; level: string; count: number; last: string; message: string }>()
  for (const e of events) {
    const key = `${e.source}|${e.id}`
    const g = groups.get(key)
    if (g) { g.count++; if (e.time > g.last) { g.last = e.time; g.message = e.message } }
    else groups.set(key, { source: e.source, id: e.id, level: e.level, count: 1, last: e.time, message: e.message })
  }
  const list = [...groups.values()].sort((a, b) => b.count - a.count || b.last.localeCompare(a.last))

  return (
    <div className="event-list">
      {list.map(g => {
        const key = `${g.source}|${g.id}`
        const hint = findHint(hints, g.source, g.id)
        const expanded = open === key
        return (
          <div key={key} className="event-group">
            <div className="event-head" onClick={() => setOpen(expanded ? null : key)}>
              <span className={'badge ' + (g.level === 'Critical' ? 'critical' : g.level === 'Error' ? 'critical' : 'warning')}>{g.level === 'Critical' ? 'критично' : g.level === 'Error' ? 'ошибка' : 'предупр.'}</span>
              <span className="mono">{g.id}</span>
              <span className="event-source" title={g.source}>{g.source}</span>
              {g.count > 1 && <span className="badge neutral" title="Сколько раз за окно проверки">×{g.count}</span>}
              {hint && <span className="event-hint-title">💡 {hint.title}</span>}
              <span className="muted small" style={{ marginLeft: 'auto' }}>{new Date(g.last).toLocaleString('ru', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}</span>
            </div>
            {expanded && (
              <div className="event-body">
                <div className="event-message">{g.message}</div>
                {hint ? (
                  <div className="event-hint">
                    <div><strong>Причина.</strong> {hint.cause}</div>
                    <div style={{ marginTop: 4 }}><strong>Что делать.</strong> {hint.fix}</div>
                  </div>
                ) : (
                  <div className="muted small">Подсказки для этого события пока нет.</div>
                )}
                <a className="small" href={`https://www.google.com/search?q=${encodeURIComponent(`Event ID ${g.id} ${g.source}`)}`} target="_blank" rel="noreferrer">Искать «Event ID {g.id} {g.source}» в интернете ↗</a>
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
