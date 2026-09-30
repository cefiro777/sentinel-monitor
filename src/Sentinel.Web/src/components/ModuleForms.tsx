// Формы настроек модулей агента. Ключи полей совпадают с классами *ModuleSettings в Sentinel.Contracts.
import { useEffect, useState } from 'react'
import { api, type ServicePreset } from '../api/client'

export type Settings = Record<string, unknown>
type FormProps = { s: Settings; set: (patch: Settings) => void }

export interface ModuleDef { title: string; hint: string; defaults: Settings; interval: number; Form: (p: FormProps) => JSX.Element }

const num = (v: unknown, d = 0) => (typeof v === 'number' ? v : d)
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const arr = <T,>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : [])
const csv = (v: unknown) => arr<string | number>(v).join(', ')
const parseCsv = (v: string) => v.split(',').map(x => x.trim()).filter(Boolean)
const parseNums = (v: string) => parseCsv(v).map(Number).filter(n => !isNaN(n))

function Num({ label, k, s, set, min, step }: FormProps & { label: string; k: string; min?: number; step?: number }) {
  return <div className="field"><label>{label}</label><input type="number" min={min} step={step} value={num(s[k])} onChange={e => set({ [k]: +e.target.value })} /></div>
}
function Bool({ label, k, s, set }: FormProps & { label: string; k: string }) {
  return <label className="row" style={{ marginBottom: 10 }}><input type="checkbox" style={{ width: 'auto' }} checked={!!s[k]} onChange={e => set({ [k]: e.target.checked })} /> {label}</label>
}

/** Таблица однотипных строк (службы, адреса, порты). */
function Rows<T extends Settings>({ k, s, set, blank, columns }: FormProps & { k: string; blank: T; columns: { key: keyof T & string; label: string; type?: 'text' | 'number' | 'bool'; width?: number; placeholder?: string }[] }) {
  const rows = arr<T>(s[k])
  const update = (i: number, patch: Partial<T>) => set({ [k]: rows.map((r, n) => (n === i ? { ...r, ...patch } : r)) })
  return (
    <div className="field">
      <table>
        <thead><tr>{columns.map(c => <th key={c.key} style={{ width: c.width }}>{c.label}</th>)}<th style={{ width: 40 }}></th></tr></thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              {columns.map(c => (
                <td key={c.key}>
                  {c.type === 'bool'
                    ? <input type="checkbox" style={{ width: 'auto' }} checked={!!r[c.key]} onChange={e => update(i, { [c.key]: e.target.checked } as Partial<T>)} />
                    : <input type={c.type ?? 'text'} value={String(r[c.key] ?? '')} placeholder={c.placeholder} onChange={e => update(i, { [c.key]: c.type === 'number' ? +e.target.value : e.target.value } as Partial<T>)} />}
                </td>
              ))}
              <td><button className="secondary sm" title="Удалить" onClick={() => set({ [k]: rows.filter((_, n) => n !== i) })}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <button className="secondary sm" style={{ marginTop: 8 }} onClick={() => set({ [k]: [...rows, { ...blank }] })}>+ Добавить</button>
    </div>
  )
}

let presetsCache: ServicePreset[] | null = null

/** «Добавить набор служб»: роли сервера с сервера (/api/service-presets); уже присутствующие имена не дублируются. */
function ServicePresetPicker({ s, set }: FormProps) {
  const [presets, setPresets] = useState<ServicePreset[]>(presetsCache ?? [])
  const [id, setId] = useState('')
  useEffect(() => { if (!presetsCache) api.servicePresets().then(p => { presetsCache = p; setPresets(p) }).catch(() => {}) }, [])
  const chosen = presets.find(p => p.id === id)
  function add() {
    if (!chosen) return
    const rows = arr<Settings>(s.services)
    const have = new Set(rows.map(r => str(r.name).trim().toLowerCase()))
    const fresh = chosen.services.filter(x => !have.has(x.name.toLowerCase())).map(x => ({ name: x.name, autoRestart: x.autoRestart, critical: x.critical, comment: x.comment }))
    set({ services: [...rows, ...fresh] })
  }
  if (presets.length === 0) return null
  return (
    <div className="form-row" style={{ alignItems: 'flex-end' }}>
      <div className="field"><label>Добавить набор служб</label>
        <select value={id} onChange={e => setId(e.target.value)}>
          <option value="">— выберите роль сервера —</option>
          {presets.map(p => <option key={p.id} value={p.id}>{p.title} ({p.services.length})</option>)}
        </select>
        {chosen && <div className="muted small" style={{ marginTop: 4 }}>{chosen.description}</div>}
      </div>
      <div className="field" style={{ flex: '0 0 auto' }}><button className="secondary sm" disabled={!chosen} onClick={add}>Добавить</button></div>
    </div>
  )
}

