import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api, auth, timeAgo, type CctvDevice } from '../api/client'
import { StatusDot, usePolling } from '../components/Layout'
import { CheckEditorPanel, type EditorTarget } from '../components/CheckEditorPanel'

const MODULE: Record<string, string> = { 'cctv.hikvision': 'Hikvision', 'cctv.dahua': 'Dahua', 'cctv.onvif': 'ONVIF', 'cctv.rtsp': 'RTSP' }

interface Info { vendor?: string; model?: string; serial?: string; firmware?: string; deviceTime?: string; timeDriftSeconds?: number; hdd?: { name: string; status: string; capacityGb: number; freeGb: number }[]; channels?: { id: number; name: string; online?: boolean; recording?: boolean; streamOk?: boolean; mode?: 'motion' | 'ignored' | null }[]; problems?: string[] }

export function CctvPage() {
  const { data, error, refresh } = usePolling(() => api.cctv(), 20000)
  const [open, setOpen] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorTarget | null>(null)
  if (error) return <div className="error">{error}</div>
  if (!data) return <div className="muted">Загрузка…</div>

  const problems = data.filter(d => d.status !== 'ok')
  const canCommand = auth.hasMfa()

  async function command(d: CctvDevice, type: 'cctv.settime' | 'cctv.reboot') {
    if (type === 'cctv.reboot' && !confirm(`Перезагрузить ${d.key}? Запись прервётся на время перезагрузки.`)) return
    setMsg(null)
    try { const c = await api.issueCommand(d.hostId, type, { checkId: d.checkId, host: d.key }); setMsg(`${type === 'cctv.settime' ? 'Установка времени' : 'Перезагрузка'} ${d.key}: ${c.status === 'sent' ? 'отправлена агенту' : 'в очереди'}`) }
    catch (e) { setMsg((e as Error).message) }
  }

  return (
    <>
      <div className="row between">
        <h1>Видеонаблюдение</h1>
        {auth.canOperate() && <button className="sm" onClick={() => setEditor({ mode: 'new', moduleIds: ['cctv.hikvision', 'cctv.dahua', 'cctv.onvif', 'cctv.rtsp'], hint: 'Опрос ведёт агент на выбранном хосте — у него должен быть сетевой доступ к регистраторам (та же локальная сеть или VPN).' })}>+ Регистратор / камеры</button>}
      </div>
      {editor && <CheckEditorPanel target={editor} onClose={changed => { setEditor(null); if (changed) refresh() }} />}
      <div className="grid">
        <Stat label="Устройств" value={data.length} />
        <Stat label="С проблемами" value={problems.length} tone={problems.length ? 'critical' : 'ok'} />
        <Stat label="Каналов онлайн" value={data.reduce((n, d) => n + ((d.details as Info | undefined)?.channels?.filter(c => c.online !== false).length ?? 0), 0)} />
        <Stat label="Расходятся часы" value={data.filter(d => Math.abs((d.details as Info | undefined)?.timeDriftSeconds ?? 0) > 60).length} />
      </div>
      {msg && <div className="notice">{msg}</div>}
      <div className="card" style={{ padding: 0, marginTop: 14 }}>
        <table>
          <thead><tr><th style={{ width: 24 }}></th><th>Устройство</th><th>Хост / клиент</th><th>Каналы</th><th>Запись</th><th>Диски</th><th>Часы</th><th>Проверено</th><th></th></tr></thead>
          <tbody>
            {data.map(d => {
              const id = d.checkId + d.key
              const info = d.details as Info | undefined
              return (
                <tr key={id} className="clickable" onClick={() => setOpen(open === id ? null : id)}>
                  <td><StatusDot status={d.status} /></td>
                  <td style={{ minWidth: 260 }}>
                    <strong style={{ fontSize: 15 }}>{d.name}</strong>
                    <div className="muted small mono">{d.key}{info?.model && ` · ${info.model}`}</div>
                    <div className="muted small">{MODULE[d.moduleId] ?? d.moduleId} · {d.checkName}</div>
                    {d.status !== 'ok' && <div className="small" style={{ marginTop: 4, color: d.status === 'warning' ? 'var(--warning)' : 'var(--critical)' }} title={d.summary}>{short(d.summary)}</div>}
                  </td>
                  <td><Link to={`/hosts/${d.hostId}`} onClick={e => e.stopPropagation()}>{d.hostName}</Link><div className="muted small">{d.tenantName}</div></td>
                  <td><Chip {...channelChip(info)} /></td>
                  <td><Chip {...recordingChip(info)} /></td>
                  <td><Chip {...hddChip(info, d.moduleId)} /></td>
                  <td><Chip {...clockChip(info)} /></td>
                  <td className="muted" title={d.lastResultAt}>{timeAgo(d.lastResultAt)}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {canCommand && d.agentOnline && (d.moduleId === 'cctv.hikvision' || d.moduleId === 'cctv.dahua') && (
                      <>
                        <button className="secondary sm" onClick={e => { e.stopPropagation(); command(d, 'cctv.settime') }}>Время</button>
                        <button className="secondary sm danger" style={{ marginLeft: 6 }} onClick={e => { e.stopPropagation(); command(d, 'cctv.reboot') }}>Ребут</button>
                      </>
                    )}
                    {auth.canOperate() && <button className="secondary sm" style={{ marginLeft: 6 }} onClick={e => { e.stopPropagation(); setEditor({ mode: 'edit', hostId: d.hostId, checkId: d.checkId }) }}>Настроить</button>}
                  </td>
                </tr>
              )
            })}
            {data.length === 0 && <tr><td colSpan={9} className="muted">Устройств нет — нажмите «+ Регистратор / камеры».</td></tr>}
          </tbody>
        </table>
      </div>
      {open && data.filter(d => d.checkId + d.key === open).map(d => (
        <Details key={open} device={d} onClose={() => { setOpen(null); refresh() }}
          onEditCheck={() => setEditor({ mode: 'edit', hostId: d.hostId, checkId: d.checkId })} />
      ))}
    </>
  )
}

