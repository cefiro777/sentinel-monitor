import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api, auth, timeAgo, type BackupJob, type BackupRun } from '../api/client'
import { usePolling } from '../components/Layout'
import { CheckEditorPanel, type EditorTarget } from '../components/CheckEditorPanel'

const MODULE: Record<string, string> = { 'backup.files': 'Файлы', 'backup.1c.file': '1С файловая', 'backup.mssql': 'MS SQL', 'backup.postgres': 'PostgreSQL' }
const size = (b: number) => b < 1024 ? `${b} Б` : b < 1048576 ? `${(b / 1024).toFixed(1)} КБ` : b < 1073741824 ? `${(b / 1048576).toFixed(1)} МБ` : `${(b / 1073741824).toFixed(2)} ГБ`

export function BackupsPage() {
  const { data, error, refresh } = usePolling(() => api.backups(), 20000)
  const [open, setOpen] = useState<BackupJob | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorTarget | null>(null)
  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>

  const problems = data.filter(j => j.overdue || (j.lastRun && !j.lastRun.success) || (j.cloudEnabled && j.lastRun?.success && !j.lastRun.cloudUploaded))

  async function runNow(j: BackupJob) {
    setMsg(null)
    try { const r = await api.runBackup(j.checkId); setMsg(`${j.name}: ${r.status === 'sent' ? 'запущен на агенте' : 'команда в очереди, агент офлайн'}`) }
    catch (e) { setMsg((e as Error).message) }
  }

  return (
    <>
      <div className="row between">
        <h1>Бэкапы</h1>
        {auth.canOperate() && <button className="sm" onClick={() => setEditor({ mode: 'new', moduleIds: ['backup.files', 'backup.1c.file', 'backup.mssql', 'backup.postgres'], hint: 'Задание выполняет агент на выбранном хосте: он должен видеть исходные файлы/базу и папку назначения.' })}>+ Задание бэкапа</button>}
      </div>
      {editor && <CheckEditorPanel target={editor} onClose={changed => { setEditor(null); if (changed) refresh() }} />}
      <div className="grid">
        <Stat label="Заданий" value={data.length} />
        <Stat label="С проблемами" value={problems.length} tone={problems.length ? 'critical' : 'ok'} />
        <Stat label="В облаке" value={data.filter(j => j.cloudEnabled).length} />
        <Stat label="Просрочено" value={data.filter(j => j.overdue).length} tone={data.some(j => j.overdue) ? 'critical' : 'ok'} />
      </div>
      {msg && <div className="notice">{msg}</div>}
      <div className="card" style={{ padding: 0, marginTop: 14 }}>
        <table>
          <thead><tr><th style={{ width: 24 }}></th><th>Задание</th><th>Хост / клиент</th><th>Куда</th><th>Расписание</th><th>Последний запуск</th><th>Последний успешный</th><th>Облако</th><th></th></tr></thead>
          <tbody>
            {data.map(j => {
              const key = j.checkId + j.target
              const tone = j.overdue || (j.lastRun && !j.lastRun.success) ? 'critical' : j.cloudEnabled && j.lastRun?.success && !j.lastRun.cloudUploaded ? 'warning' : j.lastRun ? 'ok' : 'unknown'
              return (
                <tr key={key} className="clickable" onClick={() => setOpen(open?.checkId === j.checkId && open.target === j.target ? null : j)}>
                  <td><span className={'dot ' + tone} /></td>
                  <td><strong>{j.name}</strong>{j.target && <span className="mono"> / {j.target}</span>}<div className="muted small">{MODULE[j.moduleId] ?? j.moduleId}{!j.enabled && ' · выключено'}</div></td>
                  <td><Link to={`/hosts/${j.hostId}`} onClick={e => e.stopPropagation()}>{j.hostName}</Link><div className="muted small">{j.tenantName}</div></td>
                  <td className="mono small" style={{ maxWidth: 240, wordBreak: 'break-all' }}>{j.destination || <span className="muted">—</span>}{j.cloudEnabled && <div className="muted small">облако: {j.cloudFolder || '/Sentinel'}/{'{клиент}/{хост}/{задание}'}</div>}</td>
                  <td className="muted">{j.scheduleText}</td>
                  <td>{j.lastRun ? <><span className={'badge ' + (j.lastRun.success ? 'ok' : 'critical')}>{j.lastRun.success ? j.lastRun.kind : 'ошибка'}</span> <span className="muted small">{timeAgo(j.lastRun.finishedAt)}{j.lastRun.success && ` · ${size(j.lastRun.sizeBytes)}`}</span>{!j.lastRun.success && <div className="small" style={{ color: 'var(--critical)' }}>{j.lastRun.error}</div>}</> : <span className="muted">ещё не выполнялся</span>}</td>
                  <td>{j.overdue ? <span className="badge critical">просрочен</span> : <span className="muted">{timeAgo(j.lastSuccessAt)}</span>}</td>
                  <td>{!j.cloudEnabled ? <span className="muted">—</span> : j.lastCloudAt ? <span className="badge ok" title={j.lastCloudAt}>{timeAgo(j.lastCloudAt)}</span> : <span className="badge warning">не выгружен</span>}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {auth.hasMfa() && j.agentOnline && <button className="secondary sm" onClick={e => { e.stopPropagation(); runNow(j) }}>Запустить</button>}
                    {auth.canOperate() && <button className="secondary sm" style={{ marginLeft: 6 }} onClick={e => { e.stopPropagation(); setEditor({ mode: 'edit', hostId: j.hostId, checkId: j.checkId }) }}>Настроить</button>}
                  </td>
                </tr>
              )
            })}
            {data.length === 0 && <tr><td colSpan={9} className="muted">Заданий бэкапа нет — нажмите «+ Задание бэкапа».</td></tr>}
          </tbody>
        </table>
      </div>
      {open && <RunsPanel job={open} onClose={() => { setOpen(null); refresh() }} />}
    </>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'critical' }) {
  return <div className="card"><div className="muted small">{label}</div><div style={{ fontSize: 26, fontWeight: 600, color: tone === 'critical' ? 'var(--critical)' : tone === 'ok' ? 'var(--ok)' : undefined }}>{value}</div></div>
}

