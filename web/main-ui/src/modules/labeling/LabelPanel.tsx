import { useState } from 'react'
import { Alert, Button, Group, Select, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { Download, Printer, QrCode } from 'lucide-react'

import { api } from '../../api'
import type { InventoryObject, LabelTemplate, MediaPreset, PrinterMedia, PrintRequest } from '../../api'

const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request failed.'

const printStateLabel: Record<PrintRequest['state'], string> = {
  Queued: 'Waiting for printer',
  Claimed: 'Received',
  Unknown: 'Confirmation unavailable',
  Completed: 'Printed',
  Rejected: 'Could not print',
  Reset: 'Cleared',
}

export function LabelPanel({ object, csrfToken, disabled, allocate }: {
  object: InventoryObject, csrfToken: string, disabled: boolean, allocate: () => void,
}) {
  const templates = useQuery({ queryKey: ['label-templates'],
    queryFn: () => api<LabelTemplate[]>('/label-templates') })
  const media = useQuery({ queryKey: ['printer-media'],
    queryFn: () => api<PrinterMedia>('/printers/default/media'), refetchInterval: 2000 })
  const presets = useQuery({ queryKey: ['label-media-presets'],
    queryFn: () => api<MediaPreset[]>('/label-media-presets') })
  const [templateId, setTemplateId] = useState<string | null>(null)
  const [jobId, setJobId] = useState<string>()
  const [printError, setPrintError] = useState<unknown>()
  const [printing, setPrinting] = useState(false)
  const selectedTemplate = templates.data?.find(template => template.id === templateId)
    ?? templates.data?.find(template => template.is_default) ?? templates.data?.[0]
  const source = selectedTemplate
    ? `/api/label-templates/${selectedTemplate.id}/preview.svg?object_id=${object.id}` : ''
  const managedSource = selectedTemplate
    ? `/api/label-templates/${selectedTemplate.id}/preview.png?object_id=${object.id}` : ''
  const job = useQuery({ queryKey: ['print-request', jobId],
    queryFn: ({ signal }) => api<PrintRequest>(`/print-requests/${jobId}`, {
      signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]),
    }), enabled: !!jobId, retry: false,
    refetchIntervalInBackground: true,
    refetchInterval: query => {
      const state = query.state.data?.state
      return !state || state === 'Queued' || state === 'Claimed' || state === 'Unknown' ? 250 : false
    }, refetchOnWindowFocus: query => {
      const state = query.state.data?.state
      return !state || state === 'Queued' || state === 'Claimed' || state === 'Unknown'
    }, refetchOnReconnect: false })
  async function printTag() {
    setPrinting(true)
    setPrintError(undefined)
    try {
      const created = await api<PrintRequest>('/print-requests', { method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken },
        body: JSON.stringify({ object_id: object.id, template_id: selectedTemplate?.id, copies: 1 }) })
      setJobId(created.id)
    } catch (failure) { setPrintError(failure) } finally { setPrinting(false) }
  }
  async function forceReset() {
    setPrinting(true)
    setPrintError(undefined)
    try {
      await api('/printers/default/force-reset', { method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken } })
      setJobId(undefined)
    } catch (failure) { setPrintError(failure) } finally { setPrinting(false) }
  }
  const mediaMatches = !selectedTemplate || !media.data?.available ||
    (media.data.width_mm === selectedTemplate.width_mm &&
      (selectedTemplate.media_kind === 'continuous' ||
        media.data.height_mm === selectedTemplate.height_mm) &&
      media.data.media_kind === selectedTemplate.media_kind)
  const directPrintSupported = presets.data?.some(preset =>
    preset.width_mm === selectedTemplate?.width_mm &&
    (preset.media_kind === 'continuous' || preset.height_mm === selectedTemplate?.height_mm) &&
    preset.media_kind === selectedTemplate?.media_kind)
  return <section className="label-panel"><Group justify="space-between" mb="md"><Title order={2}>Label</Title><QrCode size={18} /></Group>
    <Select<string> label="Template" mb="sm" value={selectedTemplate?.id ?? null}
      onChange={value => setTemplateId(value)}
      data={(templates.data ?? []).map(template => ({ value: template.id,
        label: `${template.name}${template.is_default ? ' · default' : ''}` }))} />
    {selectedTemplate && <Text size="xs" fw={600} ta="center">
      {selectedTemplate.width_mm} × {selectedTemplate.height_mm} mm · revision {selectedTemplate.revision}
    </Text>}
    {source && <div className="managed-tag-preview"><img src={source}
      style={selectedTemplate ? { aspectRatio: `${selectedTemplate.width_mm} / ${selectedTemplate.height_mm}` } : undefined}
      alt={`Label preview for ${object.name}`} /></div>}
    {!mediaMatches && <Alert color="yellow" mb="sm">Installed roll: {media.data?.media_kind === 'continuous'
      ? `${media.data?.width_mm} mm continuous`
      : `${media.data?.width_mm} × ${media.data?.height_mm} mm`}.
      This template requires {selectedTemplate?.width_mm} × {selectedTemplate?.height_mm} mm.</Alert>}
    {presets.error && <Alert color="red" mb="sm">Could not load supported label sizes: {errorText(presets.error)}</Alert>}
    {selectedTemplate && presets.data && !directPrintSupported && <Alert color="blue" mb="sm">
      Preview and download are available. To print, choose a supported roll preset in the template editor
      that matches the installed roll. The template will not be resized automatically.
    </Alert>}
    {printError != null && <Alert color="red" mb="sm">{errorText(printError)}</Alert>}
    {job.error && !job.data && <Alert color="red" mb="sm" role="status">
      Print status unavailable: {errorText(job.error)} The last displayed state may be stale.
    </Alert>}
    {job.data && <Alert color={job.data.state === 'Completed' ? 'green' :
      job.data.state === 'Rejected' || job.data.state === 'Reset' || job.data.state === 'Unknown' ? 'red' : 'blue'} mb="sm" role="status">
      <Text fw={600}>Tag print: {printStateLabel[job.data.state]}</Text>
      {job.data.detail && <Text size="sm" mt={4}>{job.data.detail}</Text>}
    </Alert>}
    <Group justify="center" gap="xs"><Button component="a" href={source || undefined} download={`inventoryzing-${object.alias ?? object.id}.svg`} disabled={!source} variant="default" size="xs" leftSection={<Download size={14} />}>SVG</Button>
      <Button component="a" href={managedSource || undefined} download={`inventoryzing-${object.alias ?? object.id}.png`} disabled={!managedSource} variant="default" size="xs" leftSection={<Download size={14} />}>PNG</Button>
      <Button size="xs" data-shortcut-print leftSection={<Printer size={14} />} loading={printing}
        disabled={disabled || !selectedTemplate || !mediaMatches || !directPrintSupported}
        onClick={() => void printTag()}>
        {printError ? 'Retry print request' : 'Print tag'}</Button></Group>
    <Button fullWidth mt="xs" disabled={disabled}
      color="red" variant="light" size="xs" loading={printing} onClick={() => void forceReset()}>
      Clear printer status
    </Button>
    {!object.alias && <Button fullWidth mt="md" variant="light" disabled={disabled} onClick={allocate}>Allocate local ID</Button>}
  </section>
}