type ChipProps = { text: string; tone: 'ok' | 'warning' | 'critical' | 'muted'; title?: string }

/** Компактный значок состояния в строке устройства — вместо мелкого текста. */
function Chip({ text, tone, title }: ChipProps) {
  if (tone === 'muted') return <span className="muted" title={title}>{text}</span>
  return <span className={'badge ' + tone} title={title}>{text}</span>
}

function short(summary: string) {
  const text = summary.includes(':') ? summary.slice(summary.indexOf(':') + 1).trim() : summary
  return text.length > 90 ? text.slice(0, 90) + '…' : text
}

function channelChip(info?: Info): ChipProps {
  const all = (info?.channels ?? []).filter(c => c.mode !== 'ignored')
  if (all.length === 0) return { text: '—', tone: 'muted', title: 'каналы не обнаружены' }
  const known = all.filter(c => c.online != null)
  if (known.length === 0) return { text: `${all.length} кан.`, tone: 'muted', title: 'аналоговые входы: статус связи недоступен' }
  const online = known.filter(c => c.online).length
  return { text: `${online}/${known.length}`, tone: online === known.length ? 'ok' : online === 0 ? 'critical' : 'warning', title: 'камер на связи из проверяемых' }
}

function recordingChip(info?: Info): ChipProps {
  const checked = (info?.channels ?? []).filter(c => c.recording != null)
  if (checked.length === 0) return { text: '—', tone: 'muted', title: 'запись не проверяется' }
  const rec = checked.filter(c => c.recording).length
  return { text: `${rec}/${checked.length}`, tone: rec === checked.length ? 'ok' : rec === 0 ? 'critical' : 'warning', title: 'каналов с записью за окно проверки' }
}

function hddChip(info?: Info, moduleId?: string): ChipProps {
  const hdd = info?.hdd ?? []
  // Диски есть только у регистраторов; RTSP/ONVIF их не отдают — там прочерк, а не проблема.
  if (hdd.length === 0) return moduleId === 'cctv.hikvision' || moduleId === 'cctv.dahua'
    ? { text: 'нет дисков', tone: 'critical', title: 'регистратор не видит ни одного диска' }
    : { text: '—', tone: 'muted', title: 'модуль не отдаёт состояние дисков' }
  const bad = hdd.filter(h => h.status.toLowerCase() !== 'ok')
  if (bad.length > 0) return { text: bad[0].status, tone: 'critical', title: bad.map(h => `${h.name}: ${h.status}`).join(', ') }
  return { text: `${hdd.length} ок`, tone: 'ok', title: hdd.map(h => `${h.name}: ${h.capacityGb} ГБ, свободно ${h.freeGb} ГБ`).join('; ') }
}

