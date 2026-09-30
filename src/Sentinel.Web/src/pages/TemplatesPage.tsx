import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, auth, timeAgo, type ApplyResult, type CheckTemplate, type Host, type Tenant, type TemplateItem } from '../api/client'
import { usePolling } from '../components/Layout'
import { MODULES } from '../components/ModuleForms'
import { CheckForm, newDraft, type CheckDraft } from '../components/CheckForm'

/** Шаблоны наборов проверок: список, редактор состава, применение к нескольким хостам разом. */
export function TemplatesPage() {
  const { data, error, refresh } = usePolling(async () => {
    const [templates, tenants] = await Promise.all([api.templates(), api.tenants()])
    return { templates, tenants }
  }, 30000, [])
  const [editing, setEditing] = useState<CheckTemplate | 'new' | null>(null)
  const [applying, setApplying] = useState<CheckTemplate | null>(null)
  const canOperate = auth.canOperate()

  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>
  const { templates, tenants } = data

  return (
    <>
      <div className="row between">
        <h1>Шаблоны проверок</h1>
        {canOperate && <button className="sm" onClick={() => { setApplying(null); setEditing('new') }}>+ Новый шаблон</button>}
      </div>
      <div className="muted small" style={{ marginBottom: 12 }}>
        Набор проверок, который применяется к нескольким серверам разом. Проверка на хосте считается той же, если совпали модуль и название — такие либо пропускаются,
        либо перенастраиваются по шаблону; остальные проверки хоста не трогаются. Готовый набор с настроенного сервера можно снять кнопкой «Сохранить как шаблон» на странице хоста.
      </div>

      {templates.length === 0 && <div className="muted">Шаблонов ещё нет.</div>}
      {templates.map(t => (
        <div key={t.id} className="card" style={{ marginBottom: 10 }}>
          <div className="row between">
            <div>
              <strong>{t.name}</strong> <span className="muted small">· {t.tenantId ? t.tenantName : 'все клиенты'} · {t.items.length} {plural(t.items.length, 'проверка', 'проверки', 'проверок')} · изменён {timeAgo(t.updatedAt)}</span>
              {t.description && <div className="small muted">{t.description}</div>}
              <div className="small" style={{ marginTop: 4 }}>{t.items.map(i => <span key={i.moduleId + i.name} className="badge neutral" style={{ marginRight: 4 }} title={MODULES[i.moduleId]?.title ?? i.moduleId}>{i.name}{i.enabled ? '' : ' (выкл.)'}</span>)}</div>
            </div>
            {canOperate && (
              <div className="row">
                <button className="sm" onClick={() => { setEditing(null); setApplying(t) }}>Применить…</button>
                <button className="secondary sm" onClick={() => { setApplying(null); setEditing(t) }}>Изменить</button>
              </div>
            )}
          </div>
          {applying?.id === t.id && <ApplyPanel template={t} tenants={tenants} onClose={() => setApplying(null)} />}
        </div>
      ))}

      {editing && <TemplateEditor template={editing === 'new' ? null : editing} tenants={tenants} onClose={() => { setEditing(null); refresh() }} />}
    </>
  )
}

function plural(n: number, one: string, few: string, many: string) {
  const m10 = n % 10, m100 = n % 100
  return m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? few : many
}

