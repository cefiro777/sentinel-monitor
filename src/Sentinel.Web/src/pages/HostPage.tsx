import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api, auth, timeAgo, type Check, type CheckTemplate, type Command } from '../api/client'
import { StatusDot, usePolling } from '../components/Layout'
import { MODULES } from '../components/ModuleForms'
import { IncidentTable } from './IncidentsPage'
import { CheckForm, newDraft } from '../components/CheckForm'
import { HostIndicators } from '../components/HostIndicators'

const RANK_STATUS: Record<string, number> = { critical: 0, warning: 1, unknown: 2, ok: 3 }

export function HostPage() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const { data, error, refresh } = usePolling(async () => {
    const [host, checks, commands, incidents, metrics] = await Promise.all([
      api.host(id), api.checks(id), api.commands(id), api.incidents({ hostId: id, status: 'active' }), api.metrics(id).catch(() => []),
    ])
    return { host, checks, commands, incidents, metrics }
  }, 10000, [id])
  const [editing, setEditing] = useState<Check | 'new' | null>(null)
  const [templateMode, setTemplateMode] = useState<'apply' | 'save' | null>(null)
  const [tab, setTab] = useState<'indicators' | 'checks' | 'incidents' | 'commands'>('indicators')

  async function hideKey(c: Check, key: string) {
    if (!confirm(`Больше не отслеживать «${key}» в проверке «${c.name}»? Вернуть можно в настройке проверки.`)) return
    await api.updateCheck(host.id, c.id, {
      moduleId: c.moduleId, name: c.name, enabled: c.enabled, intervalSeconds: c.intervalSeconds, confirmCount: c.confirmCount,
      settings: c.settings, ignoredKeys: [...c.ignoredKeys, key], alertDelayMinutes: c.alertDelayMinutes,
    })
    refresh()
  }
  const [params, setParams] = useSearchParams()
  const addModule = params.get('add')
  useEffect(() => { if (addModule) { setEditing('new'); setTab('checks') } }, [addModule])

  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>
  const { host, checks, commands, incidents, metrics } = data
  const canOperate = auth.canOperate()

  async function remove() {
    if (!confirm(`Удалить хост «${host.name}» вместе с историей проверок?`)) return
    await api.deleteHost(host.id)
    nav('/')
  }

  return (
    <>
      <div className="row between">
        <h1 className="row"><StatusDot status={host.worstStatus} /> {host.name} <span className="muted" style={{ fontWeight: 400 }}>· {host.tenantName}</span></h1>
        {canOperate && <button className="secondary sm danger" onClick={remove}>Удалить хост</button>}
      </div>

      <div className="row" style={{ alignItems: 'stretch', gap: 14, flexWrap: 'wrap' }}>
        <div className="card" style={{ flex: '0 1 320px', minWidth: 280 }}>
          <h2 style={{ marginTop: 0 }}>Агент</h2>
          {host.agent ? (
            <dl className="kv">
              <dt>Состояние</dt><dd><span className={'badge ' + (host.agent.isOnline ? 'online' : 'offline')}>{host.agent.isOnline ? 'онлайн' : 'офлайн'}</span></dd>
              <dt>Последняя связь</dt><dd>{timeAgo(host.agent.lastSeenAt)}</dd>
              <dt>Версия</dt><dd>{host.agent.agentVersion || '—'}</dd>
              <dt>ОС</dt><dd>{host.agent.osVersion || '—'}</dd>
              <dt>Конфигурация</dt><dd>v{host.agent.reportedConfigVersion} {host.agent.reportedConfigVersion !== host.agent.configVersion && <span className="badge warning">ожидается v{host.agent.configVersion}</span>}</dd>
              <dt>Буфер</dt><dd>{host.agent.queueDepth} результатов</dd>
            </dl>
          ) : <div className="muted">Агент не установлен. Создайте токен на странице «Установка агентов».</div>}
        </div>

        {host.agent && canOperate && <CommandPanel hostId={host.id} online={host.agent.isOnline} checks={checks} onIssued={refresh} />}
      </div>

      <MaintenancePanel hostId={host.id} canOperate={canOperate} />

      <div className="tabs">
        <button className={'tab' + (tab === 'indicators' ? ' on' : '')} onClick={() => setTab('indicators')}>Показатели</button>
        <button className={'tab' + (tab === 'checks' ? ' on' : '')} onClick={() => setTab('checks')}>Проверки <span className="muted">{checks.length}</span></button>
        <button className={'tab' + (tab === 'incidents' ? ' on' : '')} onClick={() => setTab('incidents')}>Инциденты {incidents.length > 0 && <span className="badge critical">{incidents.length}</span>}</button>
        <button className={'tab' + (tab === 'commands' ? ' on' : '')} onClick={() => setTab('commands')}>Команды</button>
      </div>

      {tab === 'indicators' && (
        checks.length === 0
          ? <div className="muted">Проверок нет — добавьте на вкладке «Проверки» или примените шаблон.</div>
          : <HostIndicators checks={checks} incidents={incidents} metrics={metrics} canOperate={canOperate}
              onHide={hideKey} onEditCheck={c => { setEditing(c); setTab('checks') }} onChanged={refresh} />
      )}

      {tab === 'checks' && (
        <>
          {canOperate && (
            <div className="row" style={{ justifyContent: 'flex-end', gap: 8, marginBottom: 10 }}>
              <button className="secondary sm" onClick={() => setTemplateMode('apply')}>Применить шаблон</button>
              {checks.length > 0 && <button className="secondary sm" onClick={() => setTemplateMode('save')}>Сохранить как шаблон</button>}
              <button className="sm" onClick={() => setEditing('new')}>+ Добавить проверку / бэкап / регистратор</button>
            </div>
          )}
          {templateMode && <HostTemplatePanel mode={templateMode} hostId={host.id} hostName={host.name} tenantId={host.tenantId} onClose={() => { setTemplateMode(null); refresh() }} />}
          {editing && <CheckEditor hostId={host.id} check={editing === 'new' ? null : editing} initialModule={addModule ?? undefined} onClose={() => { setEditing(null); if (addModule) setParams({}); refresh() }} />}
          <div className="card" style={{ padding: 0, marginTop: 10 }}>
            <table>
              <thead><tr><th>Проверка</th><th>Модуль</th><th>Интервал</th><th>Показателей</th><th>Не отслеживаются</th><th></th></tr></thead>
              <tbody>
                {checks.map(c => {
                  const worst = c.states.reduce((w, st) => (RANK_STATUS[st.status] < RANK_STATUS[w] ? st.status : w), 'ok' as Check['states'][number]['status'])
                  return (
                    <tr key={c.id}>
                      <td><StatusDot status={c.states.length ? worst : 'unknown'} /> <strong>{c.name}</strong>{!c.enabled && <span className="muted small"> · выключена</span>}</td>
                      <td className="muted">{MODULES[c.moduleId]?.title ?? c.moduleId}</td>
                      <td className="muted">{c.intervalSeconds} с</td>
                      <td className="muted">{c.states.length}</td>
                      <td className="muted small">{c.ignoredKeys.join(', ') || '—'}</td>
                      <td style={{ textAlign: 'right' }}>{canOperate && <button className="secondary sm" onClick={() => setEditing(c)}>Настроить</button>}</td>
                    </tr>
                  )
                })}
                {checks.length === 0 && <tr><td colSpan={6} className="muted">Проверок нет.</td></tr>}
              </tbody>
            </table>
          </div>
        </>
      )}

      {tab === 'incidents' && (
        <div className="card" style={{ padding: 0 }}>
          {incidents.length ? <IncidentTable incidents={incidents} onChanged={refresh} compact /> : <div className="muted" style={{ padding: 14 }}>Активных инцидентов нет.</div>}
        </div>
      )}

      {tab === 'commands' && (
        <div className="card" style={{ padding: 0 }}>
          <CommandTable commands={commands} />
        </div>
      )}
    </>
  )
}

