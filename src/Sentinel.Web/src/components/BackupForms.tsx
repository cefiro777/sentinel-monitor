// Формы заданий бэкапа. Общие блоки: расписание, назначение, ретеншн, облако (Яндекс.Диск).
import type { ModuleDef, Settings } from './ModuleForms'

type FormProps = { s: Settings; set: (patch: Settings) => void }
const obj = (v: unknown): Settings => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Settings) : {})
const str = (v: unknown, d = '') => (typeof v === 'string' ? v : d)
const num = (v: unknown, d = 0) => (typeof v === 'number' ? v : d)
const list = (v: unknown) => (Array.isArray(v) ? (v as string[]) : [])
const lines = (v: unknown) => list(v).join('\n')
const parseLines = (v: string) => v.split('\n').map(x => x.trim()).filter(Boolean)

function Sub({ k, s, set, children }: FormProps & { k: string; children: (p: FormProps) => JSX.Element }) {
  const sub = obj(s[k])
  return children({ s: sub, set: patch => set({ [k]: { ...sub, ...patch } }) })
}

const defaults = {
  schedule: { type: 'daily', time: '02:00', everyHours: 6, daysOfWeek: [1, 2, 3, 4, 5] },
  destination: { path: '', username: '', password: '' },
  retention: { keepFullSets: 4, cloudKeepCount: 30 },
  cloud: { enabled: false, provider: 'yandex', token: '', rootFolder: '/Sentinel', verifyHash: true, warnFreeGb: 5 },
}

function CommonBlocks(p: FormProps) {
  return (
    <>
      <h3 className="muted small" style={{ margin: '14px 0 6px', textTransform: 'uppercase' }}>Расписание</h3>
      <Sub {...p} k="schedule">{q => (
        <div className="form-row">
          <div className="field"><label>Тип</label>
            <select value={str(q.s.type, 'daily')} onChange={e => q.set({ type: e.target.value })}><option value="daily">Ежедневно</option><option value="weekly">По дням недели</option><option value="hourly">Каждые N часов</option></select>
          </div>
          {str(q.s.type, 'daily') !== 'hourly' && <div className="field"><label>Время (локальное время сервера)</label><input type="time" value={str(q.s.time, '02:00')} onChange={e => q.set({ time: e.target.value })} /></div>}
          {str(q.s.type, 'daily') === 'hourly' && <div className="field"><label>Каждые, часов</label><input type="number" min={1} value={num(q.s.everyHours, 6)} onChange={e => q.set({ everyHours: +e.target.value })} /></div>}
          {str(q.s.type, 'daily') === 'weekly' && <div className="field"><label>Дни (1 пн … 7 вс, через запятую)</label><input value={list(q.s.daysOfWeek).join(', ')} onChange={e => q.set({ daysOfWeek: e.target.value.split(',').map(x => +x.trim()).filter(x => x >= 1 && x <= 7) })} /></div>}
        </div>
      )}</Sub>

      <h3 className="muted small" style={{ margin: '14px 0 6px', textTransform: 'uppercase' }}>Куда складывать</h3>
      <Sub {...p} k="destination">{q => (
        <div className="form-row">
          <div className="field" style={{ flex: 2 }}><label>Папка (локальная или \\сервер\шара)</label><input value={str(q.s.path)} onChange={e => q.set({ path: e.target.value })} placeholder="D:\Backups\1C  или  \\nas\backup\srv1" /></div>
          <div className="field"><label>Логин для шары (необязательно)</label><input value={str(q.s.username)} onChange={e => q.set({ username: e.target.value })} placeholder="DOMAIN\backup" /></div>
          <div className="field"><label>Пароль</label><input type="password" value={str(q.s.password)} onChange={e => q.set({ password: e.target.value })} /></div>
        </div>
      )}</Sub>

      <h3 className="muted small" style={{ margin: '14px 0 6px', textTransform: 'uppercase' }}>Хранение и облако</h3>
      <div className="form-row">
        <Sub {...p} k="retention">{q => (
          <>
            <div className="field"><label>Хранить локально полных наборов</label><input type="number" min={1} value={num(q.s.keepFullSets, 4)} onChange={e => q.set({ keepFullSets: +e.target.value })} /></div>
            <div className="field"><label>Хранить в облаке файлов (0 — не удалять)</label><input type="number" min={0} value={num(q.s.cloudKeepCount, 30)} onChange={e => q.set({ cloudKeepCount: +e.target.value })} /></div>
          </>
        )}</Sub>
      </div>
      <Sub {...p} k="cloud">{q => (
        <>
          <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={!!q.s.enabled} onChange={e => q.set({ enabled: e.target.checked })} /> Выгружать в Яндекс.Диск</label>
          {!!q.s.enabled && (
            <>
              <div className="form-row">
                <div className="field" style={{ flex: 2 }}><label>OAuth-токен Яндекс.Диска</label><input type="password" value={str(q.s.token)} onChange={e => q.set({ token: e.target.value })} /></div>
                <div className="field"><label>Корневая папка на Диске</label><input value={str(q.s.rootFolder, '/Sentinel')} onChange={e => q.set({ rootFolder: e.target.value })} /></div>
                <div className="field" style={{ flex: '0 0 170px' }}><label>Внимание, если свободно &lt; ГБ</label><input type="number" min={0} value={num(q.s.warnFreeGb, 5)} onChange={e => q.set({ warnFreeGb: +e.target.value })} /></div>
              </div>
              <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={q.s.verifyHash !== false} onChange={e => q.set({ verifyHash: e.target.checked })} /> Сверять хеш после загрузки</label>
              <div className="muted small">Токен: зарегистрируйте приложение на oauth.yandex.ru с правами «Яндекс.Диск REST API» (cloud_api:disk.app_folder / disk.write / disk.read / disk.info), затем откройте
                https://oauth.yandex.ru/authorize?response_type=token&amp;client_id=&lt;ID приложения&gt; и скопируйте access_token. Файлы лягут в {str(q.s.rootFolder, '/Sentinel')}/&lt;клиент&gt;/&lt;хост&gt;/&lt;задание&gt;/.</div>
            </>
          )}
        </>
      )}</Sub>
    </>
  )
}

