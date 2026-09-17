import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Badge, Button, Group, Loader, Stack, Text, TextInput, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { Link, useLocation } from 'wouter'
import { Check, ScanLine, X } from 'lucide-react'
import { ApiError, api } from '../../api'
import type { InventoryObject } from '../../api'

type Outcome = 'success' | 'failure' | 'no_action'
type ScanResult = { sequence: number, event_id: string, outcome: Outcome, message: string,
  object_id?: string | null, object_name?: string | null, occurred_at: string }
type ScanSession = { id: string, terminal_id: string, mode: 'lookup' | 'move',
  destination_id?: string | null, destination_name?: string | null, expires_at: string,
  results: ScanResult[] }

export type ScannerStatus = { ready: boolean, error?: string, takeControl?: () => void }

function controllerId() {
  const key = 'inventoryzing.scanner.controller'
  const saved = sessionStorage.getItem(key)
  if (saved) return saved
  const created = crypto.randomUUID()
  sessionStorage.setItem(key, created)
  return created
}

export function ScannerWorkflow({ destinationId, csrfToken, visible, lookupTarget = 'object', onStatus }: {
  destinationId?: string, csrfToken: string, visible: boolean, lookupTarget?: 'object' | 'intake',
  onStatus?: (status: ScannerStatus) => void,
}) {
  const [, navigate] = useLocation()
  const destination = useQuery({
    queryKey: ['inventory', 'object', destinationId],
    queryFn: () => api<InventoryObject>(`/objects/${destinationId}`), enabled: !!destinationId,
  })
  const [session, setSession] = useState<ScanSession | null>(null)
  const [results, setResults] = useState<ScanResult[]>([])
  const [error, setError] = useState<unknown>()
  const [manual, setManual] = useState('')
  const [manualBusy, setManualBusy] = useState(false)
  const [takeOver, setTakeOver] = useState(false)
  const after = useRef(0)
  const controller = useRef(controllerId()).current
  const desired = destinationId ? `move:${destinationId}` : `lookup:${lookupTarget}`
  const started = useRef<string | null>(null)
  const headers = useMemo(() => ({ 'X-CSRF-Token': csrfToken, 'X-Scanner-Controller': controller }),
    [controller, csrfToken])
  useEffect(() => {
    onStatus?.({ ready: !!session && started.current === desired,
      error: error instanceof Error ? error.message : undefined,
      takeControl: error instanceof ApiError && error.status === 409
        ? () => { setError(undefined); setTakeOver(true) } : undefined })
  }, [session, desired, error, onStatus])

  useEffect(() => {
    if (destinationId && !destination.data) return
    if (started.current === desired && session) return
    let active = true
    void api<ScanSession>('/scanner/sessions', {
      method: 'POST', headers,
      body: JSON.stringify({ mode: destinationId ? 'move' : 'lookup', destination_id: destinationId ?? null,
        controller_id: controller, take_over: takeOver }),
    }).then(created => {
      if (!active) return
      started.current = desired
      after.current = 0
      setResults([])
      setSession(created)
      setError(undefined)
      setTakeOver(false)
    }).catch(failure => { if (active) setError(failure) })
    return () => { active = false }
  }, [controller, csrfToken, desired, destination.data, destinationId, session, takeOver])

  useEffect(() => {
    if (!session || started.current !== desired) return
    let stopped = false
    let polling = false
    const poll = async () => {
      if (stopped || polling) return
      polling = true
      try {
        const update = await api<ScanSession>(`/scanner/sessions/${session.id}?after=${after.current}`, { headers })
        if (stopped) return
        if (update.results.length) {
          after.current = Math.max(...update.results.map(result => result.sequence))
          setResults(current => [...update.results.slice().reverse(), ...current].slice(0, 100))
          const found = update.results.find(result => result.outcome === 'success' && result.object_id)
          if (!destinationId && found?.object_id) {
            if (lookupTarget === 'intake') stopped = true
            navigate(lookupTarget === 'intake'
              ? `/intake/copy/${found.object_id}` : `/objects/${found.object_id}`)
          }
        }
      } catch (failure) {
        if (!stopped) {
          started.current = null
          setSession(null)
          setError(failure)
        }
      } finally { polling = false }
    }
    void poll()
    const timer = window.setInterval(() => void poll(), 200)
    return () => { stopped = true; window.clearInterval(timer) }
  }, [destinationId, desired, headers, lookupTarget, navigate, session])

  async function submitManual(event: React.FormEvent) {
    event.preventDefault()
    if (!session || !manual.trim()) return
    setManualBusy(true)
    try {
      await api(`/scanner/sessions/${session.id}/input`, { method: 'POST', headers,
        body: JSON.stringify({ payload: manual.trim() }) })
      setManual('')
    } catch (failure) { setError(failure) } finally { setManualBusy(false) }
  }

  if (!visible) return null
  if (destinationId && destination.isPending) return <Loader aria-label="Loading destination" />
  if (destination.error) return <Alert color="red">{destination.error.message}</Alert>
  const controllerConflict = error instanceof ApiError && error.status === 409
  return <section>
    <Group justify="space-between" mb="lg"><div>
      <Text className="eyebrow">SCANNER TERMINAL</Text>
      <Title order={1}>{destinationId ? 'Move into here' : 'Scan lookup'}</Title>
    </div><Badge color={session ? 'green' : 'gray'}>{session ? 'Scanner ready' : 'Starting'}</Badge></Group>
    {destination.data && <Alert color="blue" mb="lg" title="Destination">
      <Text fw={600}>{destination.data.name}</Text>
      <Text size="sm">{[...destination.data.location_path.map(item => item.name), destination.data.name].join(' / ')}</Text>
    </Alert>}
    {error != null && <Alert color="red" mb="lg">{error instanceof Error ? error.message : 'Scanner workflow failed.'}
      {controllerConflict && <Button mt="sm" size="xs" color="red" variant="light"
        onClick={() => { setError(undefined); setTakeOver(true) }}>Take control of this terminal</Button>}
    </Alert>}
    <Stack>
      <Text>{destinationId
        ? 'Scan each object to move it here. Select Finish when done.'
        : 'Scan a label to open its object record.'}</Text>
      {session && <Text size="xs" c="dimmed">Scanner terminal ID: <code>{session.terminal_id}</code></Text>}
      <form onSubmit={submitManual}><Group align="end">
        <TextInput label="Manual input"
          leftSection={<ScanLine size={16} />} value={manual}
          onChange={event => setManual(event.currentTarget.value)} disabled={!session} />
        <Button type="submit" loading={manualBusy} disabled={!session || !manual.trim()}>Process</Button>
      </Group></form>
      {destinationId && <Group><Button component={Link} href={`/objects/${destinationId}`} variant="default">Finish</Button></Group>}
      {results.length > 0 && <Stack gap="xs" mt="md">{results.map(result =>
        <Alert key={result.event_id} color={result.outcome === 'success' ? 'green' : result.outcome === 'failure' ? 'red' : 'yellow'}
          icon={result.outcome === 'success' ? <Check size={17} /> : <X size={17} />}>
          {result.object_id ? <Link href={`/objects/${result.object_id}`}>{result.message}</Link> : result.message}
        </Alert>)}</Stack>}
    </Stack>
  </section>
}
