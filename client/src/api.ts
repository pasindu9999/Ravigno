export type Branch = { id: number; name: string }
export type StockItem = { productId: number; productName: string; quantity: number }
export type BranchStock = { branchId: number; branchName: string; items: StockItem[] }
export type LocationRef = { id: number; name: string }
export type TransferLine = { productId: number; productName: string; quantity: number; receivedQuantity: number | null }
export type Transfer = {
  id: number
  from: LocationRef
  to: LocationRef
  status: 'Requested' | 'Dispatched' | 'Received' | 'Cancelled'
  idempotencyKey: string | null
  createdAt: string
  lines: TransferLine[]
}
export type CreateBody = {
  fromLocationId: number
  toLocationId: number
  lines: { productId: number; quantity: number }[]
}

// Thrown for any failed call. `retryable` is true for a thrown fetch or a 5xx, i.e. when the outcome is unknown.
export class ApiError extends Error {
  retryable: boolean
  constructor(message: string, retryable: boolean) {
    super(message)
    this.retryable = retryable
  }
}

// Turns a failed response into one readable string: errors map -> detail -> status text.
async function errorText(res: Response): Promise<string> {
  let body: unknown = null
  try {
    body = await res.json()
  } catch {
    // Empty or non-JSON body. A stopped API behind the Vite proxy usually lands here as a 500 with no body.
    return `Server unreachable or returned an unreadable response (HTTP ${res.status}${res.statusText ? ' ' + res.statusText : ''})`
  }
  const b = body as { errors?: Record<string, string[]>; detail?: string } | null
  if (b?.errors && typeof b.errors === 'object') {
    const messages = Object.values(b.errors).flat()
    if (messages.length > 0) return messages.join('; ')
  }
  if (b?.detail) return b.detail
  return `HTTP ${res.status} ${res.statusText}`.trim()
}

async function call(url: string, init?: RequestInit): Promise<Response> {
  let res: Response
  try {
    res = await fetch(url, init)
  } catch {
    throw new ApiError('Network error: could not reach the server', true)
  }
  if (!res.ok) throw new ApiError(await errorText(res), res.status >= 500)
  return res
}

export async function getBranches(): Promise<Branch[]> {
  return (await call('/api/branches')).json()
}

export async function getStock(branchId: number): Promise<BranchStock> {
  return (await call(`/api/branches/${branchId}/stock`)).json()
}

export async function getTransfers(status: string, branchId: string): Promise<Transfer[]> {
  const q = new URLSearchParams()
  if (status) q.set('status', status)
  if (branchId) q.set('branchId', branchId)
  return (await call(`/api/transfers?${q}`)).json()
}

// 201 = created, 200 = replay of an earlier request with the same key.
export async function createTransfer(body: CreateBody, key: string): Promise<{ status: number; transfer: Transfer }> {
  const res = await call('/api/transfers', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key },
    body: JSON.stringify(body),
  })
  return { status: res.status, transfer: await res.json() }
}

export type ReceiveBody = {
  reason?: string
  lines: { productId: number; receivedQuantity: number }[]
}

// `body` is only for a partial receive; without it the request has no body, so everything is received.
export async function act(id: number, action: 'dispatch' | 'receive' | 'cancel', body?: ReceiveBody): Promise<Transfer> {
  const init: RequestInit = { method: 'POST' }
  if (body) {
    init.headers = { 'Content-Type': 'application/json' }
    init.body = JSON.stringify(body)
  }
  return (await call(`/api/transfers/${id}/${action}`, init)).json()
}
