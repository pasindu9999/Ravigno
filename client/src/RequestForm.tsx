import { useEffect, useRef, useState } from 'react'
import { ApiError, createTransfer, getStock, type Branch, type BranchStock, type CreateBody } from './api'

type Line = { productId: string; quantity: string }

type Props = { branches: Branch[]; refreshKey: number; onChanged: () => void }

export default function RequestForm({ branches, refreshKey, onChanged }: Props) {
  const [fromId, setFromId] = useState('')
  const [toId, setToId] = useState('')
  const [lines, setLines] = useState<Line[]>([])
  const [stock, setStock] = useState<BranchStock | null>(null)
  const [stockError, setStockError] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState('')

  // Idempotency key for the current submission attempt and the form content it was created for.
  const keyRef = useRef<string | null>(null)
  const keyForRef = useRef('')
  // Set synchronously on the first click, unlike `submitting`, which only changes after React re-renders.
  const inFlight = useRef(false)

  useEffect(() => {
    if (!fromId) return
    // Cleanup flag: if "from" changes (or the effect re-runs) before this response arrives, drop the stale response.
    let stale = false
    getStock(Number(fromId)).then(
      (s) => {
        if (stale) return
        setStock(s)
        setStockError('')
      },
      (e: Error) => {
        if (stale) return
        setStock(null)
        setStockError(e.message)
      },
    )
    return () => {
      stale = true
    }
  }, [fromId, refreshKey])

  const addLine = () => {
    if (stock && stock.items.length > 0)
      setLines([...lines, { productId: String(stock.items[0].productId), quantity: '' }])
  }
  const updateLine = (i: number, patch: Partial<Line>) =>
    setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)))
  const removeLine = (i: number) => setLines(lines.filter((_, j) => j !== i))

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (inFlight.current) return
    inFlight.current = true
    setSubmitting(true)
    setError('')
    setResult('')

    // Number(), not parseInt: "1.5" or "" must reach the API unchanged so its 400 message is shown.
    const body: CreateBody = {
      fromLocationId: Number(fromId),
      toLocationId: Number(toId),
      lines: lines.map((l) => ({ productId: Number(l.productId), quantity: Number(l.quantity) })),
    }

    // New key if none is held or the content changed since it was made; otherwise reuse it (a retry).
    const fingerprint = JSON.stringify(body)
    if (keyRef.current === null || keyForRef.current !== fingerprint) {
      keyRef.current = crypto.randomUUID()
      keyForRef.current = fingerprint
    }

    try {
      const { status, transfer } = await createTransfer(body, keyRef.current)
      keyRef.current = null // 2xx: done, the next submission is a new request
      setResult(status === 201 ? `Transfer #${transfer.id} created` : `Transfer #${transfer.id} already existed (same request replayed)`)
      setLines([])
      onChanged()
    } catch (err) {
      // Keep the key only when the outcome is unknown (thrown fetch or 5xx): the request may have committed,
      // so a retry must carry the same key. A 4xx is a definite answer, so the key is dropped.
      if (!(err instanceof ApiError && err.retryable)) keyRef.current = null
      setError(err instanceof Error ? err.message : 'Unexpected error')
    } finally {
      inFlight.current = false
      setSubmitting(false)
    }
  }

  return (
    <form onSubmit={submit} noValidate>
      <fieldset>
      <legend>Request a transfer</legend>
      <div className="row">
        <label>
          From{' '}
          <select value={fromId} onChange={(e) => setFromId(e.target.value)}>
            <option value="">-- choose --</option>
            {branches.filter((b) => String(b.id) !== toId).map((b) => (
              <option key={b.id} value={b.id}>{b.name}</option>
            ))}
          </select>
        </label>{' '}
        <label>
          To{' '}
          <select value={toId} onChange={(e) => setToId(e.target.value)}>
            <option value="">-- choose --</option>
            {branches.filter((b) => String(b.id) !== fromId).map((b) => (
              <option key={b.id} value={b.id}>{b.name}</option>
            ))}
          </select>
        </label>
      </div>

      {fromId && stockError && <p className="error" role="alert">Could not load stock: {stockError}</p>}
      {fromId && stock && (
        <div className="stock">
          <strong>Available at {stock.branchName} (advisory only, checked again on dispatch):</strong>
          <ul>
            {stock.items.map((i) => (
              <li key={i.productId}>{i.productName}: {i.quantity}</li>
            ))}
          </ul>
        </div>
      )}

      {lines.map((l, i) => (
        <div className="line-row" key={i}>
          <select aria-label="Product" value={l.productId} onChange={(e) => updateLine(i, { productId: e.target.value })}>
            {stock?.items.map((p) => (
              <option key={p.productId} value={p.productId}>{p.productName}</option>
            ))}
          </select>{' '}
          <input
            aria-label="Quantity"
            type="number"
            placeholder="quantity"
            value={l.quantity}
            onChange={(e) => updateLine(i, { quantity: e.target.value })}
          />{' '}
          <button type="button" onClick={() => removeLine(i)}>Remove</button>
        </div>
      ))}
      <div className="buttons">
        <button type="button" onClick={addLine} disabled={!stock}>Add line</button>{' '}
        <button type="submit" className="primary" disabled={submitting}>Submit</button>
      </div>
      </fieldset>

      {error && <p className="error" role="alert">Error: {error}</p>}
      {result && <p className="success">{result}</p>}
    </form>
  )
}