export const MODULES: Record<string, ModuleDef> = {
  system: {
    title: 'Система', hint: 'CPU, память, аптайм, ожидающая перезагрузка, дрейф часов', interval: 60,
    defaults: { cpuWarnPercent: 85, cpuCritPercent: 95, memoryWarnPercent: 85, memoryCritPercent: 95, clockDriftWarnSeconds: 60, warnOnPendingReboot: true },
    Form: p => (
      <>
        <div className="form-row"><Num {...p} label="CPU: внимание, %" k="cpuWarnPercent" /><Num {...p} label="CPU: критично, %" k="cpuCritPercent" /></div>
        <div className="form-row"><Num {...p} label="Память: внимание, %" k="memoryWarnPercent" /><Num {...p} label="Память: критично, %" k="memoryCritPercent" /></div>
        <div className="form-row"><Num {...p} label="Допустимый дрейф часов, с" k="clockDriftWarnSeconds" /><div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}><Bool {...p} label="Предупреждать об ожидающей перезагрузке" k="warnOnPendingReboot" /></div></div>
      </>
    ),
  },
  services: {
    title: 'Службы Windows', hint: 'Состояние выбранных служб, автозапуск остановленных', interval: 60,
    defaults: { services: [] },
    Form: p => (
      <>
        <div className="muted small" style={{ marginBottom: 8 }}>Системное имя службы (не отображаемое): MSSQLSERVER, "1C:Enterprise 8.3 Server Agent (1540)", W3SVC, Spooler… Готовые наборы по ролям сервера — ниже.</div>
        <ServicePresetPicker {...p} />
        <Rows {...p} k="services" blank={{ name: '', autoRestart: false, critical: true, comment: '' }}
          columns={[{ key: 'name', label: 'Имя службы', placeholder: 'MSSQLSERVER', width: 260 }, { key: 'comment', label: 'Описание', placeholder: 'для чего' }, { key: 'autoRestart', label: 'Автозапуск', type: 'bool', width: 100 }, { key: 'critical', label: 'Критично', type: 'bool', width: 90 }]} />
      </>
    ),
  },
  disks: {
    title: 'Диски', hint: 'Свободное место на локальных дисках', interval: 300,
    defaults: { warnFreePercent: 15, critFreePercent: 7, warnFreeGb: 0, critFreeGb: 0, include: [], exclude: [] },
    Form: p => (
      <>
        <div className="form-row"><Num {...p} label="Внимание: свободно меньше, %" k="warnFreePercent" /><Num {...p} label="Критично: свободно меньше, %" k="critFreePercent" /></div>
        <div className="form-row"><Num {...p} label="Внимание: свободно меньше, ГБ (0 — не учитывать)" k="warnFreeGb" /><Num {...p} label="Критично: свободно меньше, ГБ" k="critFreeGb" /></div>
        <div className="form-row">
          <div className="field"><label>Только диски (через запятую, пусто — все)</label><input value={csv(p.s.include)} onChange={e => p.set({ include: parseCsv(e.target.value) })} placeholder="C:, D:" /></div>
          <div className="field"><label>Исключить диски</label><input value={csv(p.s.exclude)} onChange={e => p.set({ exclude: parseCsv(e.target.value) })} placeholder="E:" /></div>
        </div>
      </>
    ),
  },
  'vm.hyperv': {
    title: 'Виртуальные машины (Hyper-V)', hint: 'Состояние ВМ, аптайм, службы интеграции, контрольные точки, репликация', interval: 300,
    defaults: { include: [], exclude: [], expectedOff: [], stoppedIsCritical: true, checkHeartbeat: true, checkpointWarnDays: 3, checkReplication: true },
    Form: p => (
      <>
        <div className="muted small" style={{ marginBottom: 8 }}>Проверка ставится на сам хост Hyper-V — агент читает все ВМ через WMI, агенты внутри ВМ и отдельные учётки не нужны.</div>
        <div className="form-row">
          <div className="field"><label title="Имена ВМ как в диспетчере Hyper-V, через запятую">Следить только за (пусто — за всеми)</label><input value={csv(p.s.include)} onChange={e => p.set({ include: parseCsv(e.target.value) })} placeholder="все" /></div>
          <div className="field"><label>Не следить за</label><input value={csv(p.s.exclude)} onChange={e => p.set({ exclude: parseCsv(e.target.value) })} placeholder="TEST-VM, SHABLON" /></div>
        </div>
        <div className="form-row">
          <div className="field"><label title="Резервные ВМ и шаблоны: выключенное состояние для них — норма">Должны быть выключены</label><input value={csv(p.s.expectedOff)} onChange={e => p.set({ expectedOff: parseCsv(e.target.value) })} placeholder="RESERVE-DC" /></div>
          <Num {...p} label="Контрольные точки старше N дней (0 — не проверять)" k="checkpointWarnDays" min={0} />
        </div>
        <Bool {...p} label="Выключенная ВМ — критично (иначе предупреждение)" k="stoppedIsCritical" />
        <Bool {...p} label="Проверять службы интеграции (heartbeat) у запущенных ВМ" k="checkHeartbeat" />
        <Bool {...p} label="Проверять состояние репликации Hyper-V (если настроена)" k="checkReplication" />
      </>
    ),
  },
  eventlog: {
    title: 'Журнал событий', hint: 'Критичные ошибки в журналах Windows за окно наблюдения', interval: 300,
    defaults: { logs: ['System', 'Application'], levels: [1, 2], lookbackMinutes: 0, includeSources: [], excludeSources: [], includeEventIds: [], excludeEventIds: [], warnCount: 1, critCount: 10, maxEventsInDetails: 30 },
    Form: p => (
      <>
        <div className="form-row">
          <div className="field"><label>Журналы</label><input value={csv(p.s.logs)} onChange={e => p.set({ logs: parseCsv(e.target.value) })} placeholder="System, Application" /></div>
          <div className="field"><label>Уровни (1 критично, 2 ошибка, 3 предупреждение)</label><input value={csv(p.s.levels)} onChange={e => p.set({ levels: parseNums(e.target.value) })} /></div>
          <Num {...p} label="Окно, мин (0 = интервал проверки)" k="lookbackMinutes" />
        </div>
        <div className="form-row">
          <div className="field"><label>Только источники (подстрока)</label><input value={csv(p.s.includeSources)} onChange={e => p.set({ includeSources: parseCsv(e.target.value) })} /></div>
          <div className="field"><label>Исключить источники</label><input value={csv(p.s.excludeSources)} onChange={e => p.set({ excludeSources: parseCsv(e.target.value) })} placeholder="Schannel, DistributedCOM" /></div>
        </div>
        <div className="form-row">
          <div className="field"><label>Только Event ID</label><input value={csv(p.s.includeEventIds)} onChange={e => p.set({ includeEventIds: parseNums(e.target.value) })} /></div>
          <div className="field"><label>Исключить Event ID</label><input value={csv(p.s.excludeEventIds)} onChange={e => p.set({ excludeEventIds: parseNums(e.target.value) })} placeholder="10016, 36871" /></div>
        </div>
        <div className="form-row"><Num {...p} label="Внимание от N событий" k="warnCount" /><Num {...p} label="Критично от N событий" k="critCount" /></div>
      </>
    ),
  },
  'net.ping': {
    title: 'Ping', hint: 'ICMP-доступность хостов в сети клиента', interval: 60,
    defaults: { targets: [], timeoutMs: 1500, attempts: 3, warnLatencyMs: 0 },
    Form: p => (
      <>
        <Rows {...p} k="targets" blank={{ name: '', address: '', critical: true }}
          columns={[{ key: 'name', label: 'Название', placeholder: 'Роутер' }, { key: 'address', label: 'Адрес', placeholder: '192.168.1.1' }, { key: 'critical', label: 'Критично', type: 'bool', width: 90 }]} />
        <div className="form-row"><Num {...p} label="Таймаут, мс" k="timeoutMs" /><Num {...p} label="Попыток" k="attempts" /><Num {...p} label="Внимание при задержке > мс (0 — нет)" k="warnLatencyMs" /></div>
      </>
    ),
  },
  'net.tcp': {
    title: 'TCP-порт / TLS', hint: 'Доступность порта, срок сертификата', interval: 120,
    defaults: { targets: [], timeoutMs: 3000 },
    Form: p => (
      <>
        <Rows {...p} k="targets" blank={{ name: '', host: '', port: 443, tls: false, certWarnDays: 14, critical: true }}
          columns={[{ key: 'name', label: 'Название' }, { key: 'host', label: 'Хост' }, { key: 'port', label: 'Порт', type: 'number', width: 90 }, { key: 'tls', label: 'TLS', type: 'bool', width: 60 }, { key: 'certWarnDays', label: 'Серт., дн.', type: 'number', width: 90 }, { key: 'critical', label: 'Критично', type: 'bool', width: 90 }]} />
        <div className="form-row"><Num {...p} label="Таймаут, мс" k="timeoutMs" /></div>
      </>
    ),
  },
}

import { BACKUP_MODULES } from './BackupForms'
import { CCTV_MODULES } from './CctvForms'
Object.assign(MODULES, BACKUP_MODULES, CCTV_MODULES)

export { str }

/** Группы модулей для выпадающего списка в редакторе проверки. */
export const MODULE_GROUPS: { title: string; ids: string[] }[] = [
  { title: 'Мониторинг сервера', ids: ['system', 'services', 'disks', 'eventlog', 'net.ping', 'net.tcp'] },
  { title: 'Виртуализация', ids: ['vm.hyperv'] },
  { title: 'Бэкапы', ids: ['backup.files', 'backup.1c.file', 'backup.mssql', 'backup.postgres'] },
  { title: 'Видеонаблюдение', ids: ['cctv.hikvision', 'cctv.dahua', 'cctv.onvif', 'cctv.rtsp'] },
]
