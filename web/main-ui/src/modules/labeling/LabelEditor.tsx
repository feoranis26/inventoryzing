import { useDeferredValue, useMemo, useRef, useState } from 'react'
import type { PointerEvent as ReactPointerEvent } from 'react'
import {
  Alert, Button, Checkbox, Group, Modal, NumberInput, Paper, Select, SimpleGrid, Stack,
  Table, Text, TextInput, Title,
} from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Braces, CopyPlus, Plus, Printer, Save, Send, Trash2 } from 'lucide-react'

import { api } from '../../api'
import type { PropertyDefinition } from '../../Properties'
import inventoryzingLogoUrl from '../../../../../assets/branding/inventoryzing.svg?url'
import type {
  LabelElement, LabelTemplate, MediaPreset, ObjectPage, PrinterMedia, PrintRequest, SaveLabelTemplate,
} from '../../api'

const kinds: { value: LabelElement['kind'], label: string }[] = [
  { value: 'qr', label: 'QR code' },
  { value: 'text', label: 'Text' },
  { value: 'field', label: 'Object field' },
  { value: 'site_branding', label: 'Site branding' },
  { value: 'inventoryzing_branding', label: 'inventoryzing branding' },
]

const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request failed.'
const sizeKey = (width: number, height: number, kind: string) => `${kind}:${width}:${height}`

function fromTemplate(template: LabelTemplate): SaveLabelTemplate {
  return {
    name: template.name,
    width_mm: template.width_mm,
    height_mm: template.height_mm,
    media_kind: template.media_kind,
    definition: structuredClone(template.definition),
    is_default: template.is_default,
  }
}

function blankTemplate(): SaveLabelTemplate {
  return {
    name: 'New label template', width_mm: 29, height_mm: 90, media_kind: 'die_cut',
    is_default: false, definition: { elements: [], orientation: 'normal' },
  }
}

function newElement(kind: LabelElement['kind'], count: number): LabelElement {
  const content = kind === 'qr' ? '{object.uuid}'
    : kind === 'field' ? '{object.name}'
      : kind === 'text' ? 'Static text' : ''
  return {
    id: `${kind.replaceAll('_', '-')}-${count + 1}`, kind, x_mm: 2, y_mm: 2,
    width_mm: kind === 'qr' ? 25 : 25, height_mm: kind === 'qr' ? 25 : 6,
    content, show_label: false, font_size_mm: 3, wrap_text: false, auto_fit: false,
    min_font_size_mm: 1, align: 'center', vertical_align: 'center',
  }
}

