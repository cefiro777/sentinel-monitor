import { useEffect, useState } from 'react'

/** Ссылка СБП (Т-Банк): та же, что зашита в QR-код public/donate-qr.png. */
export const DONATE_URL = 'https://qr.nspk.ru/BS2A001IFAE405DS9SUBM5TAJBT3U0C4'

/**
 * Скромная ссылка «Поддержать проект» внизу меню. QR — в отдельном окне:
 * карточка банка вытянутая и в меню заняла бы половину высоты.
 */
export function DonateLink() {
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  return (
    <>
      <a href="#" className="donate-link" onClick={e => { e.preventDefault(); setOpen(true) }}>♥ Поддержать проект</a>
      {open && (
        <div className="modal-backdrop" onClick={() => setOpen(false)}>
          <div className="modal donate" role="dialog" aria-label="Поддержать проект" onClick={e => e.stopPropagation()}>
            <h2 style={{ marginTop: 0 }}>Поддержать проект</h2>
            <p className="muted">
              Sentinel бесплатен и развивается в свободное время. Если он экономит вам время и нервы — можно сказать спасибо
              любой суммой через СБП: отсканируйте код камерой телефона или откройте ссылку.
            </p>
            <img src="/donate-qr.png" alt="QR-код СБП для перевода" className="donate-qr" />
            <div className="row" style={{ justifyContent: 'space-between', marginTop: 12 }}>
              <a href={DONATE_URL} target="_blank" rel="noopener noreferrer">Открыть ссылку СБП</a>
              <button className="secondary sm" onClick={() => setOpen(false)}>Закрыть</button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
