import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, auth } from '../api/client'

export function LoginPage() {
  const nav = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [challenge, setChallenge] = useState<string | null>(null)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true); setError(null)
    try {
      if (challenge) {
        const r = await api.totpVerify(challenge, code)
        auth.set(r.token!, r.user!)
        nav('/')
        return
      }
      const r = await api.login(email, password)
      if (r.totpChallenge) { setChallenge(r.totpChallenge); return }
      auth.set(r.token!, r.user!)
      nav('/')
    } catch {
      setError(challenge ? 'Неверный код' : 'Неверный email или пароль')
    } finally { setBusy(false) }
  }

  return (
    <div className="login">
      <form className="card" onSubmit={submit}>
        <h1>Sentinel</h1>
        {!challenge ? (
          <>
            <div className="field"><label>Email</label><input type="email" value={email} onChange={e => setEmail(e.target.value)} autoFocus required /></div>
            <div className="field"><label>Пароль</label><input type="password" value={password} onChange={e => setPassword(e.target.value)} required /></div>
          </>
        ) : (
          <div className="field">
            <label>Код из приложения-аутентификатора</label>
            <input inputMode="numeric" pattern="[0-9 ]*" autoComplete="one-time-code" value={code} onChange={e => setCode(e.target.value)} autoFocus required className="mono" style={{ fontSize: 20, letterSpacing: 4 }} />
          </div>
        )}
        <button type="submit" disabled={busy} style={{ width: '100%' }}>{busy ? 'Вход…' : challenge ? 'Подтвердить' : 'Войти'}</button>
        {challenge && <div className="small muted" style={{ marginTop: 8 }}><a href="#" onClick={e => { e.preventDefault(); setChallenge(null); setCode('') }}>← назад</a></div>}
        {error && <div className="error">{error}</div>}
      </form>
    </div>
  )
}
