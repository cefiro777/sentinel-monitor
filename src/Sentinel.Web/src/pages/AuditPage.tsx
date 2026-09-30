import { useState } from 'react'
import { api, timeAgo } from '../api/client'
import { usePolling } from '../components/Layout'

const ACTIONS: Record<string, string> = {
  'user.login': 'Вход', 'user.login.mfa': 'Вход (2FA)', 'user.create': 'Создан пользователь', 'user.totp.enable': 'Включена 2FA', 'user.totp.disable': 'Отключена 2FA', 'user.totp.reset': 'Сброс 2FA',
  'agent.enroll': 'Регистрация агента', 'enrollment.create': 'Выдан токен', 'enrollment.revoke': 'Отозван токен',
  'tenant.create': 'Создан клиент', 'tenant.update': 'Изменён клиент', 'host.create': 'Создан хост', 'host.delete': 'Удалён хост',
  'check.create': 'Создана проверка', 'check.update': 'Изменена проверка', 'check.delete': 'Удалена проверка',
  'command.issue': 'Команда отправлена', 'command.result': 'Результат команды',
  'incident.ack': 'Инцидент взят', 'incident.resolve': 'Инцидент закрыт',
  'channel.create': 'Создан канал', 'channel.update': 'Изменён канал', 'channel.delete': 'Удалён канал', 'route.create': 'Создан маршрут', 'route.update': 'Изменён маршрут', 'route.delete': 'Удалён маршрут',
}

export function AuditPage() {
  const [filter, setFilter] = useState('')
  const { data, error } = usePolling(() => api.audit(filter || undefined), 30000, [filter])
  if (error) return <div className="error">{error}</div>
  return (
    <>
      <div className="row between">
        <h1>Аудит</h1>
        <select style={{ width: 220 }} value={filter} onChange={e => setFilter(e.target.value)}>
          <option value="">Все действия</option>
          <option value="command">Команды</option>
          <option value="user">Пользователи и входы</option>
          <option value="check">Проверки</option>
          <option value="incident">Инциденты</option>
          <option value="agent">Агенты</option>
        </select>
      </div>
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Время</th><th>Кто</th><th>Действие</th><th>Клиент</th><th>Детали</th><th>IP</th></tr></thead>
          <tbody>
            {(data ?? []).map(a => (
              <tr key={a.id}>
                <td className="muted" title={a.at} style={{ whiteSpace: 'nowrap' }}>{new Date(a.at).toLocaleString('ru')}<div className="small">{timeAgo(a.at)}</div></td>
                <td>{a.userName ?? (a.agentId ? <span className="mono small">агент {a.agentId.slice(0, 8)}</span> : '—')}</td>
                <td>{ACTIONS[a.action] ?? a.action}<div className="mono small muted">{a.action}</div></td>
                <td className="muted">{a.tenantName ?? ''}</td>
                <td className="mono small" style={{ maxWidth: 420, wordBreak: 'break-all' }}>{a.details ?? (a.targetType ? `${a.targetType} ${a.targetId?.slice(0, 8)}` : '')}</td>
                <td className="muted small">{a.remoteIp}</td>
              </tr>
            ))}
            {data && data.length === 0 && <tr><td colSpan={6} className="muted">Записей нет</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}
