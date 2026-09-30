import { useState } from 'react'
import { MODULES, MODULE_GROUPS, type Settings } from './ModuleForms'

/** Параметры проверки без привязки к хосту — то, что редактируется и на странице хоста, и в шаблоне. */
export interface CheckDraft { moduleId: string; name: string; enabled: boolean; intervalSeconds: number; confirmCount: number; settings: Settings; ignoredKeys?: string[]; alertDelayMinutes?: number }

export function newDraft(moduleId = 'system'): CheckDraft {
  const d = MODULES[moduleId] ?? MODULES.system
  const id = MODULES[moduleId] ? moduleId : 'system'
  return { moduleId: id, name: d.title, enabled: true, intervalSeconds: d.interval, confirmCount: 2, settings: { ...d.defaults }, alertDelayMinutes: defaultAlertDelay(id) }
}

/** Пики CPU/памяти кратковременны — у «Системы» по умолчанию оповещаем, только если держится 30 мин (как на сервере). */
export function defaultAlertDelay(moduleId: string) {
  return moduleId === 'system' ? 30 : 0
}

/**
 * Редактор одной проверки: выбор модуля, общие поля, типовая форма модуля (или JSON для неизвестных).
 * Сохранение делает вызывающая сторона — на хосте это API, в шаблоне — локальный список.
 */
export function CheckForm({ initial, isNew, title, moduleIds, onSave, onCancel, onDelete }: {
  initial: CheckDraft; isNew: boolean; title?: string
  /** Ограничить выбор модуля (страницы «Бэкапы» и «Видеонаблюдение» показывают только свои типы). */
  moduleIds?: string[]
  onSave: (d: CheckDraft) => Promise<void> | void; onCancel: () => void; onDelete?: () => void
}) {
  const [moduleId, setModuleId] = useState(initial.moduleId)
  const [name, setName] = useState(initial.name)
  const [interval, setInterval] = useState(initial.intervalSeconds)
  const [confirm, setConfirmCount] = useState(initial.confirmCount)
  const [enabled, setEnabled] = useState(initial.enabled)
  const [settings, setSettings] = useState<Settings>(initial.settings)
  const [rawMode, setRawMode] = useState<boolean>(!MODULES[initial.moduleId])
  const [raw, setRaw] = useState(!MODULES[initial.moduleId] ? JSON.stringify(initial.settings, null, 2) : '')
  const [error, setError] = useState<string | null>(null)
  const [ignored, setIgnored] = useState<string[]>(initial.ignoredKeys ?? [])
  const [delay, setDelay] = useState(initial.alertDelayMinutes ?? defaultAlertDelay(initial.moduleId))
  const def = MODULES[moduleId]

  function pickModule(id: string) {
    setModuleId(id)
    const d = MODULES[id]
    if (isNew && d) { setName(d.title); setSettings({ ...d.defaults }); setInterval(d.interval); setDelay(defaultAlertDelay(id)) }
  }

  function toggleRaw() {
    if (!rawMode) setRaw(JSON.stringify(settings, null, 2))
    else { try { setSettings(JSON.parse(raw)); setError(null) } catch { setError('Настройки: невалидный JSON'); return } }
    setRawMode(!rawMode)
  }

  async function save() {
    let parsed = settings
    if (rawMode) { try { parsed = JSON.parse(raw) } catch { setError('Настройки: невалидный JSON'); return } }
    if (!name.trim()) { setError('Укажите название.'); return }
    try { await onSave({ moduleId, name: name.trim(), enabled, intervalSeconds: interval, confirmCount: confirm, settings: parsed, ignoredKeys: ignored, alertDelayMinutes: Math.max(0, delay || 0) }) }
    catch (e) { setError((e as Error).message) }
  }

  return (
    <div className="card" style={{ marginTop: 10, borderColor: 'var(--accent)' }}>
      <h2 style={{ marginTop: 0 }}>{title ?? (isNew ? 'Новая проверка' : 'Настройка проверки')}</h2>
      <div className="form-row">
        <div className="field"><label>Модуль</label>
          <select value={moduleId} onChange={e => pickModule(e.target.value)} disabled={!isNew}>
            {moduleIds
              ? moduleIds.filter(id => MODULES[id]).map(id => <option key={id} value={id}>{MODULES[id].title}</option>)
              : MODULE_GROUPS.map(g => <optgroup key={g.title} label={g.title}>{g.ids.filter(id => MODULES[id]).map(id => <option key={id} value={id}>{MODULES[id].title}</option>)}</optgroup>)}
            {!MODULES[moduleId] && <option value={moduleId}>{moduleId}</option>}
          </select>
          {def && <div className="muted small" style={{ marginTop: 4 }}>{def.hint}</div>}
        </div>
        <div className="field"><label>Название</label><input value={name} onChange={e => setName(e.target.value)} /></div>
        <div className="field" style={{ flex: '0 0 120px' }}><label>Интервал, с</label><input type="number" min={10} value={interval} onChange={e => setInterval(+e.target.value)} /></div>
        <div className="field" style={{ flex: '0 0 150px' }}><label title="Сколько подряд результатов нужно для смены состояния">Подтверждений</label><input type="number" min={1} max={10} value={confirm} onChange={e => setConfirmCount(+e.target.value)} /></div>
        <div className="field" style={{ flex: '0 0 170px' }}>
          <label title="Инцидент и уведомление — только если проблема держится дольше. Кратковременные пики видны на дашборде, но не будят. 0 — сразу.">Оповещать через, мин</label>
          <input type="number" min={0} max={1440} value={delay} onChange={e => setDelay(+e.target.value)} />
        </div>
      </div>

      {rawMode || !def
        ? <div className="field"><label>Настройки (JSON)</label><textarea className="mono" value={raw} onChange={e => setRaw(e.target.value)} /></div>
        : <def.Form s={settings} set={patch => setSettings({ ...settings, ...patch })} />}

      {ignored.length > 0 && (
        <div className="notice" style={{ marginBottom: 10 }}>
          <strong>Не отслеживаются:</strong>{' '}
          {ignored.map(k => (
            <span key={k} className="badge neutral" style={{ marginRight: 6 }}>
              {k} <a href="#" title="Снова отслеживать" onClick={e => { e.preventDefault(); setIgnored(ignored.filter(x => x !== k)) }}>вернуть</a>
            </span>
          ))}
          <div className="muted small">Скрыть показатель — ссылка «скрыть» в его строке на странице хоста. Возврат вступает в силу после сохранения.</div>
        </div>
      )}

      <div className="row between">
        <div className="row">
          <label className="row" style={{ marginBottom: 0 }}><input type="checkbox" style={{ width: 'auto' }} checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Включена</label>
          {def && <a href="#" className="small" onClick={e => { e.preventDefault(); toggleRaw() }}>{rawMode ? 'форма' : 'JSON'}</a>}
        </div>
        <div className="row">
          {onDelete && <button className="secondary sm danger" onClick={onDelete}>Удалить</button>}
          <button className="secondary sm" onClick={onCancel}>Отмена</button>
          <button className="sm" onClick={save}>Сохранить</button>
        </div>
      </div>
      {error && <div className="error">{error}</div>}
    </div>
  )
}
