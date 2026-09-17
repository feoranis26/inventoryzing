import { useMemo, useState } from 'react'
import { Alert, Button, Checkbox, Group, MultiSelect, PasswordInput, Select, Stack, Tabs, Text, TextInput, Textarea, Title } from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api'
import type { Session } from '../../api'

type Role = { id: string, name: string, description: string, version: number, permissions: string[], assigned_account_count: number }
type Account = { id: string, login: string, principal_id?: string | null, principal_name?: string | null, disabled: boolean, roles: string[], permissions: string[], version: number }
type Principal = { id: string, principal_kind: string, display_name: string }
const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request could not be completed.'

export function AccessAdministration({ session }: { session: Session }) {
  const canRoles = session.permissions.includes('role.manage')
  const canAccounts = session.permissions.includes('user.manage')
  return <section><div className="page-title"><div><Text className="eyebrow">ADMINISTRATION</Text><Title order={1}>Accounts and roles</Title>
    </div></div>
    <Tabs defaultValue={canAccounts ? 'accounts' : 'roles'}><Tabs.List>
      {canAccounts && <Tabs.Tab value="accounts">Accounts</Tabs.Tab>}
      {canRoles && <Tabs.Tab value="roles">Roles</Tabs.Tab>}
    </Tabs.List>{canAccounts && <Tabs.Panel value="accounts" pt="lg"><Accounts session={session} /></Tabs.Panel>}
      {canRoles && <Tabs.Panel value="roles" pt="lg"><Roles session={session} /></Tabs.Panel>}</Tabs>
  </section>
}

function Roles({ session }: { session: Session }) {
  const cache = useQueryClient(); const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState(''); const [description, setDescription] = useState(''); const [permissions, setPermissions] = useState<string[]>([])
  const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState(false)
  const roles = useQuery({ queryKey: ['admin', 'roles'], queryFn: () => api<Role[]>('/admin/roles') })
  const available = useQuery({ queryKey: ['admin', 'permissions'], queryFn: () => api<string[]>('/admin/permissions') })
  const current = roles.data?.find(role => role.id === selected)
  function select(id: string | null) { const role = roles.data?.find(item => item.id === id); setSelected(id); setName(role?.name ?? ''); setDescription(role?.description ?? ''); setPermissions(role?.permissions ?? []); setError(undefined) }
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!name.trim()) return; setBusy(true); setError(undefined)
    try {
      if (current) await api(`/admin/roles/${current.id}`, { method: 'PUT', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ name: name.trim(), description, permissions, expected_version: current.version }) })
      else await api('/admin/roles', { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ name: name.trim(), description, permissions }) })
      await cache.invalidateQueries({ queryKey: ['admin', 'roles'] }); select(null)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  if (roles.error || available.error) return <Alert color="red">{errorText(roles.error ?? available.error)}</Alert>
  return <Group align="start" grow><Stack><Title order={2}>Roles</Title>{(roles.data ?? []).map(role => <Button key={role.id} variant={role.id === selected ? 'light' : 'subtle'} justify="space-between" onClick={() => select(role.id)}>{role.name}<Text size="xs">{role.assigned_account_count} accounts</Text></Button>)}
    <Button variant="default" onClick={() => select(null)}>New role</Button></Stack>
    <form onSubmit={save}><Stack><Title order={2}>{current ? `Edit ${current.name}` : 'New role'}</Title>{error && <Alert color="red">{errorText(error)}</Alert>}
      <TextInput label="Role name" value={name} onChange={event => setName(event.currentTarget.value)} required maxLength={160} />
      <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} autosize minRows={2} />
      <MultiSelect label="Permissions" searchable value={permissions} onChange={setPermissions} data={(available.data ?? []).map(permission => ({ value: permission, label: permission }))} />
      <Button type="submit" loading={busy}>{current ? 'Save role' : 'Create role'}</Button></Stack></form></Group>
}

