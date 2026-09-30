import { useEffect, useState } from 'react'
import { api, timeAgo, type Channel, type ChannelType, type CheckStatus, type Route, type Tenant } from '../api/client'
import { usePolling } from '../components/Layout'

const TYPES: Record<ChannelType, string> = { email: 'Email (SMTP)', telegram: 'Telegram', max: 'Max' }
const SEVERITY: Record<CheckStatus, string> = { ok: 'OK', warning: 'Внимание и выше', critical: 'Только критичные', unknown: 'Неизвестно' }

export function NotificationsPage() {
  const { data, refresh, error } = usePolling(async () => {
    const [channels, routes, log, tenants] = await Promise.all([api.channels(), api.routes(), api.notificationLog(), api.tenants()])
    return { channels, routes, log, tenants }
  }, 30000)
  const [editChannel, setEditChannel] = useState<Channel | 'new' | null>(null)
  const [editRoute, setEditRoute] = useState<Route | 'new' | null>(null)
  const [msg, setMsg] = useState<string | null>(null)

  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>

  async function test(c: Channel) {
    setMsg(`Отправка в «${c.name}»…`)
    try { await api.testChannel(c.id); setMsg(`«${c.name}»: тестовое сообщение доставлено`) }
    catch (e) { setMsg(`«${c.name}»: ${(e as Error).message}`) }
    refresh()
  }

  return (
    <>
      <h1>Оповещения</h1>
      <div className="row between"><h2 style={{ marginTop: 0 }}>Каналы</h2><button className="sm" onClick={() => setEditChannel('new')}>+ Канал</button></div>
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Название</th><th>Тип</th><th>Состояние</th><th>Последняя отправка</th><th></th></tr></thead>
          <tbody>
            {data.channels.map(c => (
              <tr key={c.id}>
                <td>{c.name}</td><td>{TYPES[c.type]}</td>
                <td>{!c.isEnabled ? <span className="badge unknown">выключен</span> : c.lastError ? <span className="badge critical" title={c.lastError}>ошибка</span> : <span className="badge ok">ок</span>}
                  {c.lastError && <div className="muted small" style={{ maxWidth: 360 }}>{c.lastError}</div>}</td>
                <td className="muted">{timeAgo(c.lastUsedAt)}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="secondary sm" onClick={() => test(c)}>Тест</button>
                  <button className="secondary sm" style={{ marginLeft: 6 }} onClick={() => setEditChannel(c)}>Изменить</button>
                </td>
              </tr>
            ))}
            {data.channels.length === 0 && <tr><td colSpan={5} className="muted">Каналов нет — добавьте Email или Telegram</td></tr>}
          </tbody>
        </table>
      </div>
      {msg && <div className="notice">{msg}</div>}
      {editChannel && <ChannelEditor channel={editChannel === 'new' ? null : editChannel} onClose={() => { setEditChannel(null); refresh() }} />}

      <div className="row between"><h2>Маршруты</h2><button className="sm" disabled={!data.channels.length} onClick={() => setEditRoute('new')}>+ Маршрут</button></div>
      <p className="muted small">Маршрут определяет, инциденты какого клиента и какой серьёзности идут в канал. Без маршрутов уведомления не отправляются.</p>
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Клиент</th><th>Канал</th><th>Серьёзность</th><th>О восстановлении</th><th>Тихие часы</th><th>Напоминать</th><th></th></tr></thead>
          <tbody>
            {data.routes.map(r => (
              <tr key={r.id} style={{ opacity: r.isEnabled ? 1 : .5 }}>
                <td>{r.tenantName ?? <em>все клиенты</em>}</td><td>{r.channelName}</td><td>{SEVERITY[r.minSeverity]}</td>
                <td>{r.notifyOnResolve ? 'да' : 'нет'}</td>
                <td>{r.quietFromHour != null && r.quietToHour != null ? `${r.quietFromHour}:00–${r.quietToHour}:00` : '—'}</td>
                <td>{r.remindMinutes > 0 ? `каждые ${r.remindMinutes} мин` : '—'}</td>
                <td><button className="secondary sm" onClick={() => setEditRoute(r)}>Изменить</button></td>
              </tr>
            ))}
            {data.routes.length === 0 && <tr><td colSpan={7} className="muted">Маршрутов нет</td></tr>}
          </tbody>
        </table>
      </div>
      {editRoute && <RouteEditor route={editRoute === 'new' ? null : editRoute} channels={data.channels} tenants={data.tenants} onClose={() => { setEditRoute(null); refresh() }} />}

      <h2>Журнал отправки</h2>
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Время</th><th>Инцидент</th><th>Канал</th><th>Тип</th><th>Результат</th></tr></thead>
          <tbody>
            {data.log.slice(0, 50).map(l => (
              <tr key={l.id}>
                <td className="muted" title={l.at}>{timeAgo(l.at)}</td><td>{l.incidentTitle}</td><td>{l.channelName}</td><td className="mono small">{l.kind}</td>
                <td>{l.success ? <span className="badge ok">доставлено</span> : <span className="badge critical" title={l.error}>ошибка</span>}</td>
              </tr>
            ))}
            {data.log.length === 0 && <tr><td colSpan={5} className="muted">Отправок ещё не было</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}

function ChannelEditor({ channel, onClose }: { channel: Channel | null; onClose: () => void }) {
  const [name, setName] = useState(channel?.name ?? '')
  const [type, setType] = useState<ChannelType>(channel?.type ?? 'telegram')
  const [enabled, setEnabled] = useState(channel?.isEnabled ?? true)
  const [s, setS] = useState<Record<string, unknown>>(channel?.settings ?? {})
  const [err, setErr] = useState<string | null>(null)
  const set = (k: string, v: unknown) => setS({ ...s, [k]: v })
  const str = (k: string) => String(s[k] ?? '')
  const list = (k: string) => Array.isArray(s[k]) ? (s[k] as string[]).join(', ') : ''
  const setList = (k: string, v: string) => set(k, v.split(',').map(x => x.trim()).filter(Boolean))

  useEffect(() => { if (!channel) setS(type === 'email' ? { port: 587, security: 'starttls' } : {}) }, [type, channel])

  async function save() {
    setErr(null)
    try {
      const body = { name, type, isEnabled: enabled, settings: s }
      if (channel) await api.updateChannel(channel.id, body); else await api.createChannel(body)
      onClose()
    } catch (e) { setErr((e as Error).message) }
  }

  return (
    <div className="card" style={{ marginTop: 10, borderColor: 'var(--accent)' }}>
      <h2 style={{ marginTop: 0 }}>{channel ? 'Канал' : 'Новый канал'}</h2>
      <div className="form-row">
        <div className="field"><label>Название</label><input value={name} onChange={e => setName(e.target.value)} placeholder="Дежурный чат" /></div>
        <div className="field"><label>Тип</label>
          <select value={type} onChange={e => setType(e.target.value as ChannelType)} disabled={!!channel}>
            <option value="telegram">Telegram</option><option value="max">Max</option><option value="email">Email (SMTP)</option>
          </select>
        </div>
      </div>
      {type === 'telegram' && (
        <>
          <div className="form-row">
            <div className="field"><label>Токен бота (от @BotFather)</label><input value={str('botToken')} onChange={e => set('botToken', e.target.value)} /></div>
            <div className="field"><label>ID чатов через запятую</label><input value={list('chatIds')} onChange={e => setList('chatIds', e.target.value)} placeholder="-1001234567890, 123456789" /></div>
          </div>
          <div className="muted small">Добавьте бота в группу, напишите ему что-нибудь, затем откройте https://api.telegram.org/bot&lt;токен&gt;/getUpdates — там будет chat.id.</div>
        </>
      )}
      {type === 'max' && (
        <>
          <div className="form-row">
            <div className="field"><label>Токен бота (от @MasterBot в Max)</label><input value={str('token')} onChange={e => set('token', e.target.value)} /></div>
            <div className="field"><label>ID чатов через запятую</label><input value={list('chatIds')} onChange={e => setList('chatIds', e.target.value)} /></div>
          </div>
          <div className="muted small">Добавьте бота в чат, затем откройте https://botapi.max.ru/chats с заголовком Authorization: &lt;токен&gt; — там chat_id.</div>
        </>
      )}
      {type === 'email' && (
        <>
          <div className="form-row">
            <div className="field"><label>SMTP-сервер</label><input value={str('host')} onChange={e => set('host', e.target.value)} placeholder="smtp.yandex.ru" /></div>
            <div className="field" style={{ flex: '0 0 100px' }}><label>Порт</label><input type="number" value={str('port')} onChange={e => set('port', +e.target.value)} /></div>
            <div className="field" style={{ flex: '0 0 140px' }}><label>Шифрование</label>
              <select value={str('security') || 'starttls'} onChange={e => set('security', e.target.value)}><option value="starttls">STARTTLS</option><option value="ssl">SSL</option><option value="none">нет</option></select>
            </div>
          </div>
          <div className="form-row">
            <div className="field"><label>Логин</label><input value={str('username')} onChange={e => set('username', e.target.value)} /></div>
            <div className="field"><label>Пароль</label><input type="password" value={str('password')} onChange={e => set('password', e.target.value)} /></div>
          </div>
          <div className="form-row">
            <div className="field"><label>От кого</label><input value={str('from')} onChange={e => set('from', e.target.value)} placeholder="monitor@company.ru" /></div>
            <div className="field"><label>Кому (через запятую)</label><input value={list('to')} onChange={e => setList('to', e.target.value)} /></div>
          </div>
        </>
      )}
      <div className="row between">
        <label className="row" style={{ marginBottom: 0 }}><input type="checkbox" style={{ width: 'auto' }} checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Включён</label>
        <div className="row">
          {channel && <button className="secondary sm danger" onClick={() => confirm('Удалить канал и его маршруты?') && api.deleteChannel(channel.id).then(onClose)}>Удалить</button>}
          <button className="secondary sm" onClick={onClose}>Отмена</button>
          <button className="sm" disabled={!name} onClick={save}>Сохранить</button>
        </div>
      </div>
      {err && <div className="error">{err}</div>}
    </div>
  )
}

function RouteEditor({ route, channels, tenants, onClose }: { route: Route | null; channels: Channel[]; tenants: Tenant[]; onClose: () => void }) {
  const [r, setR] = useState({
    channelId: route?.channelId ?? channels[0]?.id ?? '', tenantId: route?.tenantId ?? '', minSeverity: route?.minSeverity ?? 'warning' as CheckStatus,
    notifyOnResolve: route?.notifyOnResolve ?? true, quietFromHour: route?.quietFromHour ?? '', quietToHour: route?.quietToHour ?? '', remindMinutes: route?.remindMinutes ?? 0, isEnabled: route?.isEnabled ?? true,
  })
  const [err, setErr] = useState<string | null>(null)

  async function save() {
    setErr(null)
    const body = {
      channelId: r.channelId, tenantId: r.tenantId || undefined, minSeverity: r.minSeverity, notifyOnResolve: r.notifyOnResolve, isEnabled: r.isEnabled, remindMinutes: r.remindMinutes,
      quietFromHour: r.quietFromHour === '' ? undefined : +r.quietFromHour, quietToHour: r.quietToHour === '' ? undefined : +r.quietToHour,
    }
    try { if (route) await api.updateRoute(route.id, body); else await api.createRoute(body); onClose() }
    catch (e) { setErr((e as Error).message) }
  }

  return (
    <div className="card" style={{ marginTop: 10, borderColor: 'var(--accent)' }}>
      <h2 style={{ marginTop: 0 }}>{route ? 'Маршрут' : 'Новый маршрут'}</h2>
      <div className="form-row">
        <div className="field"><label>Клиент</label>
          <select value={r.tenantId} onChange={e => setR({ ...r, tenantId: e.target.value })}><option value="">Все клиенты</option>{tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
        </div>
        <div className="field"><label>Канал</label>
          <select value={r.channelId} onChange={e => setR({ ...r, channelId: e.target.value })}>{channels.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
        </div>
        <div className="field"><label>Серьёзность</label>
          <select value={r.minSeverity} onChange={e => setR({ ...r, minSeverity: e.target.value as CheckStatus })}><option value="warning">Внимание и выше</option><option value="critical">Только критичные</option></select>
        </div>
      </div>
      <div className="form-row">
        <div className="field"><label>Тихие часы с (0–23, пусто — нет)</label><input type="number" min={0} max={23} value={r.quietFromHour} onChange={e => setR({ ...r, quietFromHour: e.target.value })} /></div>
        <div className="field"><label>по</label><input type="number" min={0} max={23} value={r.quietToHour} onChange={e => setR({ ...r, quietToHour: e.target.value })} /></div>
        <div className="field"><label title="Повторять, пока инцидент не взят в работу">Напоминать каждые, мин (0 — нет)</label><input type="number" min={0} value={r.remindMinutes} onChange={e => setR({ ...r, remindMinutes: +e.target.value })} /></div>
        <div className="field" style={{ display: 'flex', alignItems: 'flex-end', gap: 16 }}>
          <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={r.notifyOnResolve} onChange={e => setR({ ...r, notifyOnResolve: e.target.checked })} /> Сообщать о восстановлении</label>
          <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={r.isEnabled} onChange={e => setR({ ...r, isEnabled: e.target.checked })} /> Включён</label>
        </div>
      </div>
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        {route && <button className="secondary sm danger" onClick={() => api.deleteRoute(route.id).then(onClose)}>Удалить</button>}
        <button className="secondary sm" onClick={onClose}>Отмена</button>
        <button className="sm" disabled={!r.channelId} onClick={save}>Сохранить</button>
      </div>
      {err && <div className="error">{err}</div>}
    </div>
  )
}
