import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { QuickCreate } from './QuickCreate'
import { api, sendCommand } from '../../api'
import type { Session } from '../../api'

vi.mock('../../api', () => ({ api: vi.fn(), sendCommand: vi.fn(), makeCommand: (_session: unknown, payload: unknown) => payload }))
const session = { csrf_token: 'test', permissions: ['inventory.create', 'label.print'] } as Session
beforeEach(() => {
  window.history.replaceState(null, '', '/intake')
  localStorage.clear()
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener() {}, removeEventListener() {} } })
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
  vi.mocked(api).mockImplementation(async path => {
    if (path === '/label-templates') return [{ id: 'template', name: 'Workshop', is_default: true, width_mm: 62, height_mm: 29 }] as never
    if (path.startsWith('/objects?')) return { items: [] } as never
    if (path === '/print-requests') throw new Error('Printer offline')
    return [] as never
  })
  vi.mocked(sendCommand).mockResolvedValue({ entity_id: 'created-object' } as never)
})
afterEach(() => { cleanup(); vi.resetAllMocks(); vi.unstubAllGlobals() })
function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<MantineProvider><QueryClientProvider client={client}><QuickCreate session={session} /></QueryClientProvider></MantineProvider>)
}
test('no-label creation retains inputs and updates the preview without submitting a print', async () => {
  show()
  fireEvent.change(screen.getByLabelText('Name', { exact: false }), { target: { value: 'Meter' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add with no label' }))
  await screen.findByText('created without a label', { exact: false })
  expect(sendCommand).toHaveBeenCalledTimes(1)
  expect(vi.mocked(api).mock.calls.some(([path]) => path === '/print-requests')).toBe(false)
  expect((screen.getByLabelText('Name', { exact: false }) as HTMLInputElement).value).toBe('Meter')
  await waitFor(() => expect(screen.getByAltText('Label for Meter').getAttribute('src')).toContain('object_id=created-object'))
})
test('retry prints the existing object using its original template without creating another object', async () => {
  show()
  fireEvent.change(screen.getByLabelText('Name', { exact: false }), { target: { value: 'Meter' } })
  await waitFor(() => expect((screen.getByRole('button', { name: 'Add and print label' }) as HTMLButtonElement).disabled).toBe(false))
  fireEvent.click(screen.getByRole('button', { name: 'Add and print label' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Retry label' }))
  await waitFor(() => expect(vi.mocked(api).mock.calls.filter(([path]) => path === '/print-requests')).toHaveLength(2))
  expect(sendCommand).toHaveBeenCalledTimes(1)
  const prints = vi.mocked(api).mock.calls.filter(([path]) => path === '/print-requests')
  expect(prints[0][1]?.body).toBe(prints[1][1]?.body)
  expect(JSON.parse(prints[1][1]?.body as string)).toEqual({ object_id: 'created-object', template_id: 'template', copies: 1 })
})

test('creating without a label remains available while printing and preserves separate results', async () => {
  const original = vi.mocked(api).getMockImplementation()!
  let state = 'Claimed'
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (path === '/print-requests' || path === '/print-requests/request-1') return { id: 'request-1', state } as never
    return original(path, options)
  })
  vi.mocked(sendCommand).mockResolvedValueOnce({ entity_id: 'first' } as never)
    .mockResolvedValueOnce({ entity_id: 'second' } as never)
  show()
  fireEvent.change(screen.getByLabelText('Name', { exact: false }), { target: { value: 'First' } })
  const printButton = screen.getByRole('button', { name: 'Add and print label' }) as HTMLButtonElement
  await waitFor(() => expect(printButton.disabled).toBe(false))
  fireEvent.click(printButton)
  await screen.findByText('label requested', { exact: false })
  expect(printButton.disabled).toBe(true)
  fireEvent.change(screen.getByLabelText('Name', { exact: false }), { target: { value: 'Second' } })
  const noLabel = screen.getByRole('button', { name: 'Add with no label' }) as HTMLButtonElement
  await waitFor(() => expect(noLabel.disabled).toBe(false))
  fireEvent.click(noLabel)
  await screen.findByText('created without a label', { exact: false })
  state = 'Completed'
  await screen.findByText('label printed', { exact: false })
  expect(screen.getByText('created without a label', { exact: false })).toBeTruthy()
  expect(vi.mocked(api).mock.calls.filter(([path]) => path === '/print-requests')).toHaveLength(1)
})

test('navigation restores the draft and activity without creating or printing again', async () => {
  show()
  fireEvent.change(screen.getByLabelText('Name', { exact: false }), { target: { value: 'Saved meter' } })
  fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Workshop equipment' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add with no label' }))
  await screen.findByText('created without a label', { exact: false })
  cleanup()
  show()
  expect((screen.getByLabelText('Name', { exact: false }) as HTMLInputElement).value).toBe('Saved meter')
  expect((screen.getByLabelText('Description') as HTMLInputElement).value).toBe('Workshop equipment')
  expect(screen.getByText('created without a label', { exact: false })).toBeTruthy()
  expect(sendCommand).toHaveBeenCalledTimes(1)
  expect(vi.mocked(api).mock.calls.some(([path]) => path === '/print-requests')).toBe(false)
})

test('an expired saved print becomes retryable without automatically printing again', async () => {
  localStorage.setItem('inventoryzing.intake.v1.undefined.undefined.entries', JSON.stringify([
    { id: 'previous', name: 'Previous meter', templateId: 'template', requestId: 'expired', print: 'queued' },
  ]))
  const original = vi.mocked(api).getMockImplementation()!
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (path === '/print-requests/expired') throw new Error('Expired')
    return original(path, options)
  })
  show()
  await screen.findByRole('button', { name: 'Retry label' })
  expect(screen.getByText('Check for a label before retrying.', { exact: false })).toBeTruthy()
  expect(sendCommand).not.toHaveBeenCalled()
  expect(vi.mocked(api).mock.calls.some(([path]) => path === '/print-requests')).toBe(false)
})

