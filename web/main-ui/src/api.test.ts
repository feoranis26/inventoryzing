import { afterEach, expect, test, vi } from 'vitest'
import { ApiError, api, makeCommand, sendCommand } from './api'
import type { Session } from './api'

const session: Session = {
  account_id: '00000000-0000-4000-8000-000000000001', login: 'test', csrf_token: 'test-csrf',
  site_id: '00000000-0000-4000-8000-000000000002', site_name: 'Test', command_epoch: 1,
  permissions: ['inventory.create'],
}

afterEach(() => vi.unstubAllGlobals())

test('transport retry reuses the entire immutable command envelope', async () => {
  const payload = { kind: 'object.create' as const, name: 'Meter', description: '', relation: 'contained_in' as const, allocate_alias: true }
  const command = makeCommand(session, payload)
  payload.name = 'Changed in form'
  const fetch = vi.fn().mockRejectedValueOnce(new TypeError('Network lost'))
    .mockResolvedValueOnce(new Response(JSON.stringify({ command_id: command.command_id }), { status: 200 }))
  vi.stubGlobal('fetch', fetch)
  await expect(sendCommand(session, command)).rejects.toThrow('Network lost')
  await sendCommand(session, command)
  expect(fetch.mock.calls[0][1].body).toEqual(fetch.mock.calls[1][1].body)
  expect(JSON.parse(fetch.mock.calls[0][1].body).payload.name).toBe('Meter')
})

test('expired commands surface their error and are never rebound automatically', async () => {
  const command = makeCommand(session, { kind: 'object.create', name: 'Meter', description: '', relation: 'contained_in', allocate_alias: true })
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'COMMAND_EPOCH_EXPIRED', detail: 'Expired' }), { status: 409 }))
  vi.stubGlobal('fetch', fetch)
  await expect(sendCommand(session, command)).rejects.toMatchObject(new ApiError(409, 'Expired', 'COMMAND_EPOCH_EXPIRED'))
  expect(fetch).toHaveBeenCalledTimes(1)
})

test('structured printer-busy errors retain their useful message and code', async () => {
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: {
    code: 'printer_busy', message: 'The printer is already handling a label.',
  } }), { status: 409 }))
  vi.stubGlobal('fetch', fetch)
  await expect(api('/print-jobs')).rejects.toMatchObject(
    new ApiError(409, 'The printer is already handling a label.', 'printer_busy'),
  )
})
