import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { auth, type CheckStatus, statusLabel } from '../api/client'
import { DonateLink } from './Donate'

export function Layout() {
  const nav = useNavigate()
  const user = auth.user
  return (
    <div className="layout">
      <aside className="sidebar">
        <div className="brand"><span>●</span> Sentinel</div>
        <NavLink to="/" end className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Обзор</NavLink>
        <NavLink to="/incidents" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Инциденты</NavLink>
        <NavLink to="/backups" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Бэкапы</NavLink>
        <NavLink to="/cctv" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Видеонаблюдение</NavLink>
        <NavLink to="/tenants" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Клиенты</NavLink>
        {auth.canOperate() && <NavLink to="/templates" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Шаблоны</NavLink>}
        {auth.canOperate() && <NavLink to="/enroll" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Установка агентов</NavLink>}
        {auth.isAdmin() && <NavLink to="/notifications" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Оповещения</NavLink>}
        {auth.isAdmin() && <NavLink to="/users" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Пользователи</NavLink>}
        {auth.isAdmin() && <NavLink to="/audit" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Аудит</NavLink>}
        <NavLink to="/security" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>Безопасность{auth.canOperate() && !auth.hasMfa() && <span className="badge warning" style={{ marginLeft: 6 }}>2FA</span>}</NavLink>
        <div className="spacer" />
        <DonateLink />
        <div className="user">
          <div>{user?.displayName}</div>
          <div>{user?.email}</div>
          <div style={{ marginTop: 8 }}>
            <a href="#" onClick={e => { e.preventDefault(); auth.clear(); nav('/login') }}>Выйти</a>
          </div>
        </div>
      </aside>
      <main className="main"><Outlet /></main>
    </div>
  )
}

export function StatusDot({ status }: { status: CheckStatus }) {
  return <span className={'dot ' + status} title={statusLabel[status]} />
}

export function StatusBadge({ status }: { status: CheckStatus }) {
  return <span className={'badge ' + status}>{statusLabel[status]}</span>
}

/** Простейший поллинг: вызывает loader сразу и каждые intervalMs. */
import { useEffect, useState, useCallback } from 'react'
export function usePolling<T>(loader: () => Promise<T>, intervalMs: number, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const load = useCallback(loader, deps)
  const refresh = useCallback(() => {
    load().then(d => { setData(d); setError(null) }).catch(e => setError(e.message)).finally(() => setLoading(false))
  }, [load])
  useEffect(() => {
    refresh()
    const t = setInterval(refresh, intervalMs)
    return () => clearInterval(t)
  }, [refresh, intervalMs])
  return { data, error, loading, refresh }
}
