import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, auth, type Tenant } from '../api/client'
import { StatusDot, usePolling } from '../components/Layout'

export function TenantsPage() {
  const nav = useNavigate()
  const { data, error, refresh } = usePolling(() => api.tenants(), 30000)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [notes, setNotes] = useState('')
  const [err, setErr] = useState<string | null>(null)
  const [toDelete, setToDelete] = useState<Tenant | null>(null)

  async function create() {
    setErr(null)
    try { await api.createTenant(name, slug, notes || undefined); setName(''); setSlug(''); setNotes(''); refresh() }
    catch (e) { setErr((e as Error).message) }
  }

  return (
    <>
      <h1>Клиенты</h1>
      {error && <div className="error">{error}</div>}
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th style={{ width: 24 }}></th><th>Название</th><th>Код</th><th>Хостов</th><th>Агентов онлайн</th><th>Примечание</th>{auth.isAdmin() && <th style={{ width: 90 }}></th>}</tr></thead>
          <tbody>
            {(data ?? []).map(t => (
              <tr key={t.id} className="clickable" onClick={() => nav('/?tenant=' + t.id)}>
                <td><StatusDot status={t.worstStatus} /></td>
                <td>{t.name}</td>
                <td className="mono">{t.slug}</td>
                <td>{t.hostCount}</td>
                <td>{t.agentsOnline}</td>
                <td className="muted">{t.notes}</td>
                {auth.isAdmin() && <td onClick={e => e.stopPropagation()}><button className="secondary sm danger" onClick={() => setToDelete(t)}>Удалить</button></td>}
              </tr>
            ))}
            {data && data.length === 0 && <tr><td colSpan={7} className="muted">Клиентов пока нет</td></tr>}
          </tbody>
        </table>
      </div>

      {toDelete && <DeleteTenantPanel tenant={toDelete} onClose={() => { setToDelete(null); refresh() }} />}

      {auth.canOperate() && (
        <div className="card" style={{ marginTop: 16, maxWidth: 640 }}>
          <h2 style={{ marginTop: 0 }}>Новый клиент</h2>
          <div className="form-row">
            <div className="field"><label>Название</label><input value={name} onChange={e => setName(e.target.value)} placeholder="ООО Ромашка" /></div>
            <div className="field"><label>Код (латиницей, для папок бэкапов)</label><input value={slug} onChange={e => setSlug(e.target.value)} placeholder="romashka" /></div>
          </div>
          <div className="field"><label>Примечание</label><input value={notes} onChange={e => setNotes(e.target.value)} placeholder="контакты, особенности" /></div>
          <button disabled={!name} onClick={create}>Создать</button>
          {err && <div className="error">{err}</div>}
        </div>
      )}
    </>
  )
}

/** Удаление клиента: вводится точное имя — вместе с клиентом уходят хосты, проверки, история и инциденты. */
function DeleteTenantPanel({ tenant, onClose }: { tenant: Tenant; onClose: () => void }) {
  const [confirmText, setConfirmText] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  async function remove() {
    setBusy(true); setErr(null)
    try { const r = await api.deleteTenant(tenant.id, confirmText); alert(`Клиент «${tenant.name}» удалён (хостов: ${r.deletedHosts}).`); onClose() }
    catch (e) { setErr((e as Error).message) }
    finally { setBusy(false) }
  }
  return (
    <div className="card" style={{ marginTop: 16, maxWidth: 640, borderColor: 'var(--critical)' }}>
      <h2 style={{ marginTop: 0 }}>Удалить клиента «{tenant.name}»?</h2>
      <div className="notice">
        Будут безвозвратно удалены: {tenant.hostCount} хост(ов) с их агентами, все проверки, история результатов, бэкап-задания и инциденты этого клиента.
        Агенты на серверах клиента продолжат работать вхолостую — удалите их вручную (<span className="mono">SentinelAgent.exe uninstall</span>).
      </div>
      <div className="field"><label>Для подтверждения введите название клиента точно: <strong>{tenant.name}</strong></label>
        <input value={confirmText} onChange={e => setConfirmText(e.target.value)} placeholder={tenant.name} />
      </div>
      <div className="row">
        <button className="secondary" onClick={onClose}>Отмена</button>
        <button className="danger" disabled={busy || confirmText.trim() !== tenant.name} onClick={remove}>Удалить безвозвратно</button>
      </div>
      {err && <div className="error">{err}</div>}
    </div>
  )
}
