import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Divider, Group, Modal, MultiSelect, Select, Stack, Text,
  Textarea, TextInput, Title } from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { api, makeCommand, sendCommand } from '../../api'
import type { InventoryObject, LabelTemplate, ObjectType, PrintRequest, Session, Tag } from '../../api'
import type { PropertyValue } from '../../Properties'
import { IntakeLabelPreview } from '../labeling/IntakeLabelPreview'
import { useIntakeState } from './useIntakeState'
import { useLocation } from 'wouter'
import { loadIntakeCopy } from './copyObject'
import { IntakeSourcePicker } from './IntakeSourcePicker'
import type { ScannerStatus } from '../scanning/ScannerWorkflows'

type IntakeEntry = { id: string, name: string, templateId?: string, requestId?: string, print: 'not-requested' | 'queued' | 'printed' | 'failed', detail?: string }
type Creator = 'tag' | 'type' | 'property' | null
type Placement = 'contained_in' | 'located_in' | 'installed_in' | 'mounted_in'

const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request could not be completed.'
const placementOptions = [
  { value: 'contained_in', label: 'Contained in' }, { value: 'located_in', label: 'Located in' },
  { value: 'installed_in', label: 'Installed in' }, { value: 'mounted_in', label: 'Mounted in' },
]

export function QuickCreate({ session, scannerStatus }: { session: Session, scannerStatus?: ScannerStatus }) {
  const cache = useQueryClient()
  const [location, navigate] = useLocation()
  const copyId = /^\/intake\/copy\/([^/]+)$/.exec(location)?.[1]
  const [copying, setCopying] = useState(false)
  const [copiedName, setCopiedName] = useState('')
  const scope = `${session.site_id}.${session.account_id}`
  const [name, setName] = useIntakeState(scope, 'name', '')
  const [description, setDescription] = useIntakeState(scope, 'description', '')
  const [typeId, setTypeId] = useIntakeState<string | null>(scope, 'type', null)
  const [parentId, setParentId] = useIntakeState<string | null>(scope, 'parent', null)
  const [relation, setRelation] = useIntakeState<Placement>(scope, 'relation', 'contained_in')
  const [tagIds, setTagIds] = useIntakeState<string[]>(scope, 'tags', [])
  const [allocateAlias, setAllocateAlias] = useIntakeState(scope, 'allocateAlias', true)
  const [stockHolding, setStockHolding] = useIntakeState(scope, 'stockHolding', false)
  const [stockAmount, setStockAmount] = useIntakeState(scope, 'stockAmount', '')
  const [stockUnit, setStockUnit] = useIntakeState(scope, 'stockUnit', '')
  const [templateId, setTemplateId] = useIntakeState<string | null>(scope, 'template', null)
  const [values, setValues] = useIntakeState<Record<string, unknown>>(scope, 'values', {})
  const [entries, setEntries] = useIntakeState<IntakeEntry[]>(scope, 'entries', [])
  const [printRequestId, setPrintRequestId] = useState<string>(() => entries.find(entry => entry.print === 'queued')?.requestId)
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const submitting = useRef(false)
  const printing = useRef(entries.some(entry => entry.print === 'queued' && entry.requestId))
  useEffect(() => {
    setEntries(current => current.map(entry => entry.print === 'queued' && !entry.requestId
      ? { ...entry, print: 'failed', detail: 'Print status unavailable. Check for a label before retrying.' } : entry))
  }, [setEntries])
  const [creator, setCreator] = useState<Creator>(null)
  useEffect(() => {
    if (!copyId) return
    const controller = new AbortController()
    setCopying(true)
    setError(undefined)
    void loadIntakeCopy(copyId, controller.signal).then(copy => {
      if (controller.signal.aborted) return
      setName(copy.object.name)
      setDescription(copy.object.description)
      setTypeId(copy.object.object_type_id ?? null)
      setParentId(copy.object.parent_id ?? null)
      setRelation(copy.object.relation as Placement)
      setTagIds(copy.tagIds)
      setValues(copy.values)
      setCopiedName(copy.object.name)
      setCopying(false)
      navigate('/intake', { replace: true })
    }).catch(failure => { if (!controller.signal.aborted) setError(failure) })
      .finally(() => { if (!controller.signal.aborted) setCopying(false) })
    return () => controller.abort()
  }, [copyId, navigate, setName, setDescription, setTypeId, setParentId, setRelation, setTagIds, setValues])
  const types = useQuery({ queryKey: ['inventory', 'types'], queryFn: () => api<ObjectType[]>('/types') })
  const tags = useQuery({ queryKey: ['inventory', 'tags'], queryFn: () => api<Tag[]>('/tags') })
  const parents = useQuery({ queryKey: ['inventory', 'objects', 'quick-create'],
    queryFn: () => api<{ items: InventoryObject[] }>('/objects?limit=200') })
  const selectedParent = useQuery({ queryKey: ['inventory', 'object', parentId],
    queryFn: () => api<InventoryObject>(`/objects/${parentId}`), enabled: !!parentId })
  const parentOptions = new Map((parents.data?.items ?? []).map(item => [item.id, item]))
  if (selectedParent.data) parentOptions.set(selectedParent.data.id, selectedParent.data)
  const templates = useQuery({ queryKey: ['label-templates'], queryFn: () => api<LabelTemplate[]>('/label-templates') })
  const properties = useQuery({ queryKey: ['inventory', 'type-properties', typeId],
    queryFn: () => api<PropertyValue[]>(`/types/${typeId}/properties`), enabled: !!typeId })
  const stockPolicy = useQuery({ queryKey: ['stock-policy', typeId],
    queryFn: () => api<{ canonical_unit: string, quantity_dimension: string }>(`/types/${typeId}/stock-policy`),
    enabled: stockHolding && !!typeId, retry: false })
  useEffect(() => { if (stockPolicy.data && !stockUnit) setStockUnit(stockPolicy.data.canonical_unit) }, [stockPolicy.data, stockUnit, setStockUnit])
  const selectedTemplate = templates.data?.find(template => template.id === templateId)
    ?? templates.data?.find(template => template.is_default) ?? templates.data?.[0]
  const propertyValues = useMemo(() => properties.data?.filter(field => field.editable && field.id) ?? [], [properties.data])
  const propertiesUnavailable = !!typeId && (properties.isPending || !!properties.error)
  const printRequest = useQuery({ queryKey: ['quick-create-print-request', printRequestId],
    queryFn: ({ signal }) => api<PrintRequest>(`/print-requests/${printRequestId}`, {
      signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]),
    }), enabled: !!printRequestId, retry: false, refetchIntervalInBackground: true,
    refetchOnWindowFocus: false, refetchOnReconnect: false,
    refetchInterval: query => !query.state.error && (!query.state.data || ['Queued', 'Claimed', 'Unknown'].includes(query.state.data.state)) ? 250 : false })
  const printerBusy = entries.some(entry => entry.print === 'queued')
  useEffect(() => {
    if (printRequest.error) {
      printing.current = false
      setEntries(current => current.map(item => item.requestId === printRequestId && item.print === 'queued'
        ? { ...item, print: 'failed', detail: 'Print status unavailable. Check for a label before retrying.' } : item))
      return
    }
    if (!printRequest.data) return
    const state = printRequest.data.state
    if (state === 'Queued' || state === 'Claimed' || state === 'Unknown') return
    printing.current = false
    setEntries(current => current.map(item => item.requestId === printRequest.data.id && item.print === 'queued'
      ? { ...item, print: state === 'Completed' ? 'printed' : 'failed', detail: printRequest.data.detail ?? undefined }
      : item))
  }, [printRequest.data, printRequest.error, printRequestId])

  async function printLabel(entry: IntakeEntry) {
    if (!entry.templateId || printing.current) return
    printing.current = true
    setEntries(current => current.map(item => item.id === entry.id ? { ...item, print: 'queued', detail: undefined } : item))
    try {
      const request = await api<PrintRequest>('/print-requests', { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token },
        body: JSON.stringify({ object_id: entry.id, template_id: entry.templateId, copies: 1 }) })
      setEntries(current => current.map(item => item.id === entry.id ? { ...item, requestId: request.id } : item))
      setPrintRequestId(request.id)
    } catch (failure) {
      printing.current = false
      setEntries(current => current.map(item => item.id === entry.id
        ? { ...item, print: 'failed', detail: errorText(failure) } : item))
    }
  }

  async function add(event: React.FormEvent, withLabel = true) {
    event.preventDefault()
    if (!name.trim() || propertiesUnavailable || copyId || copying || submitting.current ||
      (stockHolding && (!typeId || !stockAmount || !stockPolicy.data)) ||
      (withLabel && (printing.current || printerBusy || !selectedTemplate))) return
    submitting.current = true
    setBusy(true); setError(undefined)
    try {
      const property_values: { property_id: string, mode: 'value' | 'unset', value?: unknown }[] = []
      propertyValues.forEach(field => {
        const value = values[field.id!]
        if (value === undefined || value === '') return
        if (value === null) {
          property_values.push({ property_id: field.id!, mode: 'unset' })
          return
        }
        if (field.type === 'quantity') {
          const input = value as { amount?: string, unit?: string }
          if (!input.amount) return
          property_values.push({ property_id: field.id!, mode: 'value', value: { amount: input.amount,
            unit: input.unit ?? field.canonical_unit } })
          return
        }
        property_values.push({ property_id: field.id!, mode: 'value', value })
      })
      const payload = stockHolding ? {
        kind: 'stock.holding.create', name: name.trim(), description, object_type_id: typeId!,
        parent_id: parentId, relation, amount: stockAmount, unit: stockUnit || stockPolicy.data!.canonical_unit,
        allocate_alias: allocateAlias,
      } : {
        kind: 'object.create', name: name.trim(), description, object_type_id: typeId,
        parent_id: parentId, relation, tag_ids: tagIds, property_values, allocate_alias: allocateAlias,
      }
      const created = await sendCommand(session, makeCommand(session, payload as never))
      const entry: IntakeEntry = { id: created.entity_id, name: name.trim(), print: 'not-requested',
        templateId: withLabel ? selectedTemplate?.id : undefined }
      setEntries(current => [entry, ...current].slice(0, 100))
      void cache.invalidateQueries({ queryKey: ['inventory'] })
      if (withLabel) await printLabel(entry)
    } catch (failure) { setError(failure) } finally { submitting.current = false; setBusy(false) }
  }

  return <section>
    <div className="page-title"><div><Text className="eyebrow">FAST INTAKE</Text><Title order={1}>Quick create</Title>
      </div></div>
    {error && <Alert color="red" mb="md">{errorText(error)}</Alert>}
    {properties.error && <Alert color="red" mb="md">Could not load type properties: {errorText(properties.error)}
      <Button size="xs" variant="subtle" onClick={() => void properties.refetch()}>Retry</Button></Alert>}
    <Button mb="md" variant="light" disabled={busy || copying} onClick={() => navigate('/intake/select')}>Scan or select an object</Button>
    {copiedName && <Alert mb="md" color="green">Copied details from {copiedName}. Review the inputs below, then add as many new objects as needed.</Alert>}
    {copyId && <Alert mb="md" color="blue">{copying ? 'Loading object details…' : 'Could not load this object.'}
      <Button ml="sm" size="xs" variant="subtle" onClick={() => { setCopying(false); navigate('/intake') }}>Cancel</Button></Alert>}
    {location === '/intake/select' && <IntakeSourcePicker close={() => navigate('/intake')}
      scannerStatus={scannerStatus}
      select={id => navigate(`/intake/copy/${id}`)} />}
    <form onSubmit={event => void add(event, (event.nativeEvent as SubmitEvent).submitter?.getAttribute('name') !== 'no-label')}><Stack maw={820}>
      <Group grow align="start"><TextInput label="Name" value={name} autoFocus required maxLength={160}
        onChange={event => setName(event.currentTarget.value)} />
        <Select label="Object type" value={typeId} onChange={value => { setTypeId(value); setValues({}) }} clearable searchable
          data={(types.data ?? []).filter(type => !type.abstract).map(type => ({ value: type.id, label: type.name }))} /></Group>
      <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} autosize minRows={2} />
      <Group grow align="start"><Select label="Parent" value={parentId} onChange={value => setParentId(value ? String(value) : null)} clearable searchable
        data={[...parentOptions.values()].map(item => ({ value: item.id, label: `${item.name}${item.alias ? ` (${item.alias})` : ''}` }))} />
        <Select label="Initial placement" value={relation} onChange={value => setRelation((value ?? 'contained_in') as Placement)} data={placementOptions} /></Group>
      <Checkbox label="This is a fungible stock holding" checked={stockHolding}
        onChange={event => setStockHolding(event.currentTarget.checked)} disabled={!typeId} />
      {stockHolding && <Group grow align="start"><TextInput label="Initial quantity" value={stockAmount} inputMode="decimal" required
        onChange={event => setStockAmount(event.currentTarget.value)} />
        <TextInput label="Unit" value={stockUnit || stockPolicy.data?.canonical_unit || ''} readOnly
          description={stockPolicy.data ? `${stockPolicy.data.quantity_dimension} stock` : stockPolicy.error ? 'This type has no stock policy.' : 'Loading stock policy…'} /></Group>}
      {!stockHolding && <MultiSelect label="Direct tags" value={tagIds} onChange={setTagIds} searchable clearable hidePickedOptions
        data={(tags.data ?? []).map(tag => ({ value: tag.id, label: tag.name }))} />}
      {typeId && !stockHolding && <PropertyOverrides fields={propertyValues} values={values} setValues={setValues} />}
      <Group grow align="start"><Select label="Label template" value={selectedTemplate?.id ?? null} onChange={value => setTemplateId(value ? String(value) : null)} searchable
        data={(templates.data ?? []).map(template => ({ value: template.id, label: `${template.name}${template.is_default ? ' · default' : ''}` }))} />
        <Checkbox mt={34} label="Allocate a local ID" checked={allocateAlias} onChange={event => setAllocateAlias(event.currentTarget.checked)} /></Group>
      {printerBusy && <Alert color="blue">Waiting for the previous label before another label can be requested.</Alert>}
      <IntakeLabelPreview template={selectedTemplate} object={entries[0]} />
      <Group><Button type="submit" loading={busy} disabled={propertiesUnavailable || !!copyId || copying || printerBusy || !selectedTemplate || (stockHolding && !stockPolicy.data)} leftSection={<Plus size={17} />}>Add and print label</Button>
        <Button type="submit" name="no-label" variant="default" disabled={propertiesUnavailable || !!copyId || copying || busy || (stockHolding && !stockPolicy.data)}>Add with no label</Button>
        <Button variant="light" onClick={() => setCreator('tag')}>Add tag</Button><Button variant="light" onClick={() => setCreator('type')}>Add type</Button>
        <Button variant="light" onClick={() => setCreator('property')}>Add property</Button></Group>
    </Stack></form>
    {entries.length > 0 && <Stack mt="xl" gap="xs"><Group justify="space-between"><Title order={2}>Recently created</Title>
      <Button size="xs" variant="subtle" onClick={() => setEntries(current => current.filter(entry => entry.print === 'queued'))}>Clear completed activity</Button></Group>{entries.map(entry =>
      <Alert key={entry.id} color={entry.print === 'failed' ? 'red' : entry.print === 'queued' ? 'blue' : 'green'}>
        <a href={`/objects/${entry.id}`}>{entry.name}</a> · {entry.print === 'queued' ? 'label requested' : entry.print === 'printed' ? 'label printed' : entry.print === 'failed' ? `label request failed: ${entry.detail}` : 'created without a label'}
        {entry.print === 'failed' && entry.templateId && <Button ml="sm" size="xs" variant="light" disabled={printerBusy || busy}
          onClick={() => void printLabel(entry)}>Retry label</Button>}</Alert>)}</Stack>}
    <CreatorDialog key={creator ?? 'closed'} kind={creator} close={() => setCreator(null)} session={session} refresh={() => void cache.invalidateQueries({ queryKey: ['inventory'] })} />
  </section>
}