function MaintenancePanel({ hostId, canOperate }: { hostId: string; canOperate: boolean }) {
  const { data, refresh } = usePolling(() => api.maintenance(hostId), 30000, [hostId])
  const [comment, setComment] = useState('')
  const active = (data ?? []).filter(w => w.active)
  async function add(hours: number) {
    const now = new Date()
    await api.createMaintenance({ hostId, startsAt: now.toISOString(), endsAt: new Date(now.getTime() + hours * 3600_000).toISOString(), comment: comment || 'плановые работы' })
    setComment(''); refresh()
  }
  if (!canOperate && active.length === 0) return null
  return (
    <div className="card" style={{ marginTop: 14, borderColor: active.length ? 'var(--warning)' : undefined }}>
      <div className="row between">
        <div>
          <strong>Окно обслуживания</strong>
          {active.length > 0
            ? active.map(w => <div key={w.id} className="small" style={{ color: 'var(--warning)' }}>до {new Date(w.endsAt).toLocaleString('ru')} · {w.comment}{w.hostId ? '' : ' (весь клиент)'}{canOperate && w.hostId && <> · <a href="#" onClick={e => { e.preventDefault(); api.deleteMaintenance(w.id).then(refresh) }}>завершить</a></>}</div>)
            : <div className="muted small">Нет. На время работ уведомления по хосту не отправляются, офлайн агента — не инцидент.</div>}
        </div>
        {canOperate && (
          <div className="row">
            <input style={{ width: 200 }} placeholder="комментарий" value={comment} onChange={e => setComment(e.target.value)} />
            <button className="secondary sm" onClick={() => add(1)}>+1 ч</button>
            <button className="secondary sm" onClick={() => add(4)}>+4 ч</button>
            <button className="secondary sm" onClick={() => add(12)}>+12 ч</button>
          </div>
        )}
      </div>
    </div>
  )
}

