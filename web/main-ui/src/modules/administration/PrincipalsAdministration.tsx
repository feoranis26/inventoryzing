import { useState } from 'react'
import { Alert, Button, Group, Select, Stack, Text, TextInput, Title } from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api'
import type { Session } from '../../api'

type Principal = { id: string, principal_kind: 'person' | 'team' | 'organization' | 'project' | 'site', display_name: string, version: number, linked_account_count: number, archived: boolean }
const kinds = [
  { value: 'person', label: 'Person' }, { value: 'team', label: 'Team' },
  { value: 'organization', label: 'Organization' }, { value: 'project', label: 'Project' }, { value: 'site', label: 'Site' },
]
const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request could not be completed.'

export function PrincipalsAdministration({ session }: { session: Session }) {
  const cache = useQueryClient(); const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState(''); const [kind, setKind] = useState<Principal['principal_kind']>('person'); const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState(false)
  const query = useQuery({ queryKey: ['principals'], queryFn: () => api<Principal[]>('/principals') })
  const current = query.data?.find(principal => principal.id === selected)
  function select(id: string | null) { const principal = query.data?.find(item => item.id === id); setSelected(id); setName(principal?.display_name ?? ''); setKind(principal?.principal_kind ?? 'person'); setError(undefined) }
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!name.trim()) return; setBusy(true); setError(undefined)
    try {
      if (current) await api(`/principals/${current.id}`, { method: 'PUT', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ display_name: name.trim(), principal_kind: kind, expected_version: current.version }) })
      else await api('/principals', { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ display_name: name.trim(), principal_kind: kind }) })
      await cache.invalidateQueries({ queryKey: ['principals'] }); select(null)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  async function archive() {
    if (!current) return; setBusy(true); setError(undefined)
    try { await api(`/principals/${current.id}/archive?expected_version=${current.version}`, { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token } }); await cache.invalidateQueries({ queryKey: ['principals'] }); select(null)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  if (query.isPending) return <Text>Loading principals…</Text>
  if (query.error) return <Alert color="red">{errorText(query.error)}</Alert>
  return <section><div className="page-title"><div><Text className="eyebrow">ADMINISTRATION</Text><Title order={1}>Principals</Title>
    </div></div>
    <Group align="start" grow><Stack><Title order={2}>Available principals</Title>{(query.data ?? []).map(principal => <Button key={principal.id} variant={selected === principal.id ? 'light' : 'subtle'} justify="space-between" onClick={() => select(principal.id)}>{principal.display_name}<Text size="xs">{principal.principal_kind}{principal.linked_account_count ? ` · ${principal.linked_account_count} account` : ''}</Text></Button>)}
      <Button variant="default" onClick={() => select(null)}>New principal</Button></Stack>
      <form onSubmit={save}><Stack><Title order={2}>{current ? `Edit ${current.display_name}` : 'New principal'}</Title>{error && <Alert color="red">{errorText(error)}</Alert>}
        <TextInput label="Display name" value={name} onChange={event => setName(event.currentTarget.value)} required maxLength={160} />
        <Select label="Kind" value={kind} onChange={value => setKind((value ?? 'person') as Principal['principal_kind'])} data={kinds} />
        {current?.linked_account_count ? <Alert color="blue">This principal is linked to an account, so it must remain a person.</Alert> : null}
        <Group><Button type="submit" loading={busy}>{current ? 'Save principal' : 'Create principal'}</Button>{current && <Button type="button" color="red" variant="light" loading={busy} onClick={() => void archive()}>Archive</Button>}</Group>
      </Stack></form></Group>
  </section>
}
