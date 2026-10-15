import { useEffect, useRef, useState } from 'react'
import { act, getTransfers, type Branch, type Transfer } from './api'

type Props = { branches: Branch[]; refreshKey: number; onChanged: () => void }

const STATUSES = ['Requested', 'Dispatched', 'Received', 'Cancelled']

export default function TransfersList({ branches, refreshKey, onChanged }: Props) {
  const [status, setStatus] = useState('')
  const [branchId, setBranchId] = useState('')
  const [transfers, setTransfers] = useState<Transfer[]>([])
  const [listError, setListError] = useState('')
  const [rowErrors, setRowErrors] = useState<Record<number, string>>({})
  const [busyIds, setBusyIds] = useState<number[]>([])
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

  const run = async (id: number, action: 'dispatch' | 'receive' | 'cancel') => {
    if (busy.current.has(id)) return
    busy.current.add(id)
    setBusyIds([...busy.current])
    setRowErrors((r) => ({ ...r, [id]: '' }))
    try {
      await act(id, action)
    } catch (err) {
      setRowErrors((r) => ({ ...r, [id]: err instanceof Error ? err.message : 'Unexpected error' }))
    } finally {
      busy.current.delete(id)
      setBusyIds([...busy.current])
      onChanged() // also after a failure: the transfer's real state may differ from what the row showed
    }
  }

  return (
    <div>
      <h2>Transfers</h2>
      <p>
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
      </p>
      {listError && <p>Could not load transfers: {listError}</p>}
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
                <td>{t.status}</td>
                <td>{new Date(t.createdAt).toLocaleString()}</td>
                <td>{t.lines.map((l) => `${l.productName} x ${l.quantity}`).join(', ')}</td>
                <td>
                  {t.status === 'Requested' && (
                    <>
                      <button disabled={disabled} onClick={() => run(t.id, 'dispatch')}>Dispatch</button>{' '}
                      <button disabled={disabled} onClick={() => run(t.id, 'cancel')}>Cancel</button>
                    </>
                  )}
                  {t.status === 'Dispatched' && (
                    <button disabled={disabled} onClick={() => run(t.id, 'receive')}>Receive</button>
                  )}
                  {rowErrors[t.id] && <div>Error: {rowErrors[t.id]}</div>}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
