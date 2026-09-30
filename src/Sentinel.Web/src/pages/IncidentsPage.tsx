import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, auth, timeAgo, type Incident } from '../api/client'
import { StatusBadge, usePolling } from '../components/Layout'

const KIND: Record<string, string> = { checkState: 'проверка', agentOffline: 'агент офлайн', backupOverdue: 'бэкап просрочен' }
const STATUS: Record<Incident['status'], { label: string; cls: string }> = {
  open: { label: 'открыт', cls: 'critical' },
  acknowledged: { label: 'принят', cls: 'warning' },
  inProgress: { label: 'в работе', cls: 'neutral' },
  resolved: { label: 'закрыт', cls: 'ok' },
}

export function IncidentsPage() {
  const [tab, setTab] = useState<'active' | 'resolved'>('active')
  const { data, error, refresh } = usePolling(async () => {
    const [list, summary] = await Promise.all([api.incidents({ status: tab, take: 300 }), api.incidentSummary()])
    return { list, summary }
  }, 10000, [tab])

  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>
  const { list, summary } = data

  return (
    <>
      <h1>Инциденты</h1>
      <div className="grid">
        <Stat label="Открытых" value={summary.open} tone={summary.open ? 'critical' : 'ok'} />
        <Stat label="В работе" value={summary.acknowledged} />
        <Stat label="Критичных" value={summary.critical} tone={summary.critical ? 'critical' : undefined} />
        <Stat label="Закрыто сегодня" value={summary.resolvedToday} tone="ok" />
      </div>
      <div className="row" style={{ margin: '18px 0 10px' }}>
        <button className={tab === 'active' ? 'sm' : 'secondary sm'} onClick={() => setTab('active')}>Активные</button>
        <button className={tab === 'resolved' ? 'sm' : 'secondary sm'} onClick={() => setTab('resolved')}>Закрытые</button>
      </div>
      <div className="card" style={{ padding: 0 }}>
        <IncidentTable incidents={list} onChanged={refresh} />
      </div>
    </>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'critical' }) {
  return (
    <div className="card">
      <div className="muted small">{label}</div>
      <div style={{ fontSize: 26, fontWeight: 600, color: tone === 'critical' ? 'var(--critical)' : tone === 'ok' ? 'var(--ok)' : undefined }}>{value}</div>
    </div>
  )
}