function RunsPanel({ job, onClose }: { job: BackupJob; onClose: () => void }) {
  const { data } = usePolling(() => api.backupRuns(job.checkId), 15000, [job.checkId])
  const [logOf, setLogOf] = useState<BackupRun | null>(null)
  const runs = (data ?? []).filter(r => r.target === job.target)
  return (
    <div className="card" style={{ marginTop: 14, borderColor: 'var(--accent)' }}>
      <div className="row between"><h2 style={{ margin: 0 }}>История: {job.name}{job.target && ` / ${job.target}`}</h2><button className="secondary sm" onClick={onClose}>Закрыть</button></div>
      <table style={{ marginTop: 10 }}>
        <thead><tr><th>Когда</th><th>Тип</th><th>Результат</th><th>Размер</th><th>Файл</th><th>Облако</th><th></th></tr></thead>
        <tbody>
          {runs.map(r => (
            <tr key={r.id}>
              <td className="muted" title={r.finishedAt}>{new Date(r.finishedAt).toLocaleString('ru')}<div className="small">{Math.round((new Date(r.finishedAt).getTime() - new Date(r.startedAt).getTime()) / 60000)} мин</div></td>
              <td className="mono">{r.kind}</td>
              <td>{r.success ? <span className="badge ok">ок{r.verified && ' · проверен'}</span> : <span className="badge critical" title={r.error}>ошибка</span>}{!r.success && <div className="small" style={{ color: 'var(--critical)', maxWidth: 360 }}>{r.error}</div>}</td>
              <td>{r.success ? size(r.sizeBytes) : '—'}</td>
              <td className="mono small" style={{ maxWidth: 300, wordBreak: 'break-all' }}>{r.artifactPath}</td>
              <td>{!r.cloudEnabled ? '—' : r.cloudUploaded ? <span className="badge ok" title={r.cloudPath ?? ''}>да</span> : <span className="badge warning">нет</span>}</td>
              <td>{r.log && <button className="secondary sm" onClick={() => setLogOf(logOf?.id === r.id ? null : r)}>лог</button>}</td>
            </tr>
          ))}
          {runs.length === 0 && <tr><td colSpan={7} className="muted">Запусков ещё не было</td></tr>}
        </tbody>
      </table>
      {logOf && <pre className="cmd" style={{ marginTop: 10, maxHeight: 320, overflow: 'auto' }}>{logOf.log}</pre>}
    </div>
  )
}