test('copying an object fills the bulk draft and keeps local overrides without creating anything', async () => {
  const original = vi.mocked(api).getMockImplementation()!
  const quantity = { id: 'volume', label: 'Volume', editable: true, type: 'quantity', allowed_units: ['l', 'gal_us'], canonical_unit: 'l', local_state: 'value',
    value: { amount: '75.70823568', unit: 'l', display_amount: '20', display_unit: 'gal_us' } }
  const unset = { id: 'maker', label: 'Maker', editable: true, type: 'text', local_state: 'unset', value: null }
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (path === '/objects/source') return { id: 'source', name: 'Tote', description: 'Stackable', object_type_id: 'tote-type', parent_id: null, relation: 'mounted_in' } as never
    if (path === '/objects/source/tags') return { explicit_tag_ids: [] } as never
    if (path === '/objects/source/properties' || path === '/types/tote-type/properties') return [quantity, unset] as never
    if (path === '/types') return [{ id: 'tote-type', name: 'Tote type', abstract: false }] as never
    return original(path, options)
  })
  window.history.replaceState(null, '', '/intake/copy/source')
  show()
  await screen.findByText('Copied details from Tote.', { exact: false })
  expect(sendCommand).not.toHaveBeenCalled()
  expect((screen.getByLabelText('Name', { exact: false }) as HTMLInputElement).value).toBe('Tote')
  await waitFor(() => expect((screen.getByLabelText('Volume') as HTMLInputElement).value).toBe('20'))
  expect(window.location.pathname).toBe('/intake')
  fireEvent.click(screen.getByRole('button', { name: 'Add with no label' }))
  await waitFor(() => expect(sendCommand).toHaveBeenCalledTimes(1))
  expect(vi.mocked(sendCommand).mock.calls[0][1]).toMatchObject({ kind: 'object.create', name: 'Tote', description: 'Stackable', relation: 'mounted_in', object_type_id: 'tote-type',
    property_values: [{ property_id: 'volume', mode: 'value', value: { amount: '20', unit: 'gal_us' } }, { property_id: 'maker', mode: 'unset' }] })
})
