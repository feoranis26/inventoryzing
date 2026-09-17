import { Fragment, useState } from 'react'
import { Alert, Button, Checkbox, Group, Loader, Select, Stack, Table, Text, Textarea,
  TextInput, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type { components } from './generated/api'

export type PropertyDefinition = components['schemas']['PropertyDefinition']
export type PropertyValue = components['schemas']['PropertyValue']
type Payload = components['schemas']['Command']['payload']

type UnitDimension = components['schemas']['UnitDimension']
function useUnitCatalog() {
  return useQuery({ queryKey: ['property-units'],
    queryFn: () => api<UnitDimension[]>('/property-units'), staleTime: Infinity })
}

export function PropertiesPage({ create, edit, remove, canManage }: {
  create: () => void, edit: (field: PropertyDefinition) => void,
  remove: (field: PropertyDefinition) => void, canManage: boolean,
}) {
  const [search, setSearch] = useState('')
  const query = useQuery({ queryKey: ['properties'],
    queryFn: () => api<PropertyDefinition[]>('/properties') })
  return <section>
    <Group justify="space-between" mb="md"><Title order={1}>Available properties</Title>
      <Button onClick={create} disabled={!canManage}>New property</Button></Group>
<TextInput label="Find a property" value={search} mb="md"
      onChange={event => setSearch(event.currentTarget.value)} />
    {query.isPending ? <Loader /> : query.error ? <Alert color="red">{query.error.message}</Alert>
      : <Table.ScrollContainer minWidth={600}><Table>
        <Table.Thead><Table.Tr>{['Property', 'Key / placeholder', 'Provider', 'Type', 'Example', ''].map(
          heading => <Table.Th key={heading}>{heading}</Table.Th>)}</Table.Tr></Table.Thead>
        <Table.Tbody>{query.data.filter(field => `${field.label} ${field.key} ${field.provider}`
          .toLowerCase().includes(search.toLowerCase())).map(field => <Table.Tr key={field.key}>
          <Table.Td>{field.label}<Text size="xs" c="dimmed">{field.description}</Text></Table.Td>
          <Table.Td><code>{`{${field.key}}`}</code></Table.Td>
          <Table.Td>{field.provider}</Table.Td><Table.Td>{field.type}{field.quantity_dimension ? ` (${field.quantity_dimension})` : ''}</Table.Td>
          <Table.Td>{field.example || '—'}</Table.Td>
          <Table.Td>{field.editable && <Group gap="xs" wrap="nowrap">
            <Button size="compact-xs" variant="subtle" disabled={!canManage} onClick={() => edit(field)}>Edit</Button>
            <Button size="compact-xs" variant="subtle" color="red" disabled={!canManage}
              onClick={() => remove(field)}>Delete</Button>
          </Group>}</Table.Td>
        </Table.Tr>)}</Table.Tbody>
      </Table></Table.ScrollContainer>}
  </section>
}

export function ObjectProperties({ objectId, edit, canEdit }: {
  objectId: string, edit?: (field: PropertyValue) => void, canEdit?: boolean,
}) {
  const query = useQuery({ queryKey: ['inventory', 'properties', objectId],
    queryFn: () => api<PropertyValue[]>(`/objects/${objectId}/properties`) })
  if (query.isPending) return <Text size="sm">Loading properties…</Text>
  if (query.error) return <Alert color="red">{query.error.message}</Alert>
  // Existing object fields already have dedicated positions in the record above.
  const fields = query.data.filter(field => field.provider !== 'object' || field.key === 'object.created_at')
  return <dl className="record-fields">{fields.map(field => <Fragment key={field.key}>
    <dt title={field.key}>{field.label}</dt>
    <dd><Group justify="space-between" gap="xs"><span>{field.status === 'available' ? field.formatted_value
      : <Text c="dimmed" size="sm">{field.effective_state === 'unset' ? 'Explicitly unset' : field.status === 'missing' ? 'Not set' : 'Unavailable'}</Text>}</span>
      {field.editable && edit && <Button size="compact-xs" variant="subtle" disabled={!canEdit}
        onClick={() => edit(field)}>Edit</Button>}</Group>
      {field.source_name && <Text size="xs" c="dimmed">From {field.source_name}</Text>}</dd>
  </Fragment>)}</dl>
}

export function PropertyDefinitionForm({ busy, disabled, submit, typeId, typeVersion }: {
  typeId?: string, typeVersion?: number,
  busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const [key, setKey] = useState('')
  const [label, setLabel] = useState('')
  const [description, setDescription] = useState('')
  const [valueType, setValueType] = useState<string>('text')
  const catalog = useUnitCatalog()
  const [dimension, setDimension] = useState<'count' | 'volume' | 'length' | 'mass' | 'area'>('volume')
  const units = catalog.data?.find(item => item.id === dimension)?.units ?? []
  const [allowedUnits, setAllowedUnits] = useState<string[]>(['ml', 'l', 'us_gal', 'imperial_gal'])
  return <form onSubmit={event => {
    event.preventDefault()
    submit({ kind: 'property.definition.create', key: key.trim() || null, type_id: typeId,
      expected_version: typeVersion, label, description,
      value_type: valueType as 'text', quantity_dimension: valueType === 'quantity' ? dimension : null,
      allowed_units: valueType === 'quantity' ? allowedUnits : [] })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    <details><summary>Advanced: internal key</summary>
      <TextInput label="Internal key (optional)" description="Leave blank to generate automatically. For example storage.volume."
        value={key} onChange={event => setKey(event.currentTarget.value)} />
    </details>
    <TextInput label="Name" value={label} onChange={event => setLabel(event.currentTarget.value)} required />
    <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} />
    <Select label="Value type" value={valueType} onChange={value => setValueType(value ?? 'text')}
      data={['text', 'integer', 'decimal', 'boolean', 'date', 'datetime', 'quantity']} />
    {valueType === 'quantity' && <>
      {catalog.error && <Alert color="red">{catalog.error.message}</Alert>}
      <Select label="Measurement" value={dimension} disabled={catalog.isPending}
        data={(catalog.data ?? []).map(item => ({ value: item.id, label: item.label }))}
        onChange={value => {
          const selected = catalog.data?.find(item => item.id === value)
          if (selected) { setDimension(selected.id); setAllowedUnits(selected.units.map(unit => unit.id)) }
        }} />
      <Text size="sm" fw={500}>Allowed units</Text>
      <div>{units.map(unit => <Checkbox key={unit.id} label={unit.label}
        checked={allowedUnits.includes(unit.id)} onChange={event => {
          const checked = event.currentTarget.checked
          setAllowedUnits(current => checked ? [...current, unit.id] : current.filter(value => value !== unit.id))
        }} />)}</div></>}
    <Button type="submit" loading={busy} disabled={valueType === 'quantity' && (!catalog.data || !allowedUnits.length)}>Create property</Button>
  </Stack></fieldset></form>
}

export function PropertyDefinitionEditor({ field, busy, disabled, submit }: {
  field: PropertyDefinition, busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const [label, setLabel] = useState(field.label)
  const [description, setDescription] = useState(field.description)
  return <form onSubmit={event => {
    event.preventDefault()
    submit({ kind: 'property.definition.edit', property_id: field.id!, expected_version: field.version,
      label, description })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    <TextInput label="Name" value={label} onChange={event => setLabel(event.currentTarget.value)} required />
    <Textarea label="Description" value={description}
      onChange={event => setDescription(event.currentTarget.value)} minRows={3} autosize />
    <details><summary>Advanced details</summary><Text size="sm">Internal key: <code>{field.key}</code></Text></details>
    <Button type="submit" loading={busy}>Save property</Button>
  </Stack></fieldset></form>
}

export function TypePropertiesEditor({ typeId, typeVersion, submit, editValue, disabled }: {
  typeId: string, typeVersion: number, submit: (payload: Payload) => void,
  editValue: (field: PropertyValue) => void, disabled: boolean,
}) {
  const [action, setAction] = useState<'create' | 'reuse' | null>(null)
  const [existing, setExisting] = useState<string | null>(null)
  const catalog = useQuery({ queryKey: ['properties'], queryFn: () => api<PropertyDefinition[]>('/properties') })
  const effective = useQuery({ queryKey: ['inventory', 'type-properties', typeId],
    queryFn: () => api<PropertyValue[]>(`/types/${typeId}/properties`) })
  if (catalog.isPending || effective.isPending) return <Loader />
  if (catalog.error || effective.error) return <Alert color="red">{(catalog.error ?? effective.error)?.message}</Alert>
  const available = catalog.data.filter(field => field.editable && field.id
    && !effective.data.some(item => item.id === field.id))
  return <Stack>
    <Group><Button disabled={disabled} onClick={() => setAction('create')}>Create property</Button>
      <Button variant="light" disabled={disabled} onClick={() => setAction('reuse')}>Use existing property</Button></Group>
    {action && <Button variant="subtle" size="compact-xs" disabled={disabled} onClick={() => setAction(null)}>Back to properties</Button>}
    {action === 'create' ? <>
      <Text size="sm">This field will be available on this type and its descendants. Values are optional; it starts unset.</Text>
      <PropertyDefinitionForm key={typeVersion} typeId={typeId} typeVersion={typeVersion}
        busy={disabled} disabled={disabled} submit={submit} />
    </> : action === 'reuse' ? <>
      <Select<string> label="Existing property" placeholder="Search by name" searchable value={existing} onChange={setExisting}
        nothingFoundMessage="No other properties available"
        data={available.map(field => ({ value: field.id!, label: `${field.label} — ${field.declared_on.length ? `used by ${field.declared_on.join(', ')}` : 'not yet used'}` }))} />
      <Text size="sm">Reusing a field preserves its identity across types. Creating another field with the same name keeps it separate.</Text>
      <Button disabled={disabled || !existing} onClick={() => {
        if (existing) submit({ kind: 'type.property.declare', type_id: typeId,
          expected_version: typeVersion, property_id: existing, applicable: true })
      }}>Add to type</Button>
    </> : effective.data.length === 0 ? <Text c="dimmed">No properties yet. Create a field here or reuse an existing one.</Text>
      : effective.data.map(field => <div key={field.id} className="property-editor-row">
        <Group justify="space-between" align="start"><div><Text fw={500}>{field.label}</Text>
          <Text size="xs" c="dimmed">{field.formatted_value || (field.effective_state === 'unset' ? 'Explicitly unset' : 'No default')}
            {field.source_name ? ` · from ${field.source_name}` : ''}</Text>
          {!field.declared_directly && <Text size="xs" c="dimmed">Inherited from {field.applicability_source_name}</Text>}
          <details><summary>Advanced details</summary><code>{field.key}</code></details>
        </div><Group gap="xs">
          <Button size="compact-xs" variant="light" onClick={() => editValue(field)} disabled={disabled}>Default</Button>
          {field.declared_directly && <Button size="compact-xs" variant="subtle" color="red" disabled={disabled}
            onClick={() => submit({ kind: 'type.property.declare', type_id: typeId,
              expected_version: typeVersion, property_id: field.id!, applicable: false })}>Remove</Button>}
        </Group></Group>
      </div>)}
  </Stack>
}

export function PropertyValueForm({ field, targetId, targetKind, targetVersion, busy,
  disabled, submit }: {
  field: PropertyValue, targetId: string, targetKind: 'object' | 'object_type', targetVersion: number,
  busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const catalog = useUnitCatalog()
  const units = catalog.data?.find(item => item.id === field.quantity_dimension)?.units ?? []
  const local = field.local_state === 'value' ? field.value : null
  const quantity = local && typeof local === 'object' && !Array.isArray(local) ? local as Record<string, unknown> : null
  const [mode, setMode] = useState<string>(field.local_state)
  const [value, setValue] = useState(field.type === 'quantity' ? String(quantity?.display_amount ?? '')
    : field.type === 'boolean' ? String(local ?? 'true') : local == null ? '' : String(local))
  const [unit, setUnit] = useState(String(quantity?.display_unit ?? field.allowed_units[0] ?? ''))
  function typedValue() {
    if (field.type === 'integer') return Number.parseInt(value, 10)
    if (field.type === 'boolean') return value === 'true'
    if (field.type === 'quantity') return { amount: value, unit }
    return value
  }
  return <form onSubmit={event => {
    event.preventDefault()
    submit({ kind: 'property.value.set', target_id: targetId, target_kind: targetKind,
      expected_version: targetVersion, property_id: field.id!, mode: mode as 'inherit',
      value: mode === 'value' ? typedValue() : null })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    <Text fw={500}>{field.label}</Text>
    <Select label="Local behavior" value={mode} onChange={value => setMode(value ?? 'inherit')}
      data={[{ value: 'inherit', label: 'Use inherited value' }, { value: 'unset', label: 'Leave unset' },
        { value: 'value', label: 'Set a local value' }]} />
    {mode === 'value' && (field.type === 'boolean'
      ? <Select label="Value" value={value} onChange={value => setValue(value ?? 'true')}
        data={[{ value: 'true', label: 'Yes' }, { value: 'false', label: 'No' }]} />
      : <Group grow><TextInput label={field.type === 'quantity' ? 'Amount' : 'Value'} value={value}
          type={field.type === 'date' ? 'date' : field.type === 'integer' ? 'number' : 'text'}
          onChange={event => setValue(event.currentTarget.value)} required />
        {field.type === 'quantity' && <Select label="Unit" value={unit} onChange={value => setUnit(value ?? '')}
          disabled={catalog.isPending} error={catalog.error?.message}
          data={units.filter(item => field.allowed_units.includes(item.id)).map(item => ({ value: item.id, label: item.label }))} />}</Group>)}

    {field.type === 'quantity' && field.local_state === 'value' && mode === 'value' && <Group align="end">
      <Select label="Change displayed unit" value={unit} onChange={value => setUnit(value ?? '')}
        disabled={catalog.isPending} data={units.filter(item => field.allowed_units.includes(item.id))
          .map(item => ({ value: item.id, label: item.label }))} />
      <Button type="button" variant="light" disabled={!unit} onClick={() => submit({ kind: 'property.unit.set',
        target_id: targetId, target_kind: targetKind, expected_version: targetVersion,
        property_id: field.id!, unit })}>Convert display</Button>
    </Group>}
    <Button type="submit" loading={busy}>Save property</Button>
  </Stack></fieldset></form>
}
