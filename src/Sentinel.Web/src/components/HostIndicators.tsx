import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, timeAgo, type Check, type CheckState, type CheckStatus, type Incident, type MetricSeries } from '../api/client'
import { StatusBadge } from './Layout'
import { MODULES } from './ModuleForms'
import { EventLogDetails } from './EventLogDetails'
import { LineChart, Sparkline } from './Sparkline'

/** Категория проверки — для фильтров и значка. */
const CATEGORIES: { id: string; title: string; icon: string; match: (m: string) => boolean }[] = [
  { id: 'system', title: 'Система', icon: '🖥', match: m => m === 'system' },
  { id: 'services', title: 'Службы', icon: '⚙', match: m => m === 'services' },
  { id: 'disks', title: 'Диски', icon: '💽', match: m => m === 'disks' },
  { id: 'eventlog', title: 'Журнал', icon: '📋', match: m => m === 'eventlog' },
  { id: 'net', title: 'Сеть', icon: '🌐', match: m => m.startsWith('net.') },
  { id: 'backup', title: 'Бэкапы', icon: '☁', match: m => m.startsWith('backup.') },
  { id: 'cctv', title: 'Видео', icon: '📹', match: m => m.startsWith('cctv.') },
  { id: 'vm', title: 'ВМ', icon: '🧊', match: m => m.startsWith('vm.') },
]
const categoryOf = (moduleId: string) => CATEGORIES.find(c => c.match(moduleId))?.id ?? 'other'

/** Какой ряд метрик рисовать в строке — первый найденный по этому списку. */
const PRIMARY_METRICS = ['cpu_percent', 'memory_used_percent', 'disk_free_percent', 'ping_ms', 'tcp_connect_ms', 'eventlog_errors',
  'cctv_channels_online', 'clock_offset_seconds', 'backup_size_bytes', 'vm_uptime_hours', 'rtsp_describe_ms', 'cert_days_left']
const UNITS: Record<string, string> = { cpu_percent: '%', memory_used_percent: '%', disk_free_percent: '%', ping_ms: ' мс', tcp_connect_ms: ' мс', clock_offset_seconds: ' с', rtsp_describe_ms: ' мс', cert_days_left: ' дн.', vm_uptime_hours: ' ч' }
const METRIC_TITLE: Record<string, string> = {
  cpu_percent: 'Загрузка CPU', memory_used_percent: 'Занято памяти', memory_free_mb: 'Свободно памяти, МБ', disk_free_percent: 'Свободно на диске', disk_free_gb: 'Свободно, ГБ',
  ping_ms: 'Задержка', ping_loss: 'Потери, %', tcp_connect_ms: 'Время соединения', eventlog_errors: 'Событий за окно', cctv_channels_online: 'Каналов онлайн',
  clock_offset_seconds: 'Расхождение часов', uptime_seconds: 'Аптайм, с', backup_size_bytes: 'Размер бэкапа', backup_duration_s: 'Длительность бэкапа, с',
  vm_uptime_hours: 'Аптайм ВМ', vm_running: 'ВМ работает', vm_checkpoints: 'Контрольных точек', rtsp_describe_ms: 'Ответ RTSP', cert_days_left: 'До окончания сертификата', cctv_time_drift_s: 'Расхождение часов, с',
}

const STATUS_COLOR: Record<string, string> = { critical: 'var(--critical)', warning: 'var(--warning)', unknown: 'var(--text-muted)', ok: 'var(--ok)', progress: 'var(--accent)' }
const RANK: Record<string, number> = { critical: 0, warning: 1, unknown: 2, progress: 3, ok: 4 }

interface Row { check: Check; state: CheckState; category: string; incident?: Incident; tone: string; series: MetricSeries[] }