function PropertyOverrides({ fields, values, setValues }: { fields: PropertyValue[], values: Record<string, unknown>, setValues: React.Dispatch<React.SetStateAction<Record<string, unknown>>> }) {
  if (!fields.length) return null
  return <><Divider label="Property overrides" labelPosition="left" />{fields.map(field => {
    const id = field.id!
    if (values[id] === null) return <Group key={id}><Text size="sm">{field.label}: explicitly unset</Text>
      <Button size="xs" variant="subtle" onClick={() => setValues(current => { const next = { ...current }; delete next[id]; return next })}>Use inherited value</Button></Group>
    if (field.type === 'boolean') return <Checkbox key={id} label={field.label} checked={values[id] === true}
      onChange={event => setValues(current => ({ ...current, [id]: event.currentTarget.checked }))} />
    if (field.type === 'quantity') {
      const current = (values[id] as { amount?: string, unit?: string } | undefined) ?? {}
      return <Group key={id} grow><TextInput label={field.label} value={current.amount ?? ''} inputMode="decimal"
        onChange={event => setValues(old => ({ ...old, [id]: { ...current, amount: event.currentTarget.value } }))} />
        <Select label="Unit" value={current.unit ?? field.canonical_unit ?? null} onChange={unit => setValues(old => ({ ...old, [id]: { ...current, unit } }))}
          data={field.allowed_units.map(unit => ({ value: unit, label: unit }))} /></Group>
    }
    const type = field.type === 'integer' || field.type === 'decimal' ? 'number' : field.type === 'date' ? 'date' : 'text'
    return <TextInput key={id} label={field.label} type={type} value={String(values[id] ?? '')}
      onChange={event => setValues(current => ({ ...current, [id]: field.type === 'integer' ? Number(event.currentTarget.value) : event.currentTarget.value }))} />
  })}</>
}

