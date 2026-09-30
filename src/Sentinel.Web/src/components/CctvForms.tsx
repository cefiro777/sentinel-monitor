// Формы модулей видеонаблюдения.
import type { ModuleDef, Settings } from './ModuleForms'

type FormProps = { s: Settings; set: (patch: Settings) => void }
type Device = { name: string; host: string; port: number; useHttps?: boolean; username: string; password: string; critical: boolean; recordingWindowMinutes: number; channels: number[]; ignoreChannels?: number[]; motionChannels?: number[] }
const num = (v: unknown, d = 0) => (typeof v === 'number' ? v : d)
const devices = (v: unknown): Device[] => (Array.isArray(v) ? (v as Device[]) : [])
const blankDevice: Device = { name: '', host: '', port: 80, username: 'admin', password: '', critical: true, recordingWindowMinutes: 30, channels: [] }

function DeviceRows({ s, set, recording }: FormProps & { recording: boolean }) {
  const rows = devices(s.devices)
  const upd = (i: number, patch: Partial<Device>) => set({ devices: rows.map((r, n) => (n === i ? { ...r, ...patch } : r)) })
  return (
    <div className="field">
      <table>
        <thead><tr><th>Название</th><th>Хост</th><th style={{ width: 80 }}>Порт</th><th style={{ width: 60 }} title="HTTPS вместо HTTP; сертификат регистратора не проверяется">HTTPS</th><th>Логин</th><th>Пароль</th>{recording && <th style={{ width: 110 }} title="Проверять наличие записей за N минут (0 — нет)">Запись, мин</th>}<th style={{ width: 80 }}>Критично</th><th style={{ width: 40 }}></th></tr></thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              <td><input value={r.name} placeholder="Офис, вход" onChange={e => upd(i, { name: e.target.value })} /></td>
              <td><input value={r.host} placeholder="192.168.1.100" onChange={e => upd(i, { host: e.target.value })} /></td>
              <td><input type="number" value={r.port} onChange={e => upd(i, { port: +e.target.value })} /></td>
              <td><input type="checkbox" style={{ width: 'auto' }} checked={!!r.useHttps} onChange={e => upd(i, { useHttps: e.target.checked, port: e.target.checked && r.port === 80 ? 443 : !e.target.checked && r.port === 443 ? 80 : r.port })} /></td>
              <td><input value={r.username} onChange={e => upd(i, { username: e.target.value })} /></td>
              <td><input type="password" value={r.password} onChange={e => upd(i, { password: e.target.value })} /></td>
              {recording && <td><input type="number" min={0} value={r.recordingWindowMinutes} onChange={e => upd(i, { recordingWindowMinutes: +e.target.value })} /></td>}
              
              <td><input type="checkbox" style={{ width: 'auto' }} checked={r.critical} onChange={e => upd(i, { critical: e.target.checked })} /></td>
              <td><button className="secondary sm" onClick={() => set({ devices: rows.filter((_, n) => n !== i) })}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <button className="secondary sm" style={{ marginTop: 8 }} onClick={() => set({ devices: [...rows, { ...blankDevice }] })}>+ Устройство</button>
    </div>
  )
}

function Drift(p: FormProps & { autoSet?: boolean }) {
  return (
    <div className="form-row" style={{ alignItems: 'flex-end' }}>
      <div className="field" style={{ flex: '0 0 260px' }}><label>Допустимое расхождение часов, с</label><input type="number" value={num(p.s.timeDriftWarnSeconds, 60)} onChange={e => p.set({ timeDriftWarnSeconds: +e.target.value })} /></div>
      {p.autoSet && <div className="field"><label className="row" style={{ marginBottom: 10 }} title="При расхождении больше порога агент сам выставит на регистраторе время сервера мониторинга. Надёжнее — NTP на самом регистраторе, но это страховка от севшей батарейки."><input type="checkbox" style={{ width: 'auto' }} checked={!!p.s.autoSetTime} onChange={e => p.set({ autoSetTime: e.target.checked })} /> Выставлять время автоматически</label></div>}
    </div>
  )
}

