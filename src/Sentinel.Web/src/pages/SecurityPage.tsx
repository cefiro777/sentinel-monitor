import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { api, auth } from '../api/client'

/** Настройка второго фактора (TOTP) для текущего пользователя. */
export function SecurityPage() {
  const user = auth.user
  const [setup, setSetup] = useState<{ secret: string; otpauthUrl: string; qr: string } | null>(null)
  const [code, setCode] = useState('')
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [enabled, setEnabled] = useState(user?.totpEnabled ?? false)
  const [mfa, setMfa] = useState(user?.mfa ?? false)

  useEffect(() => { api.me().then(u => { setEnabled(u.totpEnabled); setMfa(u.mfa); auth.set(auth.token!, u) }).catch(() => {}) }, [])

  async function startSetup() {
    setErr(null); setMsg(null)
    try {
      const s = await api.totpSetup()
      const qr = await QRCode.toDataURL(s.otpauthUrl, { width: 220, margin: 1, color: { dark: '#e6edf3', light: '#161c23' } })
      setSetup({ ...s, qr })
    } catch (e) { setErr((e as Error).message) }
  }

  async function enable() {
    setErr(null)
    try {
      const r = await api.totpEnable(code)
      auth.set(r.token!, r.user!)
      setEnabled(true); setMfa(true); setSetup(null); setCode('')
      setMsg('Двухфакторная аутентификация включена. Текущая сессия подтверждена.')
    } catch (e) { setErr((e as Error).message) }
  }

  async function disable() {
    setErr(null)
    try {
      const r = await api.totpDisable(code)
      auth.set(r.token!, r.user!)
      setEnabled(false); setMfa(false); setCode('')
      setMsg('2FA отключена.')
    } catch (e) { setErr((e as Error).message) }
  }

  return (
    <>
      <h1>Безопасность</h1>
      <div className="card" style={{ maxWidth: 640 }}>
        <h2 style={{ marginTop: 0 }}>Двухфакторная аутентификация</h2>
        <p className="muted small">
          Удалённые действия на серверах клиентов (команды, перезагрузка, скрипты) доступны только из сессии, подтверждённой кодом из приложения-аутентификатора
          (Google Authenticator, Яндекс.Ключ, Aegis, Microsoft Authenticator).
        </p>
        <dl className="kv" style={{ marginBottom: 14 }}>
          <dt>Состояние</dt><dd>{enabled ? <span className="badge ok">включена</span> : <span className="badge warning">не настроена</span>}</dd>
          <dt>Текущая сессия</dt><dd>{mfa ? <span className="badge ok">подтверждена</span> : <span className="badge unknown">без второго фактора</span>}</dd>
        </dl>

        {!enabled && !setup && <button onClick={startSetup}>Настроить 2FA</button>}

        {setup && (
          <div className="notice">
            <div className="row" style={{ alignItems: 'flex-start', gap: 20 }}>
              <img src={setup.qr} alt="QR" width={220} height={220} style={{ borderRadius: 8 }} />
              <div>
                <div><strong>1.</strong> Отсканируйте QR-код в приложении-аутентификаторе.</div>
                <div className="small muted" style={{ margin: '6px 0' }}>Или введите ключ вручную: <code>{setup.secret}</code></div>
                <div><strong>2.</strong> Введите код из приложения:</div>
                <div className="row" style={{ marginTop: 8 }}>
                  <input className="mono" style={{ width: 140, fontSize: 18, letterSpacing: 3 }} inputMode="numeric" value={code} onChange={e => setCode(e.target.value)} placeholder="000000" />
                  <button className="sm" disabled={code.replace(/\s/g, '').length !== 6} onClick={enable}>Включить</button>
                  <button className="secondary sm" onClick={() => { setSetup(null); setCode('') }}>Отмена</button>
                </div>
              </div>
            </div>
          </div>
        )}

        {enabled && (
          <div className="row" style={{ marginTop: 6 }}>
            <input className="mono" style={{ width: 140 }} inputMode="numeric" value={code} onChange={e => setCode(e.target.value)} placeholder="код" />
            <button className="secondary sm danger" disabled={code.replace(/\s/g, '').length !== 6} onClick={() => confirm('Отключить 2FA? Удалённые действия станут недоступны до повторной настройки.') && disable()}>Отключить 2FA</button>
          </div>
        )}

        {msg && <div className="small" style={{ marginTop: 10, color: 'var(--ok)' }}>{msg}</div>}
        {err && <div className="error">{err}</div>}
      </div>
    </>
  )
}