function Head({ s, set, mode }: FormProps & { mode?: boolean }) {
  const mirror = !!mode && str(s.mode, 'zip') === 'mirror'
  return (
    <>
      <div className="form-row">
        {mode && (
          <div className="field" style={{ flex: '1 1 360px' }}><label>Режим бэкапа</label>
            <select value={str(s.mode, 'zip')} onChange={e => set({ mode: e.target.value })}>
              <option value="zip">Архивы zip — полный + разностные, с историей</option>
              <option value="mirror">Зеркало — актуальная копия папки</option>
            </select>
          </div>
        )}
        <div className="field"><label>Код задания (для имён файлов и папки копии, латиницей)</label><input value={str(s.jobSlug)} onChange={e => set({ jobSlug: e.target.value })} placeholder="buh-1c" /></div>
        {!mirror && <div className="field"><label>Полный бэкап каждые N дней (между ними разностные; 1 — всегда полный)</label><input type="number" min={1} value={num(s.fullEveryDays, 7)} onChange={e => set({ fullEveryDays: +e.target.value })} /></div>}
      </div>
      {mirror && (
        <>
          <div className="form-row" style={{ alignItems: 'flex-end' }}>
            <div className="field">
              <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={s.mirrorDeleteRemoved !== false} onChange={e => set({ mirrorDeleteRemoved: e.target.checked })} /> Удалять в копии то, чего больше нет в источнике</label>
            </div>
            {s.mirrorDeleteRemoved !== false && (
              <div className="field" style={{ flex: '0 0 300px' }}>
                <label title="Файл, пропавший из источника, остаётся в копии на этот срок — чтобы успеть заметить случайное удаление">Удалять не сразу, а через, дней (0 — сразу)</label>
                <input type="number" min={0} max={365} value={num(s.mirrorDeleteAfterDays, 14)} onChange={e => set({ mirrorDeleteAfterDays: +e.target.value })} />
              </div>
            )}
          </div>
          <div className="form-row" style={{ alignItems: 'flex-end' }}>
            <div className="field" style={{ flex: '1 1 380px' }}>
              <label title="Часть файлов может не копироваться принципиально: нет прав, файл занят, слишком длинный путь">Если файл скопировать не удалось</label>
              <select value={str(s.mirrorOnError, 'fail')} onChange={e => set({ mirrorOnError: e.target.value })}>
                <option value="fail">Считать задание неуспешным (инцидент)</option>
                <option value="skip">Пропускать: задание успешно, в отчёте «пропущено N»</option>
              </select>
            </div>
            {str(s.mirrorOnError, 'fail') === 'skip' && (
              <div className="field">
                <label className="row" style={{ marginBottom: 8 }} title="Файл попадает в скрытый журнал .sentinel-mirror-skip.tsv в копии; повтор — только если файл изменится в источнике"><input type="checkbox" style={{ width: 'auto' }} checked={s.mirrorRememberSkipped !== false} onChange={e => set({ mirrorRememberSkipped: e.target.checked })} /> Не пробовать их снова, пока не изменятся</label>
              </div>
            )}
          </div>
          <div className="notice">Файл, удалённый в источнике, пролежит в копии заданный срок, потом исчезнет — так остаётся время восстановить случайно удалённое (журнал отсрочек — скрытый файл <span className="mono">.sentinel-mirror.tsv</span> в корне копии).
            Зеркало — копия текущего состояния, а не история версий: удалённый или зашифрованный вымогателем файл так же исчезнет (или испортится) и в копии при следующем запуске.
            Для защиты от этого держите рядом второе задание с архивами zip и выгрузкой в облако, пусть и раз в неделю. Хранение наборов и облако в режиме зеркала не применяются.</div>
        </>
      )}
    </>
  )
}