/** Скрипты, которые чаще всего нужны на сервере клиента. */
const SCRIPTS: { label: string; shell: 'powershell' | 'cmd'; script: string }[] = [
  { label: 'Остановленные службы с автозапуском', shell: 'powershell', script: "Get-WmiObject Win32_Service | Where-Object { $_.StartMode -eq 'Auto' -and $_.State -ne 'Running' } | Select-Object Name, DisplayName, State | Format-Table -AutoSize" },
  { label: 'Место на дисках', shell: 'powershell', script: "Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, @{n='ГБ всего';e={[math]::Round($_.Size/1GB,1)}}, @{n='ГБ свободно';e={[math]::Round($_.FreeSpace/1GB,1)}} | Format-Table -AutoSize" },
  { label: 'Топ-10 процессов по памяти', shell: 'powershell', script: "Get-Process | Sort-Object WS -Descending | Select-Object -First 10 Name, Id, @{n='МБ';e={[math]::Round($_.WS/1MB)}} | Format-Table -AutoSize" },
  { label: 'Ошибки в журнале System за сутки', shell: 'powershell', script: "Get-EventLog -LogName System -EntryType Error -After (Get-Date).AddDays(-1) -Newest 20 | Select-Object TimeGenerated, Source, EventID, Message | Format-List" },
  { label: 'Аптайм и последняя загрузка', shell: 'powershell', script: "$os = Get-WmiObject Win32_OperatingSystem; $b = $os.ConvertToDateTime($os.LastBootUpTime); \"Загружен: $b, аптайм: $([math]::Round(((Get-Date) - $b).TotalHours,1)) ч\"" },
  { label: 'Кто в RDP-сессиях', shell: 'cmd', script: 'query user' },
  { label: 'Очередь печати', shell: 'powershell', script: "Get-WmiObject Win32_PrintJob | Select-Object Document, Owner, JobStatus, TotalPages | Format-Table -AutoSize" },
  { label: 'Свободное место в базах 1С (файловые)', shell: 'powershell', script: "Get-ChildItem -Path C:\\,D:\\ -Filter 1Cv8.1CD -Recurse -ErrorAction SilentlyContinue | Select-Object FullName, @{n='МБ';e={[math]::Round($_.Length/1MB)}}, LastWriteTime | Format-Table -AutoSize" },
]

