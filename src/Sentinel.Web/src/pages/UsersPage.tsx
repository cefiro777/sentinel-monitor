import { useEffect, useState } from 'react'
import { api, type Tenant, type UserRole } from '../api/client'
import { usePolling } from '../components/Layout'

const ROLES: Record<UserRole, string> = { admin: 'Администратор', engineer: 'Инженер', readOnly: 'Только просмотр', clientViewer: 'Сотрудник клиента' }

export function UsersPage() {
  const { data, refresh, error } = usePolling(() => api.users(), 60000)
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [form, setForm] = useState({ email: '', displayName: '', password: '', role: 'engineer' as UserRole, tenantId: '' })
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => { api.tenants().then(setTenants) }, [])

  async function create() {
    setErr(null)
    try {
      await api.createUser({ ...form, tenantId: form.role === 'clientViewer' ? form.tenantId : undefined })
      setForm({ email: '', displayName: '', password: '', role: 'engineer', tenantId: '' }); refresh()
    } catch (e) { setErr((e as Error).message) }
  }

  return (
    <>
      <h1>Пользователи</h1>
      {error && <div className="error">{error}</div>}
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Email</th><th>Имя</th><th>Роль</th><th>Клиент</th><th>2FA</th><th>Статус</th></tr></thead>
          <tbody>
            {(data ?? []).map(u => (
              <tr key={u.id}>
                <td>{u.email}</td><td>{u.displayName}</td><td>{ROLES[u.role]}</td>
                <td className="muted">{u.tenantId ? tenants.find(t => t.id === u.tenantId)?.name ?? u.tenantId : '—'}</td>
                <td>{u.totpEnabled ? <span className="row"><span className="badge ok">да</span><button className="secondary sm" title="Сбросить, если потерян телефон" onClick={() => confirm(`Сбросить 2FA для ${u.email}?`) && api.totpReset(u.id).then(refresh)}>сброс</button></span> : <span className="badge warning">нет</span>}</td>
                <td>{u.isActive ? <span className="badge ok">активен</span> : <span className="badge unknown">отключён</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="card" style={{ marginTop: 16, maxWidth: 720 }}>
        <h2 style={{ marginTop: 0 }}>Новый пользователь</h2>
        <div className="form-row">
          <div className="field"><label>Email</label><input type="email" value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} /></div>
          <div className="field"><label>Имя</label><input value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} /></div>
        </div>
        <div className="form-row">
          <div className="field"><label>Пароль (не короче 10 символов)</label><input type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} /></div>
          <div className="field"><label>Роль</label>
            <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value as UserRole })}>
              {Object.entries(ROLES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
          </div>
          {form.role === 'clientViewer' && (
            <div className="field"><label>Клиент</label>
              <select value={form.tenantId} onChange={e => setForm({ ...form, tenantId: e.target.value })}>
                <option value="">— выберите —</option>{tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
            </div>
          )}
        </div>
        <button disabled={!form.email || !form.displayName || form.password.length < 10} onClick={create}>Создать</button>
        {err && <div className="error">{err}</div>}
      </div>
    </>
  )
}