function TemplateEditor({ template, tenants, onClose }: { template: CheckTemplate | null; tenants: Tenant[]; onClose: () => void }) {
  const [name, setName] = useState(template?.name ?? '')
  const [description, setDescription] = useState(template?.description ?? '')
  const [tenantId, setTenantId] = useState(template?.tenantId ?? '')
  const [items, setItems] = useState<TemplateItem[]>(template?.items ?? [])
  const [itemEdit, setItemEdit] = useState<number | 'new' | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function save() {
    setError(null)
    const body = { tenantId: tenantId || undefined, name, description, items }
    try { if (template) await api.updateTemplate(template.id, body); else await api.createTemplate(body); onClose() }
    catch (e) { setError((e as Error).message) }
  }
  async function remove() {
    if (!template || !confirm(`Удалить шаблон «${template.name}»? Проверки на хостах останутся.`)) return
    await api.deleteTemplate(template.id); onClose()
  }
  function saveItem(d: CheckDraft) {
    const dup = items.findIndex((x, n) => n !== itemEdit && x.moduleId === d.moduleId && x.name.toLowerCase() === d.name.toLowerCase())
    if (dup >= 0) throw new Error('В шаблоне уже есть проверка этого модуля с таким названием.')
    setItems(itemEdit === 'new' ? [...items, d] : items.map((x, n) => (n === itemEdit ? d : x)))
    setItemEdit(null)
  }

  return (
    <div className="card" style={{ marginTop: 10, borderColor: 'var(--accent)' }}>
      <h2 style={{ marginTop: 0 }}>{template ? 'Шаблон' : 'Новый шаблон'}</h2>
      <div className="form-row">
        <div className="field"><label>Название</label><input value={name} onChange={e => setName(e.target.value)} placeholder="Сервер 1С: базовый набор" /></div>
        <div className="field"><label>Доступен</label>
          <select value={tenantId} onChange={e => setTenantId(e.target.value)}>
            <option value="">всем клиентам</option>
            {tenants.map(t => <option key={t.id} value={t.id}>только {t.name}</option>)}
          </select>
        </div>
      </div>
      <div className="field"><label>Описание</label><input value={description} onChange={e => setDescription(e.target.value)} /></div>

      <div className="row between"><strong>Состав</strong><button className="secondary sm" onClick={() => setItemEdit('new')}>+ Проверка</button></div>
      {items.length === 0 && <div className="muted small" style={{ margin: '6px 0' }}>Пока пусто. Добавьте проверки — те же формы, что на странице хоста.</div>}
      {items.length > 0 && (
        <table style={{ marginTop: 6 }}>
          <thead><tr><th>Название</th><th>Модуль</th><th style={{ width: 110 }}>Интервал</th><th style={{ width: 90 }}></th></tr></thead>
          <tbody>
            {items.map((i, n) => (
              <tr key={n}>
                <td>{i.name}{!i.enabled && <span className="muted small"> · выключена</span>}</td>
                <td className="muted">{MODULES[i.moduleId]?.title ?? i.moduleId}</td>
                <td className="muted">{i.intervalSeconds} с</td>
                <td style={{ textAlign: 'right' }}><a href="#" className="small" onClick={e => { e.preventDefault(); setItemEdit(n) }}>изменить</a></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {itemEdit !== null && (
        <CheckForm key={String(itemEdit)} isNew={itemEdit === 'new'} title={itemEdit === 'new' ? 'Проверка в шаблоне' : 'Проверка в шаблоне: ' + items[itemEdit].name}
          initial={itemEdit === 'new' ? newDraft() : items[itemEdit]}
          onSave={saveItem} onCancel={() => setItemEdit(null)}
          onDelete={itemEdit === 'new' ? undefined : () => { setItems(items.filter((_, n) => n !== itemEdit)); setItemEdit(null) }} />
      )}

      <div className="row between" style={{ marginTop: 12 }}>
        <div>{template && <button className="secondary sm danger" onClick={remove}>Удалить шаблон</button>}</div>
        <div className="row">
          <button className="secondary sm" onClick={onClose}>Отмена</button>
          <button className="sm" disabled={!name.trim() || items.length === 0} onClick={save}>Сохранить шаблон</button>
        </div>
      </div>
      {error && <div className="error">{error}</div>}
    </div>
  )
}

/** Выбор клиента и хостов галочками, режим перезаписи, результат по каждому хосту. */
function ApplyPanel({ template, tenants, onClose }: { template: CheckTemplate; tenants: Tenant[]; onClose: () => void }) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [tenantId, setTenantId] = useState(template.tenantId ?? tenants[0]?.id ?? '')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [overwrite, setOverwrite] = useState(false)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<ApplyResult[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { api.hosts().then(setHosts) }, [])
  const visible = useMemo(() => (hosts ?? []).filter(h => h.tenantId === tenantId), [hosts, tenantId])
  const withAgent = visible.filter(h => h.agent)
  const allSelected = withAgent.length > 0 && withAgent.every(h => selected.has(h.id))

  function toggle(id: string) { const s = new Set(selected); if (s.has(id)) s.delete(id); else s.add(id); setSelected(s) }
  function toggleAll() { setSelected(allSelected ? new Set() : new Set(withAgent.map(h => h.id))) }

  async function apply() {
    setBusy(true); setError(null); setResult(null)
    try { setResult(await api.applyTemplate(template.id, [...selected], overwrite)) }
    catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }

  return (
    <div style={{ marginTop: 10, borderTop: '1px solid var(--border)', paddingTop: 10 }}>
      <div className="form-row">
        <div className="field"><label>Клиент</label>
          <select value={tenantId} disabled={!!template.tenantId} onChange={e => { setTenantId(e.target.value); setSelected(new Set()); setResult(null) }}>
            {tenants.filter(t => !template.tenantId || t.id === template.tenantId).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </div>
        <div className="field" style={{ flex: '0 0 auto', display: 'flex', alignItems: 'flex-end' }}>
          <label className="row" style={{ marginBottom: 10 }} title="Проверки с тем же модулем и названием будут перенастроены по шаблону (их состояние и инциденты сбросятся)"><input type="checkbox" style={{ width: 'auto' }} checked={overwrite} onChange={e => setOverwrite(e.target.checked)} /> перезаписывать существующие</label>
        </div>
      </div>
      {hosts === null ? <div className="muted small">Загрузка…</div> : visible.length === 0 ? <div className="muted small">У клиента нет хостов.</div> : (
        <table>
          <thead><tr><th style={{ width: 30 }}><input type="checkbox" style={{ width: 'auto' }} checked={allSelected} onChange={toggleAll} /></th><th>Хост</th><th>Агент</th><th>Проверок</th></tr></thead>
          <tbody>
            {visible.map(h => (
              <tr key={h.id}>
                <td><input type="checkbox" style={{ width: 'auto' }} disabled={!h.agent} checked={selected.has(h.id)} onChange={() => toggle(h.id)} /></td>
                <td><Link to={`/hosts/${h.id}`}>{h.name}</Link></td>
                <td>{h.agent ? <span className={'badge ' + (h.agent.isOnline ? 'online' : 'offline')}>{h.agent.isOnline ? 'онлайн' : 'офлайн'}</span> : <span className="muted small">нет агента</span>}</td>
                <td className="muted">{h.checksTotal}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="row" style={{ marginTop: 10 }}>
        <button disabled={busy || selected.size === 0} onClick={apply}>Применить к {selected.size} {plural(selected.size, 'хосту', 'хостам', 'хостам')}</button>
        <button className="secondary" onClick={onClose}>Закрыть</button>
        <span className="muted small">Агенты офлайн получат новые проверки при выходе на связь.</span>
      </div>
      {result && (
        <table style={{ marginTop: 10 }}>
          <thead><tr><th>Хост</th><th>Создано</th><th>Обновлено</th><th>Пропущено</th></tr></thead>
          <tbody>{result.map(r => <tr key={r.hostId}><td>{r.hostName}</td><td>{r.created}</td><td>{r.updated}</td><td title="Уже есть проверка с таким модулем и названием">{r.skipped}</td></tr>)}</tbody>
        </table>
      )}
      {error && <div className="error">{error}</div>}
    </div>
  )
}