export const CCTV_MODULES: Record<string, ModuleDef> = {
  'cctv.hikvision': {
    title: 'Видео: Hikvision / HiWatch', hint: 'ISAPI: модель, время, диски, каналы онлайн, наличие записи за окно', interval: 300,
    defaults: { devices: [], timeDriftWarnSeconds: 60, hddFreeWarnPercent: 0, autoSetTime: false },
    Form: p => <><DeviceRows {...p} recording /><Drift {...p} autoSet /><div className="muted small">Проверку выполняет агент этого хоста: регистраторы в той же сети указывайте по локальному адресу. Удалённые площадки — через проброс на роутере веб-порта регистратора (80, лучше HTTPS 443) с ограничением по IP источника; порт 8000 (SDK для iVMS/Hik-Connect) не подходит. Пользователь должен иметь права на просмотр архива. Режимы каналов (игнорировать / только доступность) настраиваются на странице «Видеонаблюдение» после первого опроса.</div></>,
  },
  'cctv.dahua': {
    title: 'Видео: Dahua / RVi', hint: 'HTTP API: модель, время, диски, каналы, наличие записи за окно', interval: 300,
    defaults: { devices: [], timeDriftWarnSeconds: 60, autoSetTime: false },
    Form: p => <><DeviceRows {...p} recording /><Drift {...p} autoSet /><div className="muted small">Порт — HTTP веб-интерфейса (обычно 80). Порт 37777 (SDK для SmartPSS/DMSS) не подходит.</div></>,
  },
  'cctv.onvif': {
    title: 'Видео: ONVIF (любой производитель)', hint: 'Время устройства, модель, медиапрофили и живость их RTSP-потоков', interval: 300,
    defaults: { devices: [], timeDriftWarnSeconds: 60, checkStreams: true },
    Form: p => (
      <>
        <DeviceRows {...p} recording={false} />
        <div className="form-row">
          <Drift {...p} />
          <div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}><label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.checkStreams !== false} onChange={e => p.set({ checkStreams: e.target.checked })} /> Проверять RTSP-потоки профилей</label></div>
        </div>
        <div className="muted small">Порт ONVIF обычно 80 (Hikvision), 8899 (китайские камеры) или 8000. Пользователю нужны права на ONVIF.</div>
      </>
    ),
  },
  'cctv.rtsp': {
    title: 'Видео: RTSP-потоки', hint: 'DESCRIBE + SETUP + PLAY и ожидание реальных данных по конкретным URL', interval: 300,
    defaults: { streams: [], timeoutMs: 8000, requireData: true },
    Form: p => {
      const rows = (Array.isArray(p.s.streams) ? p.s.streams : []) as { name: string; url: string; critical: boolean }[]
      const upd = (i: number, patch: object) => p.set({ streams: rows.map((r, n) => (n === i ? { ...r, ...patch } : r)) })
      return (
        <>
          <div className="field">
            <table>
              <thead><tr><th style={{ width: 200 }}>Название</th><th>URL</th><th style={{ width: 80 }}>Критично</th><th style={{ width: 40 }}></th></tr></thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={i}>
                    <td><input value={r.name} onChange={e => upd(i, { name: e.target.value })} /></td>
                    <td><input className="mono" value={r.url} placeholder="rtsp://admin:pass@192.168.1.100:554/Streaming/Channels/101" onChange={e => upd(i, { url: e.target.value })} /></td>
                    <td><input type="checkbox" style={{ width: 'auto' }} checked={r.critical} onChange={e => upd(i, { critical: e.target.checked })} /></td>
                    <td><button className="secondary sm" onClick={() => p.set({ streams: rows.filter((_, n) => n !== i) })}>×</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <button className="secondary sm" style={{ marginTop: 8 }} onClick={() => p.set({ streams: [...rows, { name: '', url: '', critical: true }] })}>+ Поток</button>
          </div>
          <div className="form-row">
            <div className="field" style={{ flex: '0 0 160px' }}><label>Таймаут, мс</label><input type="number" value={num(p.s.timeoutMs, 8000)} onChange={e => p.set({ timeoutMs: +e.target.value })} /></div>
            <div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}><label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.requireData !== false} onChange={e => p.set({ requireData: e.target.checked })} /> Требовать реальные данные (не только описание потока)</label></div>
          </div>
          <div className="muted small">Hikvision: rtsp://…:554/Streaming/Channels/101 (канал 1, основной поток). Dahua: rtsp://…:554/cam/realmonitor?channel=1&subtype=0. Пароль в URL хранится зашифрованным.</div>
        </>
      )
    },
  },
}