function CommandPanel({ hostId, online, checks, onIssued }: { hostId: string; online: boolean; checks: Check[]; onIssued: () => void }) {
  const [service, setService] = useState('')
  const [script, setScript] = useState('')
  const [shell, setShell] = useState<'powershell' | 'cmd'>('powershell')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const [scriptOpen, setScriptOpen] = useState(false)
  const mfa = auth.hasMfa()

  // Службы, которые этот хост уже мониторит: и настроенные, и присланные агентом.
  const known = useMemo(() => {
    const map = new Map<string, string>()
    for (const c of checks.filter(c => c.moduleId === 'services')) {
      for (const row of (c.settings.services as { name?: string; comment?: string }[] | undefined) ?? [])
        if (row?.name) map.set(row.name, row.comment ?? '')
      for (const st of c.states) if (st.key) map.set(st.key, (st.summary.split(':')[0] ?? '').trim())
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [checks])

  async function issue(type: string, payload?: unknown) {
    setBusy(true); setMsg(null)
    try { const c = await api.issueCommand(hostId, type, payload); setMsg(`${type}: ${c.status === 'sent' ? 'отправлена агенту' : 'в очереди — выполнится, когда агент выйдет на связь'}`); onIssued() }
    catch (e) { setMsg((e as Error).message) }
    finally { setBusy(false) }
  }

  if (!mfa) {
    return (
      <div className="card" style={{ flex: '1 1 560px', minWidth: 460 }}>
        <h2 style={{ marginTop: 0 }}>Действия</h2>
        <div className="notice">Удалённые действия доступны только из сессии с подтверждённым вторым фактором. <Link to="/security">Настроить 2FA</Link>{auth.user?.totpEnabled && ' или выйдите и войдите заново с кодом.'}</div>
      </div>
    )
  }

  return (
    <div className="card" style={{ flex: '1 1 560px', minWidth: 460 }}>
      <div className="row between">
        <h2 style={{ marginTop: 0, marginBottom: 0 }}>Действия</h2>
        <span className="muted small">{online ? 'Результат — в таблице «Команды» ниже' : 'Агент офлайн: команда встанет в очередь (5 мин)'}</span>
      </div>

      <div className="row" style={{ flexWrap: 'wrap', gap: 8, marginTop: 10 }}>
        <button className="secondary sm" disabled={busy} onClick={() => issue('ping')} title="Проверить связь с агентом">Ping</button>
        <button className="secondary sm" disabled={busy} onClick={() => issue('config.refresh')} title="Забрать с сервера свежий список проверок">Обновить конфиг</button>
        <button className="secondary sm" disabled={busy} onClick={() => issue('agent.update')} title="Обновиться до опубликованной версии агента">Обновить агент</button>
        <button className="secondary sm danger" disabled={busy} onClick={() => confirm('Перезагрузить сервер через 30 секунд?') && issue('reboot', { delaySeconds: 30 })}>Перезагрузка</button>
      </div>

      <div className="row" style={{ gap: 8, marginTop: 10, flexWrap: 'wrap' }}>
        <input list="host-services" style={{ flex: '1 1 240px' }} value={service} onChange={e => setService(e.target.value)}
          placeholder={known.length > 0 ? `Служба: начните вводить (известно ${known.length})` : 'Имя службы, напр. MSSQLSERVER'} />
        <datalist id="host-services">{known.map(([name, comment]) => <option key={name} value={name}>{comment}</option>)}</datalist>
        <button className="secondary sm" disabled={busy || !service.trim()} onClick={() => issue('service.restart', { name: service.trim() })}>Перезапустить</button>
        <button className="secondary sm" disabled={busy || !service.trim()} onClick={() => issue('service.start', { name: service.trim() })}>Старт</button>
        <button className="secondary sm danger" disabled={busy || !service.trim()} onClick={() => confirm(`Остановить службу ${service.trim()}?`) && issue('service.stop', { name: service.trim() })}>Стоп</button>
      </div>

      <details style={{ marginTop: 10 }} open={scriptOpen} onToggle={e => setScriptOpen((e.currentTarget as HTMLDetailsElement).open)}>
        <summary>Выполнить скрипт на сервере</summary>
        <div className="row" style={{ gap: 8, margin: '8px 0', flexWrap: 'wrap' }}>
          <select style={{ flex: '0 0 150px' }} value={shell} onChange={e => setShell(e.target.value as 'powershell' | 'cmd')}><option value="powershell">PowerShell</option><option value="cmd">cmd</option></select>
          <select style={{ flex: '1 1 260px' }} value="" onChange={e => { const t = SCRIPTS.find(x => x.label === e.target.value); if (t) { setScript(t.script); setShell(t.shell) } }}>
            <option value="">— готовый скрипт —</option>
            {SCRIPTS.map(t => <option key={t.label} value={t.label}>{t.label}</option>)}
          </select>
        </div>
        <textarea className="mono" style={{ minHeight: 110 }} value={script} onChange={e => setScript(e.target.value)}
          placeholder={shell === 'powershell' ? 'Get-Service | Where-Object Status -eq Stopped | Select-Object -First 5' : 'dir C:\\'} />
        <div className="row between" style={{ marginTop: 8 }}>
          <span className="muted small">От LocalSystem, таймаут 5 мин, нужен <span className="mono">--allow exec</span>. Всё в аудите.</span>
          <button className="sm" disabled={busy || !script.trim()} onClick={() => confirm('Выполнить скрипт на сервере клиента?') && issue('exec', { script, shell, timeoutSeconds: 300 })}>Выполнить</button>
        </div>
      </details>

      {known.length > 0 && !scriptOpen && (
        <div className="row small" style={{ flexWrap: 'wrap', gap: 6, marginTop: 10 }}>
          <span className="muted">Частые службы:</span>
          {known.slice(0, 8).map(([name, comment]) => (
            <a key={name} href="#" className="mono" title={comment} onClick={e => { e.preventDefault(); setService(name) }}>{name}</a>
          ))}
        </div>
      )}
      {msg && <div className="small" style={{ marginTop: 10 }}>{msg}</div>}
    </div>
  )
}

function CommandTable({ commands }: { commands: Command[] }) {
  if (!commands.length) return <div className="muted" style={{ padding: 14 }}>Команд ещё не было.</div>
  const tone: Record<Command['status'], string> = { pending: 'unknown', sent: 'neutral', succeeded: 'ok', failed: 'critical', expired: 'warning' }
  const label: Record<Command['status'], string> = { pending: 'в очереди', sent: 'отправлена', succeeded: 'выполнена', failed: 'ошибка', expired: 'истекла' }
  return (
    <table>
      <thead><tr><th>Время</th><th>Команда</th><th>Статус</th><th>Результат</th></tr></thead>
      <tbody>
        {commands.map(c => (
          <tr key={c.id}>
            <td className="muted" title={c.issuedAt}>{timeAgo(c.issuedAt)}</td>
            <td className="mono">{c.type}</td>
            <td><span className={'badge ' + tone[c.status]}>{label[c.status]}</span></td>
            <td className="mono small" style={{ whiteSpace: 'pre-wrap' }}>{c.error ? <span style={{ color: 'var(--critical)' }}>{c.error}</span> : c.output}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function CheckEditor({ hostId, check, initialModule, onClose }: { hostId: string; check: Check | null; initialModule?: string; onClose: () => void }) {
  const initial = check
    ? { moduleId: check.moduleId, name: check.name, enabled: check.enabled, intervalSeconds: check.intervalSeconds, confirmCount: check.confirmCount, settings: check.settings, ignoredKeys: check.ignoredKeys, alertDelayMinutes: check.alertDelayMinutes }
    : newDraft(initialModule)
  return (
    <CheckForm initial={initial} isNew={!check} onCancel={onClose}
      onSave={async d => { if (check) await api.updateCheck(hostId, check.id, d); else await api.createCheck(hostId, d); onClose() }}
      onDelete={check ? async () => { if (window.confirm('Удалить проверку ' + check.name + '?')) { await api.deleteCheck(hostId, check.id); onClose() } } : undefined} />
  )
}

/** «Применить шаблон» к этому хосту или «Сохранить проверки хоста как шаблон». */
function HostTemplatePanel({ mode, hostId, hostName, tenantId, onClose }: { mode: 'apply' | 'save'; hostId: string; hostName: string; tenantId: string; onClose: () => void }) {
  const [templates, setTemplates] = useState<CheckTemplate[] | null>(null)
  const [templateId, setTemplateId] = useState('')
  const [overwrite, setOverwrite] = useState(false)
  const [name, setName] = useState(`Как ${hostName}`)
  const [scope, setScope] = useState<'tenant' | 'all'>('tenant')
  const [msg, setMsg] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    if (mode !== 'apply') return
    api.templates().then(t => { const fit = t.filter(x => !x.tenantId || x.tenantId === tenantId); setTemplates(fit); if (fit[0]) setTemplateId(fit[0].id) })
  }, [mode])

  async function apply() {
    setError(null)
    try {
      const [r] = await api.applyTemplate(templateId, [hostId], overwrite)
      setMsg(`Создано ${r.created}, обновлено ${r.updated}, пропущено ${r.skipped}${r.skipped ? ' (уже есть с таким названием — включите перезапись, чтобы обновить)' : ''}.`)
    } catch (e) { setError((e as Error).message) }
  }
  async function save() {
    setError(null)
    try { await api.createTemplateFromHost(hostId, name, scope === 'tenant' ? tenantId : undefined); setMsg('Шаблон сохранён — см. страницу «Шаблоны».') }
    catch (e) { setError((e as Error).message) }
  }

  return (
    <div className="card" style={{ marginBottom: 10, borderColor: 'var(--accent)' }}>
      {mode === 'apply' ? (
        <div className="form-row">
          <div className="field"><label>Шаблон</label>
            {templates === null ? <div className="muted small">Загрузка…</div> : templates.length === 0
              ? <div className="muted small">Шаблонов нет. Создайте на странице <Link to="/templates">«Шаблоны»</Link> или сохраните проверки этого хоста как шаблон.</div>
              : <select value={templateId} onChange={e => setTemplateId(e.target.value)}>{templates.map(t => <option key={t.id} value={t.id}>{t.name} ({t.items.length}){t.tenantId ? '' : ' · общий'}</option>)}</select>}
          </div>
          <div className="field" style={{ flex: '0 0 auto', display: 'flex', alignItems: 'flex-end' }}>
            <label className="row" style={{ marginBottom: 10 }} title="Проверки с тем же модулем и названием будут перенастроены по шаблону"><input type="checkbox" style={{ width: 'auto' }} checked={overwrite} onChange={e => setOverwrite(e.target.checked)} /> перезаписывать существующие</label>
          </div>
          <div className="field" style={{ flex: '0 0 auto', display: 'flex', alignItems: 'flex-end', gap: 8 }}>
            <button disabled={!templateId} onClick={apply}>Применить</button>
            <button className="secondary" onClick={onClose}>Закрыть</button>
          </div>
        </div>
      ) : (
        <div className="form-row">
          <div className="field"><label>Название шаблона</label><input value={name} onChange={e => setName(e.target.value)} /></div>
          <div className="field"><label>Доступен</label>
            <select value={scope} onChange={e => setScope(e.target.value as 'tenant' | 'all')}><option value="tenant">только этому клиенту</option><option value="all">всем клиентам</option></select>
          </div>
          <div className="field" style={{ flex: '0 0 auto', display: 'flex', alignItems: 'flex-end', gap: 8 }}>
            <button disabled={!name.trim()} onClick={save}>Сохранить</button>
            <button className="secondary" onClick={onClose}>Закрыть</button>
          </div>
        </div>
      )}
      {mode === 'save' && <div className="muted small">В шаблон попадут все проверки хоста ({'пароли и токены — в зашифрованном виде'}). Потом его можно отредактировать на странице «Шаблоны».</div>}
      {msg && <div className="small" style={{ marginTop: 6 }}>{msg}</div>}
      {error && <div className="error">{error}</div>}
    </div>
  )
}