function CreatorDialog({ kind, close, session, refresh }: { kind: Creator, close: () => void, session: Session, refresh: () => void }) {
  const scope = `${session.site_id}.${session.account_id}.${kind}`
  const [name, setName] = useIntakeState(scope, 'name', ''); const [description, setDescription] = useIntakeState(scope, 'description', ''); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>()
  const [added, setAdded] = useState('')
  const [parentIds, setParentIds] = useIntakeState<string[]>(scope, 'parents', []); const [parentType, setParentType] = useIntakeState<string | null>(scope, 'parentType', null)
  const [tagIds, setTagIds] = useIntakeState<string[]>(scope, 'tags', []); const [abstract, setAbstract] = useIntakeState(scope, 'abstract', false)
  const [key, setKey] = useIntakeState(scope, 'key', ''); const [valueType, setValueType] = useIntakeState(scope, 'valueType', 'text')
  const [dimension, setDimension] = useIntakeState<string | null>(scope, 'dimension', null); const [allowedUnits, setAllowedUnits] = useIntakeState<string[]>(scope, 'units', [])
  const tags = useQuery({ queryKey: ['inventory', 'tags'], queryFn: () => api<Tag[]>('/tags') })
  const types = useQuery({ queryKey: ['inventory', 'types'], queryFn: () => api<ObjectType[]>('/types') })
  const catalog = useQuery({ queryKey: ['property-units'], queryFn: () => api<{ id: string, label: string, units: { id: string, label: string }[] }[]>('/property-units') })
  if (!kind) return null
  async function create(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setError(undefined)
    try {
      const payload = kind === 'tag' ? { kind: 'tag.create', name, description, parent_ids: parentIds }
        : kind === 'type' ? { kind: 'type.create', name, description, parent_type_id: parentType, abstract, tag_ids: tagIds }
          : { kind: 'property.definition.create', key: key.trim() || null, label: name, description,
            value_type: valueType, quantity_dimension: valueType === 'quantity' ? dimension : null,
            allowed_units: valueType === 'quantity' ? allowedUnits : [] }
      await sendCommand(session, makeCommand(session, payload as never)); refresh()
      setAdded(`Added ${kind}: ${name}`)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  return <Modal opened onClose={close} title={`Add ${kind}`} centered><form onSubmit={create}><Stack>
    {error && <Alert color="red">{errorText(error)}</Alert>}<TextInput label="Name" value={name} onChange={event => setName(event.currentTarget.value)} required autoFocus />
    <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} autosize minRows={2} />
    {kind === 'tag' && <MultiSelect label="Parent tags" value={parentIds} onChange={setParentIds} searchable
      data={(tags.data ?? []).map(tag => ({ value: tag.id, label: tag.name }))} />}
    {kind === 'type' && <><Select label="Parent type" value={parentType} onChange={value => setParentType(value ? String(value) : null)} clearable searchable
      data={(types.data ?? []).map(type => ({ value: type.id, label: type.name }))} /><MultiSelect label="Direct tags" value={tagIds} onChange={setTagIds} searchable
        data={(tags.data ?? []).map(tag => ({ value: tag.id, label: tag.name }))} /><Checkbox label="Abstract type" checked={abstract} onChange={event => setAbstract(event.currentTarget.checked)} /></>}
    {kind === 'property' && <><TextInput label="Internal key (optional)" value={key} onChange={event => setKey(event.currentTarget.value)} placeholder="storage.volume" />
      <Select label="Value type" value={valueType} onChange={value => { setValueType(value ?? 'text'); setDimension(null); setAllowedUnits([]) }} data={['text', 'integer', 'decimal', 'boolean', 'date', 'datetime', 'quantity']} />
      {valueType === 'quantity' && <><Select label="Measurement" value={dimension} required onChange={value => { const selected = catalog.data?.find(item => item.id === value); setDimension(value ? String(value) : null); setAllowedUnits(selected?.units.map(unit => unit.id) ?? []) }}
        data={(catalog.data ?? []).map(item => ({ value: item.id, label: item.label }))} /><Checkbox.Group label="Allowed units" value={allowedUnits} onChange={setAllowedUnits}>{(catalog.data?.find(item => item.id === dimension)?.units ?? []).map(unit => <Checkbox key={unit.id} value={unit.id} label={unit.label} />)}</Checkbox.Group></>}
    </>}
    {added && <Alert color="green">{added}</Alert>}
    <Button type="submit" loading={busy}>Add {kind}</Button></Stack></form></Modal>
}