export function HostIndicators({ checks, incidents, metrics, canOperate, onHide, onEditCheck, onChanged }: {
  checks: Check[]; incidents: Incident[]; metrics: MetricSeries[]; canOperate: boolean
  onHide: (c: Check, key: string) => void; onEditCheck: (c: Check) => void; onChanged: () => void
}) {
  const rows = useMemo<Row[]>(() => checks.flatMap(check => check.states.map(state => {
    const incident = incidents.find(i => i.checkId === check.id && i.key === state.key)
    const tone = incident?.status === 'inProgress' ? 'progress' : state.status
    const series = metrics.filter(m => m.checkId === check.id && m.key === state.key)
    return { check, state, category: categoryOf(check.moduleId), incident, tone, series }
  })).sort((a, b) => RANK[a.tone] - RANK[b.tone] || a.check.name.localeCompare(b.check.name) || a.state.key.localeCompare(b.state.key)), [checks, incidents, metrics])

  const counts = {
    critical: rows.filter(r => r.tone === 'critical').length,
    warning: rows.filter(r => r.tone === 'warning' || r.tone === 'unknown').length,
    progress: rows.filter(r => r.tone === 'progress').length,
    ok: rows.filter(r => r.tone === 'ok').length,
  }
  const problems = rows.length - counts.ok
  const [onlyProblems, setOnlyProblems] = useState<boolean>(problems > 0)
  const [category, setCategory] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)

  const inCategory = rows.filter(r => !category || r.category === category)
  const visible = onlyProblems ? inCategory.filter(r => r.tone !== 'ok') : inCategory
  const hiddenOk = onlyProblems ? inCategory.length - visible.length : 0
  const presentCategories = CATEGORIES.filter(c => rows.some(r => r.category === c.id))
  const selectedRow = rows.find(r => r.check.id + '|' + r.state.key === selected)

  return (
    <>
      <div className="stat-grid">
        <Stat label="Критично" value={counts.critical} color={counts.critical ? 'var(--critical)' : undefined} />
        <Stat label="Внимание" value={counts.warning} color={counts.warning ? 'var(--warning)' : undefined} />
        <Stat label="В работе" value={counts.progress} color={counts.progress ? 'var(--accent)' : undefined} />
        <Stat label="В порядке" value={counts.ok} color="var(--ok)" />
      </div>

      <div className="chips">
        <button className={'chip' + (onlyProblems ? ' on' : '')} onClick={() => setOnlyProblems(true)}>Только проблемы{problems ? ` ${problems}` : ''}</button>
        <button className={'chip' + (!onlyProblems ? ' on' : '')} onClick={() => setOnlyProblems(false)}>Все {rows.length}</button>
        <span className="chip-sep" />
        {presentCategories.map(c => (
          <button key={c.id} className={'chip' + (category === c.id ? ' on' : '')} onClick={() => setCategory(category === c.id ? null : c.id)}>
            <span aria-hidden="true">{c.icon}</span> {c.title}
          </button>
        ))}
      </div>

      <div className="ind-table">
        <div className="ind-row ind-head"><span /><span>Проверка</span><span>Показатель</span><span>Состояние</span><span>24 ч</span><span>Когда</span></div>
        {visible.map(r => {
          const id = r.check.id + '|' + r.state.key
          const primary = PRIMARY_METRICS.map(n => r.series.find(s => s.name === n)).find(Boolean)
          return (
            <div key={id} className={'ind-row' + (selected === id ? ' selected' : '') + (r.tone === 'critical' ? ' crit' : '')} onClick={() => setSelected(selected === id ? null : id)}>
              <span className="dot" style={{ background: STATUS_COLOR[r.tone] }} title={r.tone === 'progress' ? 'в работе' : r.state.status} />
              <span className="trunc" title={`${r.check.name} · ${MODULES[r.check.moduleId]?.title ?? r.check.moduleId}`}>{r.check.name}</span>
              <span className="trunc mono" title={r.state.key}>{r.state.key || '—'}</span>
              <span className="trunc" title={r.state.summary}>
                {r.state.summary}
                {r.state.pendingStatus && r.state.pendingStatus !== r.state.status && <span className="muted small"> → {r.state.pendingStatus} ({r.state.pendingCount}/{r.state.confirmCount})</span>}
                {alertIn(r) !== null && <span className="muted small" title="Задержка оповещения: кратковременный пик не будит. Инцидент откроется, если проблема продержится."> · оповещение через {alertIn(r)} мин</span>}
              </span>
              <span>{primary && <Sparkline points={primary.points} color={STATUS_COLOR[r.tone === 'ok' ? 'progress' : r.tone]} />}</span>
              <span className="muted small" title={r.state.lastResultAt}>{timeAgo(r.state.lastResultAt)}</span>
            </div>
          )
        })}
        {visible.length === 0 && <div className="ind-empty muted">{onlyProblems ? 'Проблем нет — всё в порядке.' : 'Показателей нет.'}</div>}
        {hiddenOk > 0 && (
          <div className="ind-more" onClick={() => setOnlyProblems(false)}>
            <span>Остальные в порядке — {hiddenOk}</span><a href="#" onClick={e => e.preventDefault()}>развернуть</a>
          </div>
        )}
      </div>

      {selectedRow && (
        <IndicatorDetails row={selectedRow} canOperate={canOperate} onClose={() => setSelected(null)}
          onHide={() => { onHide(selectedRow.check, selectedRow.state.key); setSelected(null) }}
          onEditCheck={() => onEditCheck(selectedRow.check)} onChanged={onChanged} />
      )}
    </>
  )
}

