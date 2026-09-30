import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { auth } from './api/client'
import { Layout } from './components/Layout'
import { LoginPage } from './pages/LoginPage'
import { OverviewPage } from './pages/OverviewPage'
import { HostPage } from './pages/HostPage'
import { TenantsPage } from './pages/TenantsPage'
import { TemplatesPage } from './pages/TemplatesPage'
import { EnrollPage } from './pages/EnrollPage'
import { UsersPage } from './pages/UsersPage'
import { IncidentPage, IncidentsPage } from './pages/IncidentsPage'
import { NotificationsPage } from './pages/NotificationsPage'
import { SecurityPage } from './pages/SecurityPage'
import { AuditPage } from './pages/AuditPage'
import { BackupsPage } from './pages/BackupsPage'
import { CctvPage } from './pages/CctvPage'
import './styles.css'

function RequireAuth({ children }: { children: React.ReactElement }) {
  return auth.token ? children : <Navigate to="/login" replace />
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<RequireAuth><Layout /></RequireAuth>}>
          <Route index element={<OverviewPage />} />
          <Route path="hosts/:id" element={<HostPage />} />
          <Route path="incidents" element={<IncidentsPage />} />
          <Route path="backups" element={<BackupsPage />} />
          <Route path="cctv" element={<CctvPage />} />
          <Route path="incidents/:id" element={<IncidentPage />} />
          <Route path="notifications" element={<NotificationsPage />} />
          <Route path="tenants" element={<TenantsPage />} />
          <Route path="templates" element={<TemplatesPage />} />
          <Route path="enroll" element={<EnrollPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="security" element={<SecurityPage />} />
          <Route path="audit" element={<AuditPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  </React.StrictMode>,
)
