import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ScannerWorkflow } from './ScannerWorkflows'
import { api } from '../../api'

vi.mock('../../api', async importOriginal => ({ ...await importOriginal<typeof import('../../api')>(), api: vi.fn() }))
beforeEach(() => { window.history.replaceState(null, '', '/intake/select'); sessionStorage.clear() })
afterEach(() => { cleanup(); vi.resetAllMocks() })

test.each(['intake', 'object'] as const)('routes a successful lookup to the active %s workflow', async target => {
  vi.mocked(api).mockImplementation(async path => {
    if (path === '/scanner/sessions') return { id: 'session' } as never
    return { id: 'session', results: [{ sequence: 1, event_id: 'event', outcome: 'success', object_id: 'source' }] } as never
  })
  const client = new QueryClient()
  render(<QueryClientProvider client={client}><ScannerWorkflow csrfToken="token" visible={false} lookupTarget={target} /></QueryClientProvider>)
  await waitFor(() => expect(window.location.pathname).toBe(target === 'intake' ? '/intake/copy/source' : '/objects/source'))
})

test('a late result from a closed selection workflow cannot overwrite normal navigation', async () => {
  let finishOld: ((value: never) => void) | undefined
  let sessions = 0
  vi.mocked(api).mockImplementation(async path => {
    if (path === '/scanner/sessions') return { id: `session-${++sessions}` } as never
    if (path.startsWith('/scanner/sessions/session-1?')) return new Promise(resolve => { finishOld = resolve })
    return { results: [] } as never
  })
  const client = new QueryClient()
  const view = render(<QueryClientProvider client={client}><ScannerWorkflow csrfToken="token" visible={false} lookupTarget="intake" /></QueryClientProvider>)
  await waitFor(() => expect(finishOld).toBeDefined())
  window.history.replaceState(null, '', '/intake')
  view.rerender(<QueryClientProvider client={client}><ScannerWorkflow csrfToken="token" visible={false} lookupTarget="object" /></QueryClientProvider>)
  finishOld!({ results: [{ sequence: 1, outcome: 'success', object_id: 'stale' }] } as never)
  await waitFor(() => expect(sessions).toBe(2))
  expect(window.location.pathname).toBe('/intake')
})
