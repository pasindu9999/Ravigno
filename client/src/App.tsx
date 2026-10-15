import { useEffect, useState } from 'react'
import { getBranches, type Branch } from './api'
import RequestForm from './RequestForm'
import TransfersList from './TransfersList'

export default function App() {
  const [branches, setBranches] = useState<Branch[]>([])
  const [error, setError] = useState('')
  // Bumped after every create/dispatch/receive/cancel; the form re-reads stock and the list reloads.
  const [refreshKey, setRefreshKey] = useState(0)

  useEffect(() => {
    getBranches().then(setBranches, (e: Error) => setError(e.message))
  }, [])

  const onChanged = () => setRefreshKey((k) => k + 1)

  return (
    <div>
      <h1>Branch Stock Transfer</h1>
      {error && <p>Could not load branches: {error}</p>}
      <RequestForm branches={branches} refreshKey={refreshKey} onChanged={onChanged} />
      <TransfersList branches={branches} refreshKey={refreshKey} onChanged={onChanged} />
    </div>
  )
}