/** Проблема есть, но задержка оповещения ещё не истекла: через сколько минут откроется инцидент (null — не ждём). */
function alertIn(r: Row): number | null {
  if (r.state.status === 'ok' || r.incident || !r.check.alertDelayMinutes || !r.state.problemSince) return null
  const left = r.check.alertDelayMinutes - (Date.now() - new Date(r.state.problemSince).getTime()) / 60000
  return left > 0 ? Math.ceil(left) : null
}

function Stat({ label, value, color }: { label: string; value: number; color?: string }) {
  return <div className="stat"><div className="muted small">{label}</div><div className="stat-value" style={{ color }}>{value}</div></div>
}

/** Панель деталей выбранного показателя: всё длинное живёт здесь, а не в строке списка. */
function IndicatorDetails({ row, canOperate, onClose, onHide, onEditCheck, onChanged }: {
  row: Row; canOperate: boolean; onClose: () => void; onHide: () => void; onEditCheck: () => void; onChanged: () => void
}) {
  const { check, state, incident, series } = row
  const [busy, setBusy] = useState(false)
  const charts = series.filter(s => s.points.length > 1 && !['uptime_seconds', 'memory_free_mb', 'vm_running'].includes(s.name))

  async function takeInProgress(hours: number) {
    if (!incident) return
    setBusy(true)
    try { await api.incidentInProgress(incident.id, hours); onChanged() } finally { setBusy(false) }
  }

  return (
    <div className="card ind-details">
      <div className="row between">
        <div>
          <strong>{check.name}</strong>{state.key && <span className="mono"> · {state.key}</span>}{' '}
          <StatusBadge status={state.status as CheckStatus} />
          {incident?.status === 'inProgress' && <span className="badge neutral" style={{ marginLeft: 6 }}>в работе до {new Date(incident.snoozedUntil!).toLocaleString('ru', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}</span>}
        </div>
        <button className="secondary sm" onClick={onClose}>Закрыть</button>
      </div>
      <div style={{ marginTop: 8 }}>{state.summary}</div>
      <div className="muted small" style={{ marginTop: 4 }}>
        {MODULES[check.moduleId]?.title ?? check.moduleId} · проверка каждые {check.intervalSeconds} с · последний результат {timeAgo(state.lastResultAt)}
        {state.lastChangeAt && <> · состояние с {new Date(state.lastChangeAt).toLocaleString('ru')}</>}
      </div>

      {charts.length > 0 && (
        <div className="ind-charts">
          {charts.map(s => (
            <div key={s.name}>
              <div className="muted small">{METRIC_TITLE[s.name] ?? s.name} · 24 ч</div>
              <LineChart points={s.points} unit={UNITS[s.name]} color={state.status === 'ok' ? 'var(--accent)' : STATUS_COLOR[state.status]} />
            </div>
          ))}
        </div>
      )}

      {check.moduleId === 'eventlog' && <EventLogDetails details={state.details} />}
      {check.moduleId.startsWith('cctv.') && <div className="small" style={{ marginTop: 8 }}>Каналы, диски и время регистратора — на странице <Link to="/cctv">«Видеонаблюдение»</Link>.</div>}
      {check.moduleId.startsWith('backup.') && <div className="small" style={{ marginTop: 8 }}>История запусков и логи — на странице <Link to="/backups">«Бэкапы»</Link>.</div>}

      {canOperate && (
        <div className="row" style={{ marginTop: 12, flexWrap: 'wrap', gap: 8 }}>
          {incident && incident.status !== 'inProgress' && (
            <>
              <span className="muted small">В работу:</span>
              {[{ l: '4 ч', h: 4 }, { l: 'сутки', h: 24 }, { l: '3 дня', h: 72 }, { l: 'неделя', h: 168 }].map(o => (
                <button key={o.h} className="secondary sm" disabled={busy} onClick={() => takeInProgress(o.h)}>{o.l}</button>
              ))}
            </>
          )}
          {incident && <Link className="small" to={`/incidents/${incident.id}`}>инцидент</Link>}
          <span style={{ flex: 1 }} />
          {state.key && <button className="secondary sm" onClick={onHide} title="Больше не отслеживать этот показатель">Скрыть показатель</button>}
          <button className="secondary sm" onClick={onEditCheck}>Настроить проверку</button>
        </div>
      )}
    </div>
  )
}
