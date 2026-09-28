import { useState } from 'react'
import { Alert, Button, Group, MultiSelect, Select, Stack, Text, Textarea, TextInput, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type { components } from './generated/api'
import type { PropertyDefinition } from './Properties'

type PropertyGroup = components['schemas']['PropertyGroup']
type Payload = components['schemas']['Command']['payload']
type Actions = { disabled: boolean, submit: (payload: Payload) => void }

function useGroups(typeId?: string) {
  return useQuery({ queryKey: ['property-groups', typeId ?? 'all'],
    queryFn: () => api<PropertyGroup[]>(`/property-groups${typeId ? `?type_id=${typeId}` : ''}`) })
}

export function PropertyGroups({ disabled, submit }: Actions) {
  const groups = useGroups()
  const [editing, setEditing] = useState<string | null>(null)
  const [deleting, setDeleting] = useState<PropertyGroup | null>(null)
  const selected = groups.data?.find(group => group.id === editing)
  return <Stack mb="xl">
    <Group justify="space-between"><Title order={2}>Property groups</Title>
      <Button disabled={disabled} onClick={() => setEditing('new')}>New group</Button></Group>
    <Text size="sm">Bundle reusable fields, then attach groups to types. Descendants inherit those fields; values remain optional.</Text>
    {groups.error && <Alert color="red">{groups.error.message}</Alert>}
    {groups.data?.map(group => <Group key={group.id} justify="space-between">
      <div><Text fw={500}>{group.name}</Text><Text size="sm" c="dimmed">{group.description} · {group.property_ids.length} fields</Text></div>
      <Group><Button size="xs" variant="light" disabled={disabled} onClick={() => setEditing(group.id)}>Edit</Button>
        <Button size="xs" variant="subtle" color="red" disabled={disabled} onClick={() => setDeleting(group)}>Delete</Button></Group>
    </Group>)}
    {deleting && <Alert color="yellow" title={`Delete ${deleting.name}?`}>
      <Text size="sm">Only groups with no type assignments can be deleted. Their property definitions are kept.</Text>
      <Group mt="sm"><Button color="red" disabled={disabled} onClick={() => {
        submit({ kind: 'definition.delete', entity_kind: 'property_group', entity_id: deleting.id, expected_version: deleting.version })
        setDeleting(null)
      }}>Delete group</Button><Button variant="subtle" onClick={() => setDeleting(null)}>Cancel</Button></Group>
    </Alert>}
    {editing && <GroupEditor key={`${editing}:${selected?.version}`} group={selected} disabled={disabled}
      submit={submit} close={() => setEditing(null)} />}
  </Stack>
}

function GroupEditor({ group, disabled, submit, close }: Actions & { group?: PropertyGroup, close: () => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [description, setDescription] = useState(group?.description ?? '')
  const [members, setMembers] = useState<string[]>(group?.property_ids ?? [])
  const definitions = useQuery({ queryKey: ['properties'], queryFn: () => api<PropertyDefinition[]>('/properties') })
  return <form onSubmit={event => {
    event.preventDefault()
    submit(group ? { kind: 'property.group.edit', group_id: group.id, expected_version: group.version,
      name, description, property_ids: members } : { kind: 'property.group.create', name, description, property_ids: members })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    <TextInput label="Group name" required value={name} onChange={event => setName(event.currentTarget.value)} />
    <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} />
    <MultiSelect label="Properties" searchable value={members} onChange={setMembers}
      description="Create fields below, then reuse them in any number of groups."
      error={definitions.error?.message}
      data={(definitions.data ?? []).filter(field => field.editable && field.id)
        .map(field => ({ value: field.id!, label: `${field.label} (${field.quantity_dimension ?? field.type})` }))} />
    <Text size="xs">Removing a field is blocked if it would hide stored values. Defaults belong to types, not groups.</Text>
    <Group><Button type="submit" disabled={definitions.isPending || !!definitions.error}>Save group</Button>
      <Button variant="subtle" onClick={close}>Close</Button></Group>
  </Stack></fieldset></form>
}

export function TypePropertyGroups({ typeId, typeVersion, disabled, submit }: Actions & { typeId: string, typeVersion: number }) {
  const all = useGroups()
  const assigned = useGroups(typeId)
  const [selected, setSelected] = useState<string | null>(null)
  return <Stack gap="xs">
    <Text fw={500}>Property groups</Text>
    {(all.error || assigned.error) && <Alert color="red">{(all.error ?? assigned.error)?.message}</Alert>}
    {assigned.data?.map(group => <Group justify="space-between" key={group.id}>
      <Text size="sm">{group.name}{group.assigned_directly ? '' : ` · inherited from ${group.source_type_name}`}</Text>
      {group.assigned_directly && <Button size="compact-xs" variant="subtle" color="red" disabled={disabled}
        onClick={() => submit({ kind: 'type.property.group.set', type_id: typeId, expected_version: typeVersion,
          group_id: group.id, applicable: false })}>Remove group</Button>}
    </Group>)}
    <Group align="end"><Select<string> label="Add a group" searchable value={selected} onChange={setSelected}
      data={(all.data ?? []).filter(group => !assigned.data?.some(item => item.id === group.id))
        .map(group => ({ value: group.id, label: group.name }))} />
      <Button disabled={disabled || !selected || assigned.isPending} onClick={() => selected && submit({
        kind: 'type.property.group.set', type_id: typeId, expected_version: typeVersion,
        group_id: selected, applicable: true,
      })}>Add group</Button></Group>
    <Text size="xs" c="dimmed">Manage reusable groups on the Properties page.</Text>
  </Stack>
}