function clockChip(info?: Info): ChipProps {
  const drift = info?.timeDriftSeconds
  if (drift == null) return { text: '—', tone: 'muted' }
  const abs = Math.abs(drift)
  const text = abs < 60 ? 'точно' : abs < 3600 ? `${Math.round(drift / 60)} мин` : abs < 86400 ? `${Math.round(drift / 3600)} ч` : `${Math.round(drift / 86400)} дн.`
  return { text, tone: abs <= 60 ? 'ok' : abs <= 600 ? 'warning' : 'critical', title: `расхождение часов: ${drift} с (${info?.deviceTime ?? '—'})` }
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'critical' }) {
  return <div className="card"><div className="muted small">{label}</div><div style={{ fontSize: 26, fontWeight: 600, color: tone === 'critical' ? 'var(--critical)' : tone === 'ok' ? 'var(--ok)' : undefined }}>{value}</div></div>
}

type Mode = 'check' | 'motion' | 'ignored'
const MODE_LABEL: Record<Mode, string> = { check: 'проверять', motion: 'только доступность', ignored: 'игнорировать' }

function Details({ device, onClose, onEditCheck }: { device: CctvDevice; onClose: () => void; onEditCheck: () => void }) {
  const info = (device.details ?? {}) as Info
  const channels = info.channels ?? []
  const editable = auth.canOperate() && (device.moduleId === 'cctv.hikvision' || device.moduleId === 'cctv.dahua') && channels.length > 0
  const initial = (): Record<number, Mode> => Object.fromEntries(channels.map(c => [c.id, (c.mode ?? 'check') as Mode]))
  const [modes, setModes] = useState<Record<number, Mode>>(initial)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const dirty = channels.some(c => modes[c.id] !== ((c.mode ?? 'check') as Mode))

  function setAll(mode: Mode) { setModes(Object.fromEntries(channels.map(c => [c.id, mode]))) }
  async function save() {
    setBusy(true); setMsg(null)
    try {
      const ids = (m: Mode) => channels.filter(c => modes[c.id] === m).map(c => c.id)
      const r = await api.setCctvChannels(device.checkId, device.key, { ignoreChannels: ids('ignored'), motionChannels: ids('motion') })
      setMsg(r.applied ? 'Сохранено — агент применит при следующем опросе (несколько минут).' : 'Сохранено — агент офлайн, применится при выходе на связь.')
    } catch (e) { setMsg((e as Error).message) }
    finally { setBusy(false) }
  }

  return (
    <div className="card" style={{ marginTop: 14, borderColor: 'var(--accent)' }}>
      <div className="row between">
        <h2 style={{ margin: 0 }}>{device.name} <span className="muted mono" style={{ fontWeight: 400, fontSize: 14 }}>{device.key}</span> <span className="muted" style={{ fontWeight: 400, fontSize: 14 }}>· {info.vendor ?? MODULE[device.moduleId]}</span></h2>
        <div className="row">
          {auth.canOperate() && <button className="secondary sm" onClick={onEditCheck}>Настроить проверку</button>}
          <button className="secondary sm" onClick={onClose}>Закрыть</button>
        </div>
      </div>
      <div className="row" style={{ marginTop: 10, gap: 20, alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <div style={{ flex: '0 1 320px', minWidth: 260 }}>
          <dl className="kv">
            <dt>Модель</dt><dd>{info.model ?? '—'}</dd>
            <dt>Серийный</dt><dd className="mono">{info.serial ?? '—'}</dd>
            <dt>Прошивка</dt><dd>{info.firmware ?? '—'}</dd>
            <dt>Время устройства</dt><dd>{info.deviceTime ?? '—'}{info.timeDriftSeconds != null && <span className="muted"> ({info.timeDriftSeconds > 0 ? '+' : ''}{info.timeDriftSeconds} с)</span>}</dd>
          </dl>
          {info.hdd && info.hdd.length > 0 && (
            <table style={{ marginTop: 10 }}>
              <thead><tr><th>Диск</th><th>Состояние</th><th>Объём</th><th>Свободно</th></tr></thead>
              <tbody>{info.hdd.map((h, i) => <tr key={i}><td>{h.name}</td><td><span className={'badge ' + (h.status.toLowerCase() === 'ok' ? 'ok' : 'critical')}>{h.status}</span></td><td>{h.capacityGb} ГБ</td><td>{h.freeGb} ГБ</td></tr>)}</tbody>
            </table>
          )}
        </div>
        <div style={{ flex: '1 1 520px', minWidth: 420 }}>
          {channels.length > 0 ? (
            <>
              <table>
                <thead><tr><th>Канал</th><th>Название</th><th>Онлайн</th><th>Запись</th><th>Поток</th>{editable && <th style={{ width: 210 }}>Режим</th>}</tr></thead>
                <tbody>
                  {channels.map(c => {
                    const mode = modes[c.id] ?? 'check'
                    return (
                      <tr key={c.id} style={{ opacity: mode === 'ignored' ? 0.55 : 1 }}>
                        <td className="mono">{c.id}</td><td>{c.name}</td>
                        <td title={c.online == null ? 'аналоговый вход: статус недоступен' : c.online ? 'камера на связи' : 'камера не отвечает'}>{c.online == null ? <span className="muted">—</span> : <span className={'badge ' + (c.online ? 'ok' : 'critical')}>{c.online ? '● на связи' : '● нет связи'}</span>}</td>
                        <td title={c.mode === 'motion' ? 'запись по движению — не проверяется' : c.recording ? 'есть записи за окно проверки' : 'записей за окно проверки нет'}>{c.recording == null ? <span className="muted small">{c.mode === 'motion' ? '◐ по движению' : '—'}</span> : <span className={'badge ' + (c.recording ? 'ok' : 'critical')}>{c.recording ? '⏺ пишет' : '⏹ не пишет'}</span>}</td>
                        <td>{c.streamOk == null ? <span className="muted">—</span> : <span className={'badge ' + (c.streamOk ? 'ok' : 'critical')}>{c.streamOk ? '▶ поток' : '✕ нет потока'}</span>}</td>
                        {editable && (
                          <td>
                            <select value={mode} onChange={e => setModes({ ...modes, [c.id]: e.target.value as Mode })}>
                              {(Object.keys(MODE_LABEL) as Mode[]).map(m => <option key={m} value={m}>{MODE_LABEL[m]}</option>)}
                            </select>
                          </td>
                        )}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
              {editable && (
                <div className="row between" style={{ marginTop: 10 }}>
                  <div className="row small" style={{ gap: 10, flexWrap: 'wrap' }}>
                    <span className="muted">Все:</span>
                    <a href="#" onClick={e => { e.preventDefault(); setAll('check') }}>проверять</a>
                    <a href="#" onClick={e => { e.preventDefault(); setAll('motion') }}>только доступность</a>
                    <a href="#" onClick={e => { e.preventDefault(); setAll('ignored') }}>игнорировать</a>
                  </div>
                  <div className="row">
                    {dirty && <button className="secondary sm" onClick={() => setModes(initial())}>Отменить</button>}
                    <button className="sm" disabled={!dirty || busy} onClick={save}>Сохранить режимы</button>
                  </div>
                </div>
              )}
              {editable && <div className="muted small" style={{ marginTop: 6 }}>«Только доступность» — для камер с записью по движению: отсутствие записи за окно не считается проблемой. «Игнорировать» — канал без камеры: не проверяется вовсе, но остаётся в списке.</div>}
              {msg && <div className="small" style={{ marginTop: 6 }}>{msg}</div>}
            </>
          ) : <div className="muted">Каналы не обнаружены</div>}
          {info.problems && info.problems.length > 0 && <ul className="small" style={{ color: 'var(--warning)', marginTop: 10 }}>{info.problems.map((p, i) => <li key={i}>{p}</li>)}</ul>}
        </div>
      </div>
    </div>
  )
}
