/** Мини-график без библиотек: ломаная по точкам [время, значение]. */
export function Sparkline({ points, width = 64, height = 18, color = 'var(--accent)' }: { points: [number, number][]; width?: number; height?: number; color?: string }) {
  if (points.length < 2) return null
  const xs = points.map(p => p[0]), ys = points.map(p => p[1])
  const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys)
  const sx = (x: number) => (x1 === x0 ? 0 : ((x - x0) / (x1 - x0)) * (width - 2) + 1)
  const sy = (y: number) => (y1 === y0 ? height / 2 : height - 2 - ((y - y0) / (y1 - y0)) * (height - 4))
  const d = points.map(p => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(' ')
  return <svg width={width} height={height} aria-hidden="true" style={{ display: 'block' }}><polyline points={d} fill="none" stroke={color} strokeWidth={1.5} /></svg>
}

/** График для панели деталей: линия, минимум/максимум по оси, время начала и конца. */
export function LineChart({ points, unit, height = 120, color = 'var(--accent)' }: { points: [number, number][]; unit?: string; height?: number; color?: string }) {
  if (points.length < 2) return <div className="muted small">Недостаточно данных для графика.</div>
  const width = 560, pad = 36
  const xs = points.map(p => p[0]), ys = points.map(p => p[1])
  const x0 = Math.min(...xs), x1 = Math.max(...xs)
  let y0 = Math.min(...ys), y1 = Math.max(...ys)
  if (y1 === y0) { y0 -= 1; y1 += 1 }
  const sx = (x: number) => pad + ((x - x0) / (x1 - x0 || 1)) * (width - pad - 6)
  const sy = (y: number) => 6 + (1 - (y - y0) / (y1 - y0)) * (height - 24)
  const d = points.map(p => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(' ')
  const fmt = (v: number) => (Math.abs(v) >= 1000 ? Math.round(v).toLocaleString('ru') : +v.toFixed(1)) + (unit ?? '')
  const t = (ms: number) => new Date(ms).toLocaleTimeString('ru', { hour: '2-digit', minute: '2-digit' })
  return (
    <svg viewBox={`0 0 ${width} ${height}`} style={{ width: '100%', maxWidth: width, height }} role="img" aria-label="График за период">
      <line x1={pad} y1={sy(y1)} x2={width - 6} y2={sy(y1)} stroke="var(--border)" strokeWidth={0.5} />
      <line x1={pad} y1={sy(y0)} x2={width - 6} y2={sy(y0)} stroke="var(--border)" strokeWidth={0.5} />
      <text x={pad - 4} y={sy(y1) + 4} textAnchor="end" fontSize={10} fill="var(--text-muted)">{fmt(y1)}</text>
      <text x={pad - 4} y={sy(y0) + 4} textAnchor="end" fontSize={10} fill="var(--text-muted)">{fmt(y0)}</text>
      <polyline points={d} fill="none" stroke={color} strokeWidth={1.5} />
      <text x={pad} y={height - 2} fontSize={10} fill="var(--text-muted)">{t(x0)}</text>
      <text x={width - 6} y={height - 2} textAnchor="end" fontSize={10} fill="var(--text-muted)">{t(x1)}</text>
    </svg>
  )
}