function Accounts({ session }: { session: Session }) {
  const cache = useQueryClient(); const [selected, setSelected] = useState<string | null>(null)
  const [login, setLogin] = useState(''); const [password, setPassword] = useState(''); const [roles, setRoles] = useState<string[]>([]); const [disabled, setDisabled] = useState(false); const [principalId, setPrincipalId] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState(false)
  const accounts = useQuery({ queryKey: ['admin', 'accounts'], queryFn: () => api<Account[]>('/admin/accounts') })
  const roleList = useQuery({ queryKey: ['admin', 'roles'], queryFn: () => api<Role[]>('/admin/roles') })
  const principals = useQuery({ queryKey: ['principals'], queryFn: () => api<Principal[]>('/principals'),
    enabled: session.permissions.includes('principal.manage') })
  const current = accounts.data?.find(account => account.id === selected)
  const rolesData = useMemo(() => (roleList.data ?? []).map(role => ({ value: role.id, label: role.name })), [roleList.data])
  function select(id: string | null) { const account = accounts.data?.find(item => item.id === id); setSelected(id); setLogin(account?.login ?? ''); setPassword(''); setRoles(account?.roles ?? []); setDisabled(account?.disabled ?? false); setPrincipalId(account?.principal_id ?? null); setError(undefined) }
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!login.trim() || (!current && password.length < 12)) return; setBusy(true); setError(undefined)
    try {
      if (current) await api(`/admin/accounts/${current.id}`, { method: 'PUT', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ principal_id: principalId, role_ids: roles, disabled, expected_version: current.version }) })
      else await api('/admin/accounts', { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ login: login.trim(), password, principal_id: principalId, role_ids: roles }) })
      await cache.invalidateQueries({ queryKey: ['admin', 'accounts'] }); select(null)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  async function resetPassword() {
    if (!current || password.length < 12) return; setBusy(true); setError(undefined)
    try { await api(`/admin/accounts/${current.id}/password`, { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token }, body: JSON.stringify({ password, expected_version: current.version }) }); setPassword(''); await cache.invalidateQueries({ queryKey: ['admin', 'accounts'] })
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  if (accounts.error || roleList.error) return <Alert color="red">{errorText(accounts.error ?? roleList.error)}</Alert>
  return <Group align="start" grow><Stack><Title order={2}>Accounts</Title>{(accounts.data ?? []).map(account => <Button key={account.id} variant={account.id === selected ? 'light' : 'subtle'} color={account.disabled ? 'gray' : undefined} justify="space-between" onClick={() => select(account.id)}>{account.login}<Text size="xs">{account.disabled ? 'Disabled' : account.principal_name ?? ''}</Text></Button>)}
    <Button variant="default" onClick={() => select(null)}>New account</Button></Stack>
    <form onSubmit={save}><Stack><Title order={2}>{current ? `Edit ${current.login}` : 'New account'}</Title>{error && <Alert color="red">{errorText(error)}</Alert>}
      <TextInput label="Login" value={login} disabled={!!current} onChange={event => setLogin(event.currentTarget.value)} required maxLength={160} />
      <PasswordInput label={current ? 'New password' : 'Password'} value={password} onChange={event => setPassword(event.currentTarget.value)} required={!current} description="At least 12 characters." />
      {session.permissions.includes('principal.manage') && <Select<string> label="Person principal" clearable searchable value={principalId} onChange={setPrincipalId}
        data={(principals.data ?? []).filter(principal => principal.principal_kind === 'person').map(principal => ({ value: principal.id, label: principal.display_name }))} />}
      <MultiSelect label="Roles" value={roles} onChange={setRoles} data={rolesData} searchable />
      {current && <Checkbox label="Disable this account" checked={disabled} onChange={event => setDisabled(event.currentTarget.checked)} />}
      {current && <Text size="xs" c="dimmed">Linked principal: {current.principal_name ?? 'Not linked yet'}</Text>}
      <Group><Button type="submit" loading={busy}>{current ? 'Save account' : 'Create account'}</Button>{current && <Button type="button" variant="light" disabled={password.length < 12 || busy} onClick={() => void resetPassword()}>Reset password</Button>}</Group>
    </Stack></form></Group>
}