export function IncidentTable({ incidents, onChanged, compact }: { incidents: Incident[]; onChanged: () => void; compact?: boolean }) {
  const canOperate = auth.canOperate()
  if (!incidents.length) return <div className="muted" style={{ padding: 14 }}>Инцидентов нет.</div>
  return (
    <table>
      <thead>
        <tr><th style={{ width: 100 }}>Серьёзность</th><th>Инцидент</th>{!compact && <th>Клиент</th>}<th>Статус</th><th>Открыт</th>{canOperate && <th></th>}</tr>
      </thead>
      <tbody>
        {incidents.map(i => (
          <tr key={i.id}>
            <td><StatusBadge status={i.severity} /></td>
            <td>
              <Link to={`/incidents/${i.id}`}><strong>{i.title}</strong></Link>
              <div className="muted small">{i.summary}</div>
            </td>
            {!compact && <td>{i.tenantName}</td>}
            <td><span className={'badge ' + STATUS[i.status].cls}>{STATUS[i.status].label}</span><div className="muted small">{KIND[i.kind]}</div></td>
            <td className="muted" title={i.openedAt}>{timeAgo(i.openedAt)}{i.resolvedAt && <div className="small">закрыт {timeAgo(i.resolvedAt)}</div>}{i.snoozedUntil && i.status === 'inProgress' && <div className="small" style={{ color: 'var(--accent)' }}>молчит до {new Date(i.snoozedUntil).toLocaleString('ru', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}</div>}</td>
            {canOperate && (
              <td style={{ whiteSpace: 'nowrap' }}>
                {i.status === 'open' && <button className="secondary sm" onClick={() => api.ackIncident(i.id).then(onChanged)}>Принять</button>}
                {i.status !== 'resolved' && <InProgressButton incident={i} onChanged={onChanged} />}
                {i.status !== 'resolved' && <button className="secondary sm" style={{ marginLeft: 6 }} onClick={() => confirm('Закрыть инцидент вручную?') && api.resolveIncident(i.id).then(onChanged)}>Закрыть</button>}
              </td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  )
}

const EVENT: Record<string, string> = { opened: 'Открыт', severity: 'Изменение серьёзности', acknowledged: 'Принят', in_progress: 'Взят в работу', in_progress_expired: 'Срок работы истёк', comment: 'Комментарий', resolved: 'Закрыт', notified: 'Уведомление' }

/** «В работе на N …»: инцидент не шумит уведомлениями до срока, после срока оповещение приходит снова. */
function InProgressButton({ incident, onChanged }: { incident: Incident; onChanged: () => void }) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const OPTIONS: { label: string; hours: number }[] = [
    { label: '4 часа', hours: 4 }, { label: 'до завтра (24 ч)', hours: 24 },
    { label: '3 дня', hours: 72 }, { label: 'неделя', hours: 168 },
  ]
  async function take(hours: number) {
    setBusy(true)
    try { await api.incidentInProgress(incident.id, hours); setOpen(false); onChanged() }
    finally { setBusy(false) }
  }
  if (!open) return <button className="secondary sm" style={{ marginLeft: 6 }} title="Взять в работу: уведомления молчат до срока, после срока придут снова" onClick={() => setOpen(true)}>В работу…</button>
  return (
    <span className="row" style={{ display: 'inline-flex', gap: 4, marginLeft: 6 }}>
      {OPTIONS.map(o => <button key={o.hours} className="secondary sm" disabled={busy} onClick={() => take(o.hours)}>{o.label}</button>)}
      <button className="secondary sm" onClick={() => setOpen(false)}>×</button>
    </span>
  )
}

export function IncidentPage() {
  const { id = '' } = useParams()
  const { data, error, refresh } = usePolling(() => api.incident(id), 15000, [id])
  const [comment, setComment] = useState('')
  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>
  const i = data.incident
  const canOperate = auth.canOperate()

  async function send(action: 'ack' | 'resolve' | 'comment') {
    if (action === 'ack') await api.ackIncident(i.id, comment || undefined)
    else if (action === 'resolve') await api.resolveIncident(i.id, comment || undefined)
    else await api.commentIncident(i.id, comment)
    setComment(''); refresh()
  }

  return (
    <>
      <div className="row"><StatusBadge status={i.severity} /><h1 style={{ margin: 0 }}>{i.title}</h1></div>
      <div className="muted" style={{ margin: '6px 0 16px' }}>
        {i.tenantName} · <Link to={`/hosts/${i.hostId}`}>{i.hostName}</Link> · {KIND[i.kind]} · <span className={'badge ' + STATUS[i.status].cls}>{STATUS[i.status].label}</span>
      </div>
      <div className="card"><strong>{i.summary}</strong>
        <div className="muted small" style={{ marginTop: 6 }}>Открыт {new Date(i.openedAt).toLocaleString('ru')}{i.resolvedAt && ` · закрыт ${new Date(i.resolvedAt).toLocaleString('ru')}`}</div>
      </div>

      <h2>История</h2>
      <div className="card" style={{ padding: 0 }}>
        <table><tbody>
          {data.events.map((e, n) => (
            <tr key={n}>
              <td className="muted" style={{ width: 150, whiteSpace: 'nowrap' }}>{new Date(e.at).toLocaleString('ru')}</td>
              <td style={{ width: 180 }}>{EVENT[e.type] ?? e.type}{e.userName && <div className="muted small">{e.userName}</div>}</td>
              <td>{e.message}</td>
            </tr>
          ))}
        </tbody></table>
      </div>

      {canOperate && (
        <div className="card" style={{ marginTop: 14 }}>
          <div className="field"><label>Комментарий</label><input value={comment} onChange={e => setComment(e.target.value)} placeholder="что сделано / что выяснили" /></div>
          <div className="row">
            {i.status === 'open' && <button className="sm" onClick={() => send('ack')}>Взять в работу</button>}
            {i.status !== 'resolved' && <button className="secondary sm" onClick={() => send('resolve')}>Закрыть вручную</button>}
            <button className="secondary sm" disabled={!comment} onClick={() => send('comment')}>Добавить комментарий</button>
          </div>
        </div>
      )}
    </>
  )
}
