import { useEffect, useRef, useState } from 'react'
import { act, getTransfers, type Branch, type ReceiveBody, type Transfer } from './api'

type Props = { branches: Branch[]; refreshKey: number; onChanged: () => void }
// Open receive form for one transfer: received quantity per productId (as typed) and an optional reason.
type ReceiveForm = { qty: Record<number, string>; reason: string }

const STATUSES = ['Requested', 'Dispatched', 'Received', 'Cancelled']

export default function TransfersList({ branches, refreshKey, onChanged }: Props) {
  const [status, setStatus] = useState('')
  const [branchId, setBranchId] = useState('')
  const [transfers, setTransfers] = useState<Transfer[]>([])
  const [listError, setListError] = useState('')
  const [rowErrors, setRowErrors] = useState<Record<number, string>>({})
  const [busyIds, setBusyIds] = useState<number[]>([])
  const [forms, setForms] = useState<Record<number, ReceiveForm | undefined>>({})
  // Ids with a request in flight, updated synchronously; busyIds only drives the disabled buttons after a re-render.
  const busy = useRef(new Set<number>())

  useEffect(() => {
    // Cleanup flag: ignore a response for a filter the user has already changed.
    let stale = false
    getTransfers(status, branchId).then(
      (t) => {
        if (stale) return
        setTransfers(t)
        setListError('')
      },
      (e: Error) => {
        if (!stale) setListError(e.message)
      },
    )
    return () => {
      stale = true
    }
  }, [status, branchId, refreshKey])

  const run = async (id: number, action: 'dispatch' | 'receive' | 'cancel', body?: ReceiveBody) => {
    if (busy.current.has(id)) return
    busy.current.add(id)
    setBusyIds([...busy.current])
    setRowErrors((r) => ({ ...r, [id]: '' }))
    try {
      await act(id, action, body)
      setForms((f) => ({ ...f, [id]: undefined })) // on failure the form stays open so the user can fix it
    } catch (err) {
      setRowErrors((r) => ({ ...r, [id]: err instanceof Error ? err.message : 'Unexpected error' }))
    } finally {
      busy.current.delete(id)
      setBusyIds([...busy.current])
      onChanged() // also after a failure: the transfer's real state may differ from what the row showed
    }
  }

  const openForm = (t: Transfer) =>
    setForms((f) => ({
      ...f,
      [t.id]: { qty: Object.fromEntries(t.lines.map((l) => [l.productId, String(l.quantity)])), reason: '' },
    }))

  const confirmReceive = (t: Transfer) => {
    const form = forms[t.id]
    if (!form) return
    const lines: { productId: number; receivedQuantity: number }[] = []
    for (const l of t.lines) {
      const raw = form.qty[l.productId] ?? ''
      const n = Number(raw)
      if (raw.trim() === '' || !Number.isInteger(n) || n < 0 || n > l.quantity) {
        setRowErrors((r) => ({ ...r, [t.id]: `${l.productName}: enter a whole number from 0 to ${l.quantity}` }))
        return
      }
      if (n !== l.quantity) lines.push({ productId: l.productId, receivedQuantity: n })
    }
    // Nothing changed = no body, exactly the old "receive everything" request.
    run(t.id, 'receive', lines.length > 0 ? { lines, reason: form.reason.trim() || undefined } : undefined)
  }

  return (
    <div>
      <h2>Transfers</h2>
      <div className="row">
        <label>
          Status{' '}
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </label>{' '}
        <label>
          Branch{' '}
          <select value={branchId} onChange={(e) => setBranchId(e.target.value)}>
            <option value="">All</option>
            {branches.map((b) => (
              <option key={b.id} value={b.id}>{b.name}</option>
            ))}
          </select>
        </label>
      </div>
      {listError && <p className="error" role="alert">Could not load transfers: {listError}</p>}
      <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Id</th><th>From</th><th>To</th><th>Status</th><th>Created</th><th>Lines</th><th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {transfers.map((t) => {
            const disabled = busyIds.includes(t.id)
            return (
              <tr key={t.id}>
                <td>{t.id}</td>
                <td>{t.from.name}</td>
                <td>{t.to.name}</td>
                <td><span className={`status status-${t.status.toLowerCase()}`}>{t.status}</span></td>
                <td>{new Date(t.createdAt).toLocaleString()}</td>
                <td>
                  <ul>
                    {t.lines.map((l, i) => (
                      <li key={i}>
                        {t.status === 'Received'
                          ? `${l.productName}: ${l.receivedQuantity ?? l.quantity} received of ${l.quantity}`
                          : `${l.productName} x ${l.quantity}`}
                      </li>
                    ))}
                  </ul>
                </td>
                <td className="actions">
                  <div className="buttons">
                    {t.status === 'Requested' && (
                      <>
                        <button disabled={disabled} onClick={() => run(t.id, 'dispatch')}>Dispatch</button>{' '}
                        <button disabled={disabled} onClick={() => run(t.id, 'cancel')}>Cancel</button>
                      </>
                    )}
                    {t.status === 'Dispatched' && !forms[t.id] && (
                      <button disabled={disabled} onClick={() => openForm(t)}>Receive</button>
                    )}
                  </div>
                  {t.status === 'Dispatched' && forms[t.id] && (
                    <div className="receive-form">
                      {t.lines.map((l) => (
                        <label key={l.productId}>
                          {l.productName} received (of {l.quantity})
                          <input
                            type="number"
                            min={0}
                            max={l.quantity}
                            step={1}
                            disabled={disabled}
                            value={forms[t.id]!.qty[l.productId] ?? ''}
                            onChange={(e) =>
                              setForms((f) => ({
                                ...f,
                                [t.id]: { ...f[t.id]!, qty: { ...f[t.id]!.qty, [l.productId]: e.target.value } },
                              }))
                            }
                          />
                        </label>
                      ))}
                      <label>
                        Reason (optional)
                        <input
                          type="text"
                          maxLength={500}
                          disabled={disabled}
                          value={forms[t.id]!.reason}
                          onChange={(e) => setForms((f) => ({ ...f, [t.id]: { ...f[t.id]!, reason: e.target.value } }))}
                        />
                      </label>
                      <div className="buttons">
                        <button className="primary" disabled={disabled} onClick={() => confirmReceive(t)}>Confirm receive</button>{' '}
                        <button disabled={disabled} onClick={() => setForms((f) => ({ ...f, [t.id]: undefined }))}>Back</button>
                      </div>
                    </div>
                  )}
                  {rowErrors[t.id] && <div className="error" role="alert">Error: {rowErrors[t.id]}</div>}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      </div>
      {transfers.length === 0 && !listError && <p className="empty">No transfers found.</p>}
    </div>
  )
}