export const BACKUP_MODULES: Record<string, ModuleDef> = {
  'backup.files': {
    title: 'Бэкап файлов', hint: 'Архивы zip (полный + разностные) или зеркало — актуальная копия папки; VSS для открытых файлов', interval: 3600,
    defaults: { jobSlug: '', mode: 'zip', mirrorDeleteRemoved: true, mirrorDeleteAfterDays: 14, mirrorOnError: 'fail', mirrorRememberSkipped: true, sources: [], exclude: ['*.tmp', '*\\Temp\\*'], fullEveryDays: 7, useVss: true, ...defaults },
    Form: p => (
      <>
        <Head {...p} mode />
        <div className="form-row">
          <div className="field"><label>Источники (по одному на строку)</label><textarea className="mono" value={lines(p.s.sources)} onChange={e => p.set({ sources: parseLines(e.target.value) })} placeholder={'D:\\Docs\nC:\\Shared\\Contracts'} /></div>
          <div className="field"><label>Исключения (маски, по одной на строку)</label><textarea className="mono" value={lines(p.s.exclude)} onChange={e => p.set({ exclude: parseLines(e.target.value) })} /></div>
        </div>
        <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.useVss !== false} onChange={e => p.set({ useVss: e.target.checked })} /> Использовать снимок VSS (открытые файлы, базы)</label>
        <CommonBlocks {...p} />
      </>
    ),
  },
  'backup.1c.file': {
    title: 'Бэкап 1С (файловая)', hint: '1Cv8.1CD через VSS с проверкой заголовка базы', interval: 3600,
    defaults: { jobSlug: '', basePath: '', wholeFolder: false, fullEveryDays: 1, ...defaults },
    Form: p => (
      <>
        <Head {...p} />
        <div className="form-row">
          <div className="field" style={{ flex: 2 }}><label>Папка базы (где лежит 1Cv8.1CD)</label><input value={str(p.s.basePath)} onChange={e => p.set({ basePath: e.target.value })} placeholder="D:\Bases\Buh" /></div>
          <div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}><label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={!!p.s.wholeFolder} onChange={e => p.set({ wholeFolder: e.target.checked })} /> Вся папка базы, не только 1Cv8.1CD</label></div>
        </div>
        <CommonBlocks {...p} />
      </>
    ),
  },
  'backup.mssql': {
    title: 'Бэкап MS SQL', hint: 'BACKUP DATABASE полный/разностный, RESTORE VERIFYONLY, 1С на MS SQL', interval: 3600,
    defaults: { jobSlug: '', server: '.', databases: [], integratedSecurity: true, username: '', password: '', sqlBackupDir: '', fullEveryDays: 7, verify: true, compression: true, ...defaults },
    Form: p => (
      <>
        <Head {...p} />
        <div className="form-row">
          <div className="field"><label>Экземпляр SQL Server</label><input value={str(p.s.server, '.')} onChange={e => p.set({ server: e.target.value })} placeholder=".\SQLEXPRESS" /></div>
          <div className="field"><label>Базы (через запятую)</label><input value={list(p.s.databases).join(', ')} onChange={e => p.set({ databases: e.target.value.split(',').map(x => x.trim()).filter(Boolean) })} placeholder="buh, zup" /></div>
        </div>
        <div className="form-row">
          <div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}><label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.integratedSecurity !== false} onChange={e => p.set({ integratedSecurity: e.target.checked })} /> Windows-аутентификация (учётка службы агента)</label></div>
          {p.s.integratedSecurity === false && <><div className="field"><label>Логин SQL</label><input value={str(p.s.username)} onChange={e => p.set({ username: e.target.value })} /></div>
            <div className="field"><label>Пароль</label><input type="password" value={str(p.s.password)} onChange={e => p.set({ password: e.target.value })} /></div></>}
        </div>
        <div className="form-row">
          <div className="field" style={{ flex: 2 }}><label>Каталог, куда пишет SQL Server (пусто — папка назначения; для удалённого SQL — путь, видимый и ему, и агенту)</label><input value={str(p.s.sqlBackupDir)} onChange={e => p.set({ sqlBackupDir: e.target.value })} /></div>
          <div className="field" style={{ display: 'flex', alignItems: 'flex-end', gap: 14 }}>
            <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.verify !== false} onChange={e => p.set({ verify: e.target.checked })} /> VERIFYONLY</label>
            <label className="row" style={{ marginBottom: 8 }}><input type="checkbox" style={{ width: 'auto' }} checked={p.s.compression !== false} onChange={e => p.set({ compression: e.target.checked })} /> Сжатие</label>
          </div>
        </div>
        <CommonBlocks {...p} />
      </>
    ),
  },
  'backup.postgres': {
    title: 'Бэкап PostgreSQL', hint: 'pg_dump (custom-формат) + проверка pg_restore --list, 1С на PostgreSQL', interval: 3600,
    defaults: { jobSlug: '', host: 'localhost', port: 5432, databases: [], username: 'postgres', password: '', binDir: '', ...defaults },
    Form: p => (
      <>
        <div className="form-row">
          <div className="field"><label>Код задания</label><input value={str(p.s.jobSlug)} onChange={e => p.set({ jobSlug: e.target.value })} placeholder="pg-1c" /></div>
          <div className="field"><label>Хост</label><input value={str(p.s.host, 'localhost')} onChange={e => p.set({ host: e.target.value })} /></div>
          <div className="field" style={{ flex: '0 0 100px' }}><label>Порт</label><input type="number" value={num(p.s.port, 5432)} onChange={e => p.set({ port: +e.target.value })} /></div>
        </div>
        <div className="form-row">
          <div className="field"><label>Базы (через запятую)</label><input value={list(p.s.databases).join(', ')} onChange={e => p.set({ databases: e.target.value.split(',').map(x => x.trim()).filter(Boolean) })} /></div>
          <div className="field"><label>Пользователь</label><input value={str(p.s.username, 'postgres')} onChange={e => p.set({ username: e.target.value })} /></div>
          <div className="field"><label>Пароль</label><input type="password" value={str(p.s.password)} onChange={e => p.set({ password: e.target.value })} /></div>
        </div>
        <div className="field"><label>Папка bin PostgreSQL (пусто — найти автоматически)</label><input value={str(p.s.binDir)} onChange={e => p.set({ binDir: e.target.value })} placeholder="C:\Program Files\PostgreSQL\16\bin" /></div>
        <CommonBlocks {...p} />
      </>
    ),
  },
}
