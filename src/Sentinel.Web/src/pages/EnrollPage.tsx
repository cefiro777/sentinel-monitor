import { useEffect, useRef, useState } from 'react'
import { api, auth, timeAgo, type EnrollmentTokenCreated, type Tenant } from '../api/client'
import { usePolling } from '../components/Layout'

export function EnrollPage() {
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [tenantId, setTenantId] = useState('')
  const [comment, setComment] = useState('')
  const [allowReboot, setAllowReboot] = useState(false)
  const [allowExec, setAllowExec] = useState(false)
  const [created, setCreated] = useState<EnrollmentTokenCreated | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const { data: tokens, refresh } = usePolling(() => api.enrollmentTokens(), 30000)
  const { data: pkg, refresh: refreshPkg } = usePolling(() => api.agentPackage(), 60000)
  const fileRef = useRef<HTMLInputElement>(null)
  const [uploadMsg, setUploadMsg] = useState<string | null>(null)

  useEffect(() => { api.tenants().then(t => { setTenants(t); if (t[0] && !tenantId) setTenantId(t[0].id) }) }, [])

  async function create() {
    setErr(null); setCreated(null)
    try { setCreated(await api.createEnrollmentToken(tenantId, comment || undefined)); refresh() }
    catch (e) { setErr((e as Error).message) }
  }

  async function upload() {
    const f = fileRef.current?.files?.[0]
    if (!f) return
    setUploadMsg('Загрузка…')
    try { const m = await api.uploadAgentPackage(f); setUploadMsg(`Опубликован пакет v${m.version}`); refreshPkg() }
    catch (e) { setUploadMsg((e as Error).message) }
  }

  const allow = 'ping,config.refresh,agent.update,service.*,backup.*,cctv.*' + (allowReboot ? ',reboot,shutdown' : '') + (allowExec ? ',exec' : '')
  const server = created?.serverUrl ?? ''
  const pinArg = created?.certificatePin ? ` -Pin ${created.certificatePin}` : ''
  const pinCli = created?.certificatePin ? ` --pin ${created.certificatePin}` : ''
  const tenantName = (id: string) => tenants.find(t => t.id === id)?.name ?? id

  return (
    <>
      <h1>Установка агентов</h1>
      <div className="card" style={{ maxWidth: 820 }}>
        <h2 style={{ marginTop: 0 }}>1. Токен регистрации</h2>
        <p className="muted small">Токен одноразовый, действует 24 часа. Агент предъявляет его при регистрации и получает постоянный ключ.</p>
        <div className="form-row">
          <div className="field"><label>Клиент</label>
            <select value={tenantId} onChange={e => setTenantId(e.target.value)}>{tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
          </div>
          <div className="field"><label>Комментарий</label><input value={comment} onChange={e => setComment(e.target.value)} placeholder="сервер 1С в офисе" /></div>
        </div>
        <div className="field row" style={{ gap: 20 }}>
          <label className="row"><input type="checkbox" style={{ width: 'auto' }} checked={allowReboot} onChange={e => setAllowReboot(e.target.checked)} /> Разрешить удалённую перезагрузку/выключение</label>
          <label className="row"><input type="checkbox" style={{ width: 'auto' }} checked={allowExec} onChange={e => setAllowExec(e.target.checked)} /> Разрешить выполнение скриптов (exec)</label>
        </div>
        <button disabled={!tenantId} onClick={create}>Создать токен</button>
        {err && <div className="error">{err}</div>}

        {created && (
          <div className="notice">
            <strong>2. На сервере клиента — вставить в PowerShell от администратора (одной строкой):</strong>
            <pre className="cmd">{`[Net.ServicePointManager]::SecurityProtocol = 3072; (New-Object Net.WebClient).DownloadFile('${server}/agent/install.ps1', "$env:TEMP\\si.ps1"); powershell -ExecutionPolicy Bypass -File "$env:TEMP\\si.ps1" -Token ${created.token} -Allow '${allow}'${pinArg}`}</pre>
            <details>
              <summary>Вручную (Server 2008 R2 без TLS 1.2 в PowerShell 2.0; сервер не открывается с этого хоста)</summary>
              <div className="small" style={{ marginTop: 6 }}>Скачайте <a href={`${server}/agent/SentinelAgent.zip`}>SentinelAgent.zip</a>{pkg && <> (опубликованная версия <strong>{pkg.version}</strong>, {(pkg.sizeBytes / 1048576).toFixed(1)} МБ — та же, что ставит команда выше и на которую обновляются агенты)</>}, распакуйте в <code>C:\Program Files\Sentinel\Agent</code> (или любую другую папку) и выполните в PowerShell от администратора — кавычки и <code>.\</code> обязательны:</div>
              <pre className="cmd">{`cd "C:\\Program Files\\Sentinel\\Agent"\n.\\SentinelAgent.exe enroll --server ${server} --token ${created.token} --allow ${allow}${pinCli}\n.\\SentinelAgent.exe install`}</pre>
            </details>
            <details>
              <summary>Офлайн-установщик (нет интернета, старый сервер без .NET 4.8, массовая установка через GPO/RMM)</summary>
              <div className="small" style={{ marginTop: 6 }}>
                Скачайте <strong>SentinelAgent-Setup-x.y.z.exe</strong> со страницы{' '}
                <a href="https://github.com/cefiro777/sentinel-monitor/releases/latest" target="_blank" rel="noopener noreferrer">релизов</a>{' '}
                — внутри агент и .NET Framework 4.8. Запустите и введите адрес и токен, либо без вопросов:
              </div>
              <pre className="cmd">{`SentinelAgent-Setup.exe /VERYSILENT /SERVER=${server} /TOKEN=${created.token}${created.certificatePin ? ` /PIN=${created.certificatePin}` : ''} /ALLOW=${allow}`}</pre>
            </details>
            {created.certificatePin && <div className="small muted">Сервер работает без домена: агент доверяет сертификату по pin (передаётся в команде). Браузер будет предупреждать о сертификате — это ожидаемо.</div>}
            <div className="small muted">Если команда падает на «Невозможно соединиться с удалённым сервером», а сервер мониторинга стоит в той же локальной сети — роутер не разворачивает обращение к своему внешнему адресу (нет NAT loopback).
              Пропишите на этом хосте в <code>%SystemRoot%\System32\drivers\etc\hosts</code> строку <code className="mono">{'<локальный IP сервера мониторинга>'} {new URL(server || 'https://example').hostname}</code> — и команда заработает.</div>
            <div className="small muted">Токен показывается один раз. Белый список команд (--allow) хранится только на агенте и с сервера не меняется.{!pkg && <strong style={{ color: 'var(--warning)' }}> Пакет агента ещё не опубликован — загрузите его ниже.</strong>}</div>
          </div>
        )}
      </div>

      {auth.isAdmin() && (
        <div className="card" style={{ maxWidth: 820, marginTop: 16 }}>
          <h2 style={{ marginTop: 0 }}>Дистрибутив агента</h2>
          {pkg ? <div>Опубликован <strong>v{pkg.version}</strong> · {(pkg.sizeBytes / 1048576).toFixed(1)} МБ · {timeAgo(pkg.publishedAt)} · sha256 <code className="small">{pkg.sha256.slice(0, 16)}…</code></div>
               : <div className="muted">Пакет не опубликован.</div>}
          <p className="muted small">Возьмите <code>SentinelAgent-x.y.z.zip</code> со страницы <a href="https://github.com/cefiro777/sentinel-monitor/releases/latest" target="_blank" rel="noopener noreferrer">релизов</a> (или соберите: <code>.\deploy\build-agent.ps1 -Version X.Y.Z</code>) и загрузите. Сервер подпишет его своим ключом; агенты проверяют подпись и обновляются сами раз в сутки (или по команде «Обновить агент»).</p>
          <div className="row"><input type="file" ref={fileRef} accept=".zip" style={{ width: 'auto' }} /><button className="sm" onClick={upload}>Опубликовать</button></div>
          {uploadMsg && <div className="small" style={{ marginTop: 8 }}>{uploadMsg}</div>}
        </div>
      )}

      <h2>Выданные токены</h2>
      <div className="card" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Клиент</th><th>Комментарий</th><th>Истекает</th><th>Статус</th><th></th></tr></thead>
          <tbody>
            {(tokens ?? []).map(t => {
              const expired = new Date(t.expiresAt) < new Date()
              return (
                <tr key={t.id}>
                  <td>{tenantName(t.tenantId)}</td>
                  <td className="muted">{t.comment}</td>
                  <td className="muted" title={t.expiresAt}>{new Date(t.expiresAt).toLocaleString('ru')}</td>
                  <td>{t.usedAt ? <span className="badge ok">использован {timeAgo(t.usedAt)}</span> : expired ? <span className="badge unknown">истёк</span> : <span className="badge neutral">активен</span>}</td>
                  <td>{!t.usedAt && !expired && <button className="secondary sm" onClick={() => api.revokeEnrollmentToken(t.id).then(refresh)}>Отозвать</button>}</td>
                </tr>
              )
            })}
            {tokens && tokens.length === 0 && <tr><td colSpan={5} className="muted">Токенов ещё не выдавали</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}
