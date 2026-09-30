import { useEffect, useState } from 'react'
import { api, auth, type Check, type Host } from '../api/client'
import { CheckForm, newDraft, type CheckDraft } from './CheckForm'

/** Что редактируем: новую проверку выбранного типа или существующую проверку конкретного хоста. */
export type EditorTarget =
  | { mode: 'new'; moduleIds: string[]; hint: string }
  | { mode: 'edit'; hostId: string; checkId: string }

/**
 * Редактор проверки прямо на текущей странице (Бэкапы, Видеонаблюдение) — без перехода на страницу хоста,
 * где в списке ещё десяток посторонних проверок. Сохранение и удаление идут в тот же API, что и на хосте.
 */
export function CheckEditorPanel({ target, onClose }: { target: EditorTarget; onClose: (changed: boolean) => void }) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [hostId, setHostId] = useState(target.mode === 'edit' ? target.hostId : '')
  const [moduleId, setModuleId] = useState(target.mode === 'new' ? target.moduleIds[0] : '')
  const [check, setCheck] = useState<Check | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (target.mode === 'new') {
      api.hosts().then(all => {
        const withAgent = all.filter(h => h.agent)
        setHosts(withAgent)
        if (withAgent[0]) setHostId(withAgent[0].id)
      }).catch(e => setError((e as Error).message))
    } else {
      api.checks(target.hostId)
        .then(list => {
          const found = list.find(c => c.id === target.checkId)
          if (!found) setError('Проверка не найдена — возможно, её удалили.')
          else { setCheck(found); setModuleId(found.moduleId) }
        })
        .catch(e => setError((e as Error).message))
    }
  }, [])

  const initial: CheckDraft | null = target.mode === 'edit'
    ? (check ? { moduleId: check.moduleId, name: check.name, enabled: check.enabled, intervalSeconds: check.intervalSeconds, confirmCount: check.confirmCount, settings: check.settings, ignoredKeys: check.ignoredKeys, alertDelayMinutes: check.alertDelayMinutes } : null)
    : newDraft(moduleId)

  async function save(d: CheckDraft) {
    if (target.mode === 'edit' && check) await api.updateCheck(target.hostId, check.id, d)
    else await api.createCheck(hostId, d)
    onClose(true)
  }

  async function remove() {
    if (target.mode !== 'edit' || !check) return
    if (!window.confirm(`Удалить «${check.name}» вместе с историей?`)) return
    await api.deleteCheck(target.hostId, check.id)
    onClose(true)
  }

  if (!auth.canOperate()) return null

  return (
    <div className="card" style={{ marginBottom: 14, borderColor: 'var(--accent)' }}>
      {error && <div className="error">{error}</div>}

      {target.mode === 'new' && (
        <>
          <div className="muted small" style={{ marginBottom: 8 }}>{target.hint}</div>
          <div className="form-row">
            <div className="field"><label>Хост с агентом</label>
              <select value={hostId} onChange={e => setHostId(e.target.value)}>
                {(hosts ?? []).map(h => <option key={h.id} value={h.id}>{h.tenantName} — {h.name}{h.agent?.isOnline ? '' : ' (офлайн)'}</option>)}
              </select>
              {hosts && hosts.length === 0 && <div className="error">Нет хостов с установленным агентом.</div>}
            </div>
          </div>
        </>
      )}

      {initial === null
        ? <div className="muted">Загрузка…</div>
        : (
          <CheckForm
            key={target.mode === 'edit' ? target.checkId : moduleId}
            initial={initial}
            isNew={target.mode === 'new'}
            moduleIds={target.mode === 'new' ? target.moduleIds : undefined}
            title={target.mode === 'new' ? 'Новое задание' : `Настройка: ${check?.name ?? ''}`}
            onSave={save}
            onCancel={() => onClose(false)}
            onDelete={target.mode === 'edit' ? remove : undefined}
          />
        )}
    </div>
  )
}
