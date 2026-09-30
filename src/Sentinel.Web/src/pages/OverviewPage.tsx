import { Link, useNavigate } from 'react-router-dom'
import { api, statusOrder, timeAgo, type Host, type Tenant } from '../api/client'
import { StatusBadge, StatusDot, usePolling } from '../components/Layout'

export function OverviewPage() {
  const nav = useNavigate()
  const { data, error } = usePolling(async () => {
    const [tenants, hosts, incidents] = await Promise.all([api.tenants(), api.hosts(), api.incidentSummary()])
    return { tenants, hosts, incidents }
  }, 15000)

  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>

  const { tenants, hosts, incidents } = data
  const problems = hosts.filter(h => h.worstStatus !== 'ok' || (h.agent && !h.agent.isOnline))
  const byTenant = new Map<string, Host[]>()
  for (const h of hosts) byTenant.set(h.tenantId, [...(byTenant.get(h.tenantId) ?? []), h])

  return (
    <>
      <h1>Обзор</h1>
      <div className="grid">
        <Stat label="Клиентов" value={tenants.length} />
        <Stat label="Хостов" value={hosts.length} />
        <Stat label="Агентов онлайн" value={`${hosts.filter(h => h.agent?.isOnline).length} / ${hosts.filter(h => h.agent).length}`} />
        <Stat label="С проблемами" value={problems.length} tone={problems.length ? 'critical' : 'ok'} />
        <Link to="/incidents" style={{ textDecoration: 'none' }}><Stat label="Открытых инцидентов" value={incidents.open + incidents.acknowledged} tone={incidents.open ? 'critical' : 'ok'} /></Link>
      </div>

      {problems.length > 0 && (
        <>
          <h2>Требуют внимания</h2>
          <div className="card" style={{ padding: 0 }}>
            <HostTable hosts={problems.sort((a, b) => statusOrder[b.worstStatus] - statusOrder[a.worstStatus])} onOpen={id => nav(`/hosts/${id}`)} showTenant />
          </div>
        </>
      )}

      {tenants.sort((a, b) => statusOrder[b.worstStatus] - statusOrder[a.worstStatus] || a.name.localeCompare(b.name)).map(t => (
        <TenantBlock key={t.id} tenant={t} hosts={byTenant.get(t.id) ?? []} onOpen={id => nav(`/hosts/${id}`)} />
      ))}
      {tenants.length === 0 && <div className="notice">Клиентов пока нет. Добавьте первого на странице «Клиенты».</div>}
    </>
  )
}

function Stat({ label, value, tone }: { label: string; value: number | string; tone?: 'ok' | 'critical' }) {
  return (
    <div className="card">
      <div className="muted small">{label}</div>
      <div style={{ fontSize: 26, fontWeight: 600, color: tone === 'critical' ? 'var(--critical)' : tone === 'ok' ? 'var(--ok)' : undefined }}>{value}</div>
    </div>
  )
}

function TenantBlock({ tenant, hosts, onOpen }: { tenant: Tenant; hosts: Host[]; onOpen: (id: string) => void }) {
  return (
    <>
      <h2 className="row"><StatusDot status={tenant.worstStatus} /> {tenant.name} <span className="muted" style={{ fontWeight: 400, textTransform: 'none' }}>— {hosts.length} хост., онлайн {tenant.agentsOnline}</span></h2>
      <div className="card" style={{ padding: 0 }}>
        {hosts.length ? <HostTable hosts={hosts} onOpen={onOpen} /> : <div className="muted" style={{ padding: 14 }}>Хостов нет — создайте токен на странице «Установка агентов».</div>}
      </div>
    </>
  )
}

export function HostTable({ hosts, onOpen, showTenant }: { hosts: Host[]; onOpen: (id: string) => void; showTenant?: boolean }) {
  return (
    <table>
      <thead>
        <tr>
          <th style={{ width: 24 }}></th>
          <th>Хост</th>
          {showTenant && <th>Клиент</th>}
          <th>Агент</th>
          <th>Проверки</th>
          <th>Последняя связь</th>
        </tr>
      </thead>
      <tbody>
        {hosts.map(h => (
          <tr key={h.id} className="clickable" onClick={() => onOpen(h.id)}>
            <td><StatusDot status={h.agent && !h.agent.isOnline ? 'critical' : h.worstStatus} /></td>
            <td>{h.name}{h.address && <span className="muted small"> · {h.address}</span>}</td>
            {showTenant && <td>{h.tenantName}</td>}
            <td>{h.agent ? <span className={'badge ' + (h.agent.isOnline ? 'online' : 'offline')}>{h.agent.isOnline ? 'онлайн' : 'офлайн'}</span> : <span className="muted">нет</span>}</td>
            <td>
              {h.checksProblem > 0 ? <StatusBadge status={h.worstStatus} /> : <span className="muted">{h.checksTotal} ок</span>}
              {h.checksProblem > 0 && <span className="muted small"> · {[h.checksCritical && `${h.checksCritical} критич.`, h.checksWarning && `${h.checksWarning} внимание`, h.checksProblem - h.checksCritical - h.checksWarning > 0 && `${h.checksProblem - h.checksCritical - h.checksWarning} неизв.`].filter(Boolean).join(', ')}</span>}
              {h.checksInProgress > 0 && <span className="badge neutral" style={{ marginLeft: 6 }} title="Инциденты, взятые в работу">{h.checksInProgress} в работе</span>}
            </td>
            <td className="muted">{timeAgo(h.agent?.lastSeenAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
