import type { components } from './generated/api'

type Schemas = components['schemas']
export type Session = Schemas['SessionView']
export type InventoryObject = Schemas['ObjectView']
export type ObjectPage = Schemas['ObjectPage']
export type ObjectType = Schemas['TypeView']
export type Tag = Schemas['TagView']
export type EntityTags = Schemas['EntityTagsView']
export type HistoryEntry = Schemas['HistoryEntry']
export type Command = Schemas['Command']
export type Payload = Command['payload']
export type CommandResult = Schemas['CommandResult']
export type PrintRequest = Schemas['PrintRequestView']
export type LabelElement = Schemas['LabelElement']
export type LabelTemplate = Schemas['LabelTemplateView']
export type SaveLabelTemplate = Schemas['SaveLabelTemplate']
export type MediaPreset = Schemas['MediaPreset']
export type PrinterMedia = Schemas['PrinterMediaView']

export class ApiError extends Error {
  status: number
  code?: string

  constructor(status: number, message: string, code?: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

export async function api<Result>(path: string, options: RequestInit = {}): Promise<Result> {
  const response = await fetch(`/api${path}`, {
    ...options,
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...options.headers },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => ({}))
    const detail = typeof body.detail === 'string'
      ? body.detail
      : typeof body.detail?.message === 'string'
        ? body.detail.message
        : Array.isArray(body.detail)
          ? body.detail.map((item: { loc?: unknown[], msg?: string }) => {
            const field = Array.isArray(item.loc) ? item.loc.slice(1).join('.') : ''
            return `${field ? `${field}: ` : ''}${item.msg ?? 'Invalid value'}`
          }).join(' ')
        : 'The request could not be completed.'
    const code = typeof body.code === 'string' ? body.code : body.detail?.code
    throw new ApiError(response.status, detail, code)
  }
  return response.status === 204 ? undefined as Result : response.json()
}

export function makeCommand(session: Session, payload: Payload, authorityEpoch = 1): Command {
  return {
    authority_site: session.site_id,
    authority_epoch: authorityEpoch,
    command_epoch: session.command_epoch,
    command_id: crypto.randomUUID(),
    payload: structuredClone(payload),
  }
}

export function sendCommand(session: Session, command: Command) {
  return api<CommandResult>('/commands', {
    method: 'POST',
    headers: { 'X-CSRF-Token': session.csrf_token },
    body: JSON.stringify(command),
  })
}