export function LabelEditor({ csrfToken, canManage }: {
  csrfToken: string, canManage: boolean,
}) {
  const cache = useQueryClient()
  const placeholders = useQuery({ queryKey: ['label-placeholders'],
    queryFn: () => api<PropertyDefinition[]>('/label-placeholders'), staleTime: Infinity })
  const templates = useQuery({ queryKey: ['label-templates'],
    queryFn: () => api<LabelTemplate[]>('/label-templates') })
  const presets = useQuery({ queryKey: ['label-media-presets'],
    queryFn: () => api<MediaPreset[]>('/label-media-presets'), staleTime: Infinity })
  const media = useQuery({ queryKey: ['printer-media'],
    queryFn: () => api<PrinterMedia>('/printers/default/media'), refetchInterval: 2000 })
  const [templateId, setTemplateId] = useState<string | null | undefined>()
  const [editedDraft, setDraft] = useState<SaveLabelTemplate | null>(null)
  const [selectedElement, setSelectedElement] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [publishing, setPublishing] = useState(false)
  const [printing, setPrinting] = useState(false)
  const [printJobId, setPrintJobId] = useState<string>()
  const [error, setError] = useState<unknown>()
  const [previewVersion, setPreviewVersion] = useState(0)
  const [placeholderReferenceOpen, setPlaceholderReferenceOpen] = useState(false)
  const [placeholderSearch, setPlaceholderSearch] = useState('')
  const drag = useRef<{
    pointerId: number, elementId: string, clientX: number, clientY: number,
    xMm: number, yMm: number,
  } | null>(null)
  const [objectSearch, setObjectSearch] = useState('')
  const [previewObjectId, setPreviewObjectId] = useState<string | null>(null)
  const deferredObjectSearch = useDeferredValue(objectSearch)
  const objects = useQuery({
    queryKey: ['label-preview-objects', deferredObjectSearch],
    queryFn: () => {
      const parameters = new URLSearchParams({ query: deferredObjectSearch, offset: '0' })
      return api<ObjectPage>(`/objects?${parameters}`)
    },
  })
  const printJob = useQuery({ queryKey: ['label-editor-print-request', printJobId],
    queryFn: ({ signal }) => api<PrintRequest>(`/print-requests/${printJobId}`, {
      signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]),
    }), enabled: !!printJobId, retry: false, refetchIntervalInBackground: true,
    refetchInterval: query => {
      const state = query.state.data?.state
      return !state || state === 'Queued' || state === 'Claimed' || state === 'Unknown' ? 250 : false
    } })

  const initialTemplate = templates.data?.find(template => template.is_default) ?? templates.data?.[0]
  const activeTemplate = templateId === undefined ? initialTemplate
    : templates.data?.find(template => template.id === templateId)
  const activeTemplateId = templateId === undefined ? activeTemplate?.id : templateId
  const draft = editedDraft ?? (activeTemplate ? fromTemplate(activeTemplate) : blankTemplate())

  const selected = draft.definition.elements.find(element => element.id === selectedElement)
  const installedPreset = media.data?.available && media.data.width_mm &&
    media.data.media_kind && media.data.media_kind !== 'unknown'
    ? sizeKey(media.data.width_mm, media.data.height_mm ?? 0, media.data.media_kind) : null
  const selectedPreset = presets.data?.find(preset =>
    preset.media_kind === draft.media_kind && preset.width_mm === draft.width_mm &&
    (preset.media_kind === 'continuous' || preset.height_mm === draft.height_mm))
  const presetOptions = useMemo(() => {
    const options = (presets.data ?? []).map(preset => ({
      value: preset.id, label: preset.name,
    }))
    const installedKnown = (presets.data ?? []).some(preset =>
      preset.media_kind === media.data?.media_kind && preset.width_mm === media.data?.width_mm &&
      (preset.media_kind === 'continuous' || preset.height_mm === media.data?.height_mm))
    if (installedPreset && !installedKnown) {
      options.push({ value: installedPreset,
        label: media.data?.media_kind === 'continuous'
          ? `Installed roll · ${media.data?.width_mm} mm continuous`
          : `Installed roll · ${media.data?.width_mm} × ${media.data?.height_mm} mm` })
    }
    return options
  }, [installedPreset, media.data?.height_mm, media.data?.media_kind,
    media.data?.width_mm, presets.data])

  function chooseTemplate(id: string | null) {
    setTemplateId(id)
    const template = templates.data?.find(candidate => candidate.id === id)
    setDraft(template ? fromTemplate(template) : blankTemplate())
    setSelectedElement(null)
    setError(undefined)
  }

  function updateElement(change: Partial<LabelElement>) {
    if (!selectedElement) return
    setDraft({
      ...draft,
      definition: { ...draft.definition, elements: draft.definition.elements.map(
        element => element.id === selectedElement ? { ...element, ...change } : element) },
    })
  }

  function addElement(kind: LabelElement['kind']) {
    const element = newElement(kind, draft.definition.elements.length)
    setDraft({ ...draft,
      definition: { ...draft.definition, elements: [...draft.definition.elements, element] } })
    setSelectedElement(element.id)
  }

  async function save(): Promise<LabelTemplate | null> {
    setSaving(true)
    setError(undefined)
    try {
      const saved = await api<LabelTemplate>(activeTemplateId ? `/label-templates/${activeTemplateId}` : '/label-templates', {
        method: activeTemplateId ? 'PUT' : 'POST', headers: { 'X-CSRF-Token': csrfToken },
        body: JSON.stringify(draft),
      })
      setTemplateId(saved.id)
      setDraft(fromTemplate(saved))
      await cache.invalidateQueries({ queryKey: ['label-templates'] })
      setPreviewVersion(version => version + 1)
      return saved
    } catch (failure) { setError(failure); return null } finally { setSaving(false) }
  }

  async function printPreview() {
    if (!previewObjectId || !activeTemplateId) return
    setPrinting(true)
    setError(undefined)
    setPrintJobId(undefined)
    try {
      let templateId = activeTemplateId
      if (editedDraft && canManage) {
        const saved = await save()
        if (!saved) return
        templateId = saved.id
      }
      const created = await api<PrintRequest>('/print-requests', { method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken },
        body: JSON.stringify({ object_id: previewObjectId, template_id: templateId, copies: 1 }) })
      setPrintJobId(created.id)
    } catch (failure) { setError(failure) } finally { setPrinting(false) }
  }

  async function publish() {
    if (!activeTemplateId) return
    setPublishing(true)
    setError(undefined)
    try {
      const published = await api<LabelTemplate>(`/label-templates/${activeTemplateId}/publish`, {
        method: 'POST', headers: { 'X-CSRF-Token': csrfToken },
      })
      setDraft(fromTemplate(published))
      await cache.invalidateQueries({ queryKey: ['label-templates'] })
    } catch (failure) { setError(failure) } finally { setPublishing(false) }
  }

  function startDrag(event: ReactPointerEvent<HTMLButtonElement>, element: LabelElement) {
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    setSelectedElement(element.id)
    drag.current = {
      pointerId: event.pointerId, elementId: element.id,
      clientX: event.clientX, clientY: event.clientY,
      xMm: element.x_mm, yMm: element.y_mm,
    }
  }

  function moveDrag(event: ReactPointerEvent<HTMLButtonElement>) {
    const active = drag.current
    if (!active || active.pointerId !== event.pointerId) return
    const element = draft.definition.elements.find(item => item.id === active.elementId)
    if (!element) return
    const round = (value: number) => Math.round(value * 10) / 10
    const xMm = round(Math.max(0, Math.min(
      draft.width_mm - element.width_mm,
      active.xMm + (event.clientX - active.clientX) / canvasScale,
    )))
    const yMm = round(Math.max(0, Math.min(
      draft.height_mm - element.height_mm,
      active.yMm + (event.clientY - active.clientY) / canvasScale,
    )))
    setDraft({ ...draft, definition: { ...draft.definition, elements: draft.definition.elements.map(
      item => item.id === active.elementId ? { ...item, x_mm: xMm, y_mm: yMm } : item) } })
  }

  function stopDrag(event: ReactPointerEvent<HTMLButtonElement>) {
    if (drag.current?.pointerId === event.pointerId) drag.current = null
  }

  const canvasScale = Math.min(300 / draft.width_mm, 620 / draft.height_mm)
  const canvasWidth = draft.width_mm * canvasScale
  const canvasHeight = draft.height_mm * canvasScale
  const rotated = draft.definition.orientation === 'rotated'
  const marginX = rotated ? (selectedPreset?.margin_y_mm ?? 0) : (selectedPreset?.margin_x_mm ?? 0)
  const marginY = rotated ? (selectedPreset?.margin_x_mm ?? 0) : (selectedPreset?.margin_y_mm ?? 0)
  const mediaMatches = !media.data?.available ||
    (media.data.width_mm === draft.width_mm &&
      (draft.media_kind === 'continuous' || media.data.height_mm === draft.height_mm) &&
      media.data.media_kind === draft.media_kind)
  const directPrintSupported = presets.data?.some(preset =>
    preset.width_mm === draft.width_mm &&
    (preset.media_kind === 'continuous' || preset.height_mm === draft.height_mm) &&
    preset.media_kind === draft.media_kind)
  return <section className="label-editor-page">
    <Group justify="space-between" mb="lg"><div><Text className="eyebrow">LABELING</Text>
      <Title order={1}>Label templates</Title></div>
      <Group><Button variant="default" leftSection={<Braces size={16} />}
        onClick={() => setPlaceholderReferenceOpen(true)}>Placeholders</Button>
        <Button variant="default" leftSection={<CopyPlus size={16} />} disabled={!canManage}
        onClick={() => { setTemplateId(null); setDraft(blankTemplate()); setSelectedElement(null) }}>
        New template</Button>
        <Button leftSection={<Save size={16} />} loading={saving} disabled={!canManage}
          onClick={() => void save()}>Save changes</Button>
        <Button variant="light" leftSection={<Send size={16} />} loading={publishing}
          disabled={!canManage || !activeTemplateId || !activeTemplate?.has_unpublished_changes}
          onClick={() => void publish()}>Publish revision</Button></Group></Group>
    {error != null && <Alert color="red" mb="md">{errorText(error)}</Alert>}
    <SimpleGrid cols={{ base: 1, lg: 3 }} spacing="lg">
      <Stack>
        <Select label="Template" value={activeTemplateId ?? null} onChange={chooseTemplate}
          data={(templates.data ?? []).map(template => ({ value: template.id,
            label: `${template.name}${template.is_default ? ' · default' : ''}` }))} />
        <TextInput label="Name" value={draft.name}
          onChange={event => setDraft({ ...draft, name: event.currentTarget.value })} />
        <Select label="Label size / roll preset"
          value={selectedPreset?.id ?? (installedPreset && draft.media_kind === media.data?.media_kind &&
            draft.width_mm === media.data?.width_mm ? installedPreset : null)}
          data={presetOptions} onChange={value => {
            if (!value) return
            const preset = presets.data?.find(candidate => candidate.id === value)
            if (preset) {
              setDraft({ ...draft, media_kind: preset.media_kind, width_mm: preset.width_mm,
                height_mm: preset.media_kind === 'continuous' ? draft.height_mm : preset.height_mm })
              return
            }
            const [kind, width, height] = value.split(':')
            setDraft({ ...draft, media_kind: kind as SaveLabelTemplate['media_kind'],
              width_mm: Number(width),
              height_mm: kind === 'continuous' ? draft.height_mm : Number(height) })
          }} />
        <Group grow><NumberInput label="Width (mm)" min={1} max={300} decimalScale={1}
          value={draft.width_mm} onChange={value => setDraft({ ...draft, width_mm: Number(value) })} />
          <NumberInput label={draft.media_kind === 'continuous' ? 'Cut length (mm)' : 'Height (mm)'}
            min={draft.media_kind === 'continuous' ? 12.7 : 1} max={1000} decimalScale={1}
            value={draft.height_mm} onChange={value => setDraft({ ...draft, height_mm: Number(value) })} /></Group>
        <Checkbox label="Default template" checked={draft.is_default}
          onChange={event => setDraft({ ...draft, is_default: event.currentTarget.checked })} />
        {media.data?.available ? <Alert color="teal" title="Currently installed roll">
          {media.data.media_kind === 'continuous' ? `${media.data.width_mm} mm continuous`
            : `${media.data.width_mm} × ${media.data.height_mm} mm`} · {media.data.state}
          <Text size="xs" mt={4}>{media.data.media_kind === 'continuous'
            ? 'Choose a cut length in the template; the roll has no fixed label length.'
            : 'Choosing it copies these dimensions into the template.'}</Text>
        </Alert> : <Alert color="gray">Current printer media is unavailable.</Alert>}
        <Text size="sm" fw={600}>Add object</Text>
        <Group gap="xs">{kinds.map(kind => <Button key={kind.value} size="xs" variant="light"
          leftSection={<Plus size={13} />} onClick={() => addElement(kind.value)}>{kind.label}</Button>)}</Group>
        {selectedPreset?.id === 'brother-dk-1221' && <Checkbox
          label="Rotate layout 90°"
          checked={rotated}
          onChange={event => setDraft({ ...draft, definition: {
            ...draft.definition, orientation: event.currentTarget.checked ? 'rotated' : 'normal',
          } })}
        />}
      </Stack>
      <Paper className="label-editor-canvas-wrap" withBorder p="md">
        <div className="label-editor-canvas" style={{ width: canvasWidth, height: canvasHeight }}>
          {draft.definition.elements.map(element => <button key={element.id} type="button"
            className={`label-editor-object${selectedElement === element.id ? ' selected' : ''}`}
            style={{ left: `${element.x_mm / draft.width_mm * 100}%`,
              top: `${element.y_mm / draft.height_mm * 100}%`,
              width: `${element.width_mm / draft.width_mm * 100}%`,
              height: `${element.height_mm / draft.height_mm * 100}%`,
              fontSize: `${Math.max(8, element.font_size_mm / draft.width_mm * canvasWidth)}px`,
              whiteSpace: element.wrap_text ? 'normal' : 'nowrap',
              overflowWrap: element.wrap_text ? 'anywhere' : 'normal',
              textOverflow: element.wrap_text ? 'clip' : 'ellipsis',
              alignItems: element.vertical_align === 'top' ? 'flex-start'
                : element.vertical_align === 'bottom' ? 'flex-end' : 'center' }}
            onPointerDown={event => startDrag(event, element)}
            onPointerMove={moveDrag} onPointerUp={stopDrag} onPointerCancel={stopDrag}
            onClick={() => setSelectedElement(element.id)}>
            {element.kind === 'qr' ? <span className="label-editor-qr">QR</span>
              : element.kind === 'site_branding' ? 'Site branding'
                : element.kind === 'inventoryzing_branding'
                  ? <img className="label-editor-brand" src={inventoryzingLogoUrl} alt="inventoryzing" />
                  : element.content}</button>)}
          <div aria-hidden="true" style={{ position: 'absolute', pointerEvents: 'none',
            inset: 0, zIndex: 2,
            borderLeft: `${marginX * canvasScale}px solid #e9ecefee`,
            borderRight: `${marginX * canvasScale}px solid #e9ecefee`,
            borderTop: `${marginY * canvasScale}px solid #e9ecefee`,
            borderBottom: `${marginY * canvasScale}px solid #e9ecefee` }} />
        </div>
        <Text size="xs" c="dimmed" ta="center" mt="sm">
          {draft.width_mm} × {draft.height_mm} mm · drag objects to position them
        </Text>
        {selectedPreset && <Text size="xs" c="dimmed" ta="center" mt="xs">
          Printable area: {(draft.width_mm - 2 * marginX).toFixed(1)} ×{' '}
          {(draft.height_mm - 2 * marginY).toFixed(1)} mm.
          Content in shaded margins will be clipped.
        </Text>}
        {printJob.error && !printJob.data && <Alert color="red" mt="sm">Print status unavailable: {errorText(printJob.error)}</Alert>}
        {printJob.data && <Alert color={printJob.data.state === 'Completed' ? 'green' :
          printJob.data.state === 'Rejected' || printJob.data.state === 'Reset' || printJob.data.state === 'Unknown' ? 'red' : 'blue'} mt="sm">
          Print test: {printJob.data.state === 'Completed' ? 'Printed' : printJob.data.state}
          {printJob.data.detail && <Text size="sm">{printJob.data.detail}</Text>}
        </Alert>}
        <Button mt="sm" data-shortcut-print leftSection={<Printer size={16} />} loading={printing}
          disabled={!previewObjectId || !activeTemplateId || !directPrintSupported || !mediaMatches}
          onClick={() => void printPreview()}>Print test label</Button>
      </Paper>
      <Stack>
        <Text fw={600}>Selected object</Text>
        {selected ? <>
          <TextInput label="Object ID" value={selected.id}
            onChange={event => {
              const id = event.currentTarget.value
              setDraft({
                ...draft,
                definition: { ...draft.definition, elements: draft.definition.elements.map(
                  element => element.id === selectedElement ? { ...element, id } : element) },
              })
              setSelectedElement(id)
            }} />
          <Select label="Kind" value={selected.kind} data={kinds}
            onChange={value => value && updateElement({ kind: value as LabelElement['kind'] })} />
          {selected.kind === 'field' ? <><Select label="Property" searchable value={selected.content}
            data={[
              ...(placeholders.data ?? []).map(field => ({ value: `{${field.key}}`,
                label: `${field.label} (${field.key})` })),
              ...((placeholders.data ?? []).some(field => `{${field.key}}` === selected.content) ? []
                : [{ value: selected.content, label: selected.content }]),
            ]} onChange={value => value && updateElement({ content: value })} />
            <TextInput label="Property expression" value={selected.content}
              onChange={event => updateElement({ content: event.currentTarget.value })} />
            <Checkbox label="Include property label" checked={selected.show_label ?? false}
              onChange={event => updateElement({ show_label: event.currentTarget.checked })} /></>
            : !['site_branding', 'inventoryzing_branding'].includes(selected.kind)
              ? <TextInput label={selected.kind === 'qr' ? 'QR payload' : 'Text'}
                value={selected.content} onChange={event => updateElement({ content: event.currentTarget.value })} /> : null}
          <SimpleGrid cols={2}><NumberInput label="X (mm)" min={0} decimalScale={1} value={selected.x_mm}
            onChange={value => updateElement({ x_mm: Number(value) })} />
            <NumberInput label="Y (mm)" min={0} decimalScale={1} value={selected.y_mm}
              onChange={value => updateElement({ y_mm: Number(value) })} />
            <NumberInput label="Width (mm)" min={0.5} decimalScale={1} value={selected.width_mm}
              onChange={value => updateElement({ width_mm: Number(value) })} />
            <NumberInput label="Height (mm)" min={0.5} decimalScale={1} value={selected.height_mm}
              onChange={value => updateElement({ height_mm: Number(value) })} /></SimpleGrid>
          {!['qr', 'inventoryzing_branding'].includes(selected.kind) && <><NumberInput label="Text size (mm)" min={1} max={30}
            decimalScale={1} value={selected.font_size_mm}
            onChange={value => updateElement({ font_size_mm: Number(value) })} />
            <Checkbox label="Wrap text" checked={selected.wrap_text ?? false}
              onChange={event => updateElement({ wrap_text: event.currentTarget.checked })} />
            <Checkbox label="Auto-fit font" checked={selected.auto_fit ?? false}
              onChange={event => updateElement({ auto_fit: event.currentTarget.checked })} />
            {selected.auto_fit && <NumberInput label="Minimum text size (mm)" min={0.8}
              max={selected.font_size_mm} decimalScale={1} value={selected.min_font_size_mm ?? 1}
              onChange={value => updateElement({ min_font_size_mm: Number(value) })} />}
            <Select label="Alignment" value={selected.align} data={['left', 'center', 'right']}
              onChange={value => value && updateElement({ align: value as LabelElement['align'] })} />
            <Select label="Vertical alignment" value={selected.vertical_align ?? 'center'}
              data={['top', 'center', 'bottom']}
              onChange={value => value && updateElement({
                vertical_align: value as LabelElement['vertical_align'],
              })} /></>}
          <Button color="red" variant="light" leftSection={<Trash2 size={15} />}
            onClick={() => { setDraft({ ...draft, definition: { ...draft.definition, elements: draft.definition.elements.filter(
              element => element.id !== selected.id) } }); setSelectedElement(null) }}>Remove object</Button>
        </> : <Text size="sm" c="dimmed">Select an object in the label preview.</Text>}
      </Stack>
    </SimpleGrid>
    <Paper withBorder p="md" mt="lg">
      <Group align="end" justify="space-between" mb="md">
        <Select<string> label="Preview with inventory object" placeholder="Choose an object" searchable clearable
          searchValue={objectSearch} onSearchChange={setObjectSearch}
          value={previewObjectId} onChange={setPreviewObjectId}
          data={(objects.data?.items ?? []).map(object => ({ value: object.id,
            label: `${object.alias ?? object.id.slice(-8)} · ${object.name}` }))}
          nothingFoundMessage={objects.isPending ? 'Loading objects…' : 'No matching objects'} />
        <Text size="xs" c="dimmed">
          Saved changes preview · published revision {activeTemplate?.revision ?? '—'}
        </Text>
      </Group>
      {activeTemplateId && previewObjectId ? <div className="label-editor-resolved-preview">
        <img src={`/api/label-templates/${activeTemplateId}/preview.svg?object_id=${previewObjectId}&v=${previewVersion}`}
          alt="Resolved label preview for selected inventory object" />
      </div> : <Text size="sm" c="dimmed" ta="center" py="xl">
        Choose an object to preview its label.
      </Text>}
    </Paper>
    <Modal opened={placeholderReferenceOpen} onClose={() => setPlaceholderReferenceOpen(false)}
      title="Label placeholders" size="xl">
      <TextInput label="Find a placeholder" value={placeholderSearch} mb="md"
        onChange={event => setPlaceholderSearch(event.currentTarget.value)} />
      {placeholders.isPending ? <Text size="sm">Loading placeholders…</Text>
        : placeholders.error ? <Alert color="red">{errorText(placeholders.error)}</Alert>
          : <Table.ScrollContainer minWidth={650}><Table verticalSpacing="sm">
            <Table.Thead><Table.Tr><Table.Th>Placeholder</Table.Th><Table.Th>Label</Table.Th>
              <Table.Th>Prefix</Table.Th><Table.Th>Type</Table.Th><Table.Th>Example</Table.Th>
            </Table.Tr></Table.Thead>
            <Table.Tbody>{placeholders.data.filter(field =>
              `${field.key} ${field.label} ${field.provider}`.toLowerCase()
                .includes(placeholderSearch.toLowerCase())).map(field => <Table.Tr key={field.key}>
              <Table.Td><code>{`{${field.key}}`}</code></Table.Td>
              <Table.Td>{field.label}{field.description &&
                <Text size="xs" c="dimmed">{field.description}</Text>}</Table.Td>
              <Table.Td><code>{field.provider}.*</code></Table.Td>
              <Table.Td>{field.type}</Table.Td><Table.Td>{field.example || '—'}</Table.Td>
            </Table.Tr>)}</Table.Tbody>
          </Table></Table.ScrollContainer>}
    </Modal>
  </section>
}
