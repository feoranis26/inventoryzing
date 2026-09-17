import { ObjectProperties, PropertiesPage, PropertyDefinitionEditor, PropertyDefinitionForm, PropertyValueForm,
  TypePropertiesEditor } from './Properties'
import type { PropertyDefinition, PropertyValue } from './Properties'
import { useDeferredValue, useId, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import {
  ActionIcon, Alert, Badge, Breadcrumbs, Button, Checkbox, Group, Loader, Modal,
  MultiSelect, Pagination, PasswordInput, SegmentedControl, Select, Stack, Table, Tabs, Text,
  Textarea, TextInput, Title, Tooltip,
} from '@mantine/core'
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useLocation } from 'wouter'
import {
  ArrowLeft, ArrowRight, ArrowRightLeft, Box, Boxes, ChevronDown, ChevronRight, FolderTree,
  GitBranch, History, Layers, LogOut, MapPin, Pencil, Plus, RefreshCw, ScanLine,
  Search, Settings, Tags,
} from 'lucide-react'
import { ApiError, api, makeCommand, sendCommand } from './api'
import logoUrl from '../../../assets/branding/inventoryzing.svg?url'
import type { Command, EntityTags, HistoryEntry, InventoryObject, ObjectPage, ObjectType,
  Payload, Session, Tag } from './api'
import { LabelPanel } from './modules/labeling/LabelPanel'
import { LabelEditor } from './modules/labeling/LabelEditor'
import { ScannerWorkflow } from './modules/scanning/ScannerWorkflows'
import type { ScannerStatus } from './modules/scanning/ScannerWorkflows'
import { QuickCreate } from './modules/intake/QuickCreate'
import { StockHoldingControls, StockPolicyForm } from './modules/stock/StockControls'
import { SiteSettingsPage } from './modules/administration/SiteSettings'
import { AccessAdministration } from './modules/administration/AccessAdministration'
import { PrincipalsAdministration } from './modules/administration/PrincipalsAdministration'

const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request failed.'
const formatTime = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

function IconButton({ label, children, onClick, disabled }: {
  label: string, children: ReactNode, onClick: () => void, disabled?: boolean,
}) {
  return <Tooltip label={label}><ActionIcon variant="subtle" color="gray" size="lg"
    aria-label={label} onClick={onClick} disabled={disabled}>{children}</ActionIcon></Tooltip>
}

function Failure({ error, retry }: { error: unknown, retry?: () => void }) {
  return <Alert color="red" title="Request failed" role="alert">
    {errorText(error)}
    {retry && <Button mt="sm" variant="light" color="red" size="xs" onClick={retry} leftSection={<RefreshCw size={14} />}>Retry</Button>}
  </Alert>
}

export default function App() {
  const query = useQuery({ queryKey: ['session'], queryFn: () => api<Session>('/auth/session'), staleTime: 60_000 })
  if (query.isPending) return <div className="loading-screen"><Loader aria-label="Loading session" /></div>
  if (query.error instanceof ApiError && query.error.status === 401) return <SignIn />
  if (query.error) return <div className="sign-in"><Failure error={query.error} retry={() => void query.refetch()} /></div>
  return <Workspace key={`${query.data.site_id}:${query.data.account_id}`} session={query.data} />
}

function SignIn() {
  const cache = useQueryClient()
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const session = await api<Session>('/auth/login', { method: 'POST', body: JSON.stringify({ login, password }) })
      setPassword('')
      cache.setQueryData(['session'], session)
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  return <main className="sign-in">
    <div className="brand"><img src={logoUrl} alt="inventoryzing" width={210} height={26} /></div>
    <form onSubmit={submit}>
      <Stack gap="lg">
        <div><Text className="eyebrow">INVENTORYZING</Text><Title order={1}>Sign in</Title></div>
        {error != null && <Failure error={error} />}
        <TextInput label="Login" value={login} onChange={event => setLogin(event.currentTarget.value)} required autoComplete="username" autoFocus />
        <PasswordInput label="Password" value={password} onChange={event => setPassword(event.currentTarget.value)} required autoComplete="current-password" />
        <Button type="submit" loading={busy} rightSection={<ArrowRight size={17} />}>Sign in</Button>
      </Stack>
    </form>
  </main>
}

type ObjectDialog = { kind: 'create', parentId?: string, object?: InventoryObject, tagIds?: string[] }
  | { kind: 'type', type?: ObjectType, copy?: ObjectType, tagIds?: string[] }
  | { kind: 'edit' | 'move' | 'object-type', object: InventoryObject }
type TagDialog = { kind: 'tag-create', tag?: Tag } | { kind: 'tag-edit', tag: Tag }
  | { kind: 'tag-parents', tag: Tag }
type AssignmentTarget = { id: string, name: string, version: number, entityKind: 'object' | 'object_type' }
type Dialog = ObjectDialog | TagDialog | { kind: 'tags', target: AssignmentTarget }
  | { kind: 'stock-policy', type: ObjectType }
  | { kind: 'property-definition' }
  | { kind: 'property-edit', field: PropertyDefinition }
  | { kind: 'type-properties', type: ObjectType }
  | { kind: 'definition-delete', entityId: string, entityKind: 'tag' | 'object_type' | 'property_definition',
      name: string, version: number }
  | { kind: 'property-value', targetId: string, targetKind: 'object' | 'object_type',
      targetVersion: number, field: PropertyValue }

function Workspace({ session }: { session: Session }) {
  const cache = useQueryClient()
  const [location, navigate] = useLocation()
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [scannerStatus, setScannerStatus] = useState<ScannerStatus>({ ready: false })
  const storageKey = `inventoryzing.pending.${session.site_id}.${session.account_id}`
  const [pending, setPending] = useState<Command | null>(() => {
    try { return JSON.parse(sessionStorage.getItem(storageKey) ?? 'null') } catch { return null }
  })
  const [busy, setBusy] = useState(false)
  const inFlight = useRef(false)
  const [error, setError] = useState<unknown>()
  const objectId = /^\/objects\/([^/]+)$/.exec(location)?.[1]
  const subtreeId = /^\/tree\/([^/]+)$/.exec(location)?.[1]
  const tagFilterId = /^\/tag\/([^/]+)$/.exec(location)?.[1]
  const scanMoveDestinationId = /^\/scanner\/move\/([^/]+)$/.exec(location)?.[1]
  const can = (permission: string) => session.permissions.includes(permission)

  async function duplicate(source: InventoryObject | ObjectType, kind: 'object' | 'type') {
    try {
      const tags = await api<EntityTags>(`/${kind === 'object' ? 'objects' : 'types'}/${source.id}/tags`)
      setDialog(kind === 'object'
        ? { kind: 'create', object: source as InventoryObject, tagIds: tags.explicit_tag_ids }
        : { kind: 'type', copy: source as ObjectType, tagIds: tags.explicit_tag_ids })
    } catch (failure) { setError(failure) }
  }

  async function deliver(command: Command) {
    if (inFlight.current) return
    inFlight.current = true
    setBusy(true)
    setError(undefined)
    try {
      const result = await sendCommand(session, command)
      sessionStorage.removeItem(storageKey)
      setPending(null)
      setDialog(null)
      await cache.invalidateQueries({ queryKey: ['inventory'] })
      if (command.payload.kind === 'property.definition.create' ||
          command.payload.kind === 'property.definition.edit' ||
          command.payload.kind === 'definition.delete') {
        await cache.invalidateQueries({ queryKey: ['properties'] })
        await cache.invalidateQueries({ queryKey: ['label-placeholders'] })
      }
      if (command.payload.kind === 'type.create' || command.payload.kind === 'type.edit' ||
          command.payload.kind === 'stock.policy.set' ||
          (command.payload.kind === 'tags.set' && command.payload.entity_kind === 'object_type')) {
        navigate('/types')
      } else if (command.payload.kind === 'property.definition.create') {
        navigate(command.payload.type_id ? '/types' : '/properties')
      } else if (command.payload.kind === 'property.definition.edit') {
        navigate('/properties')
      } else if (command.payload.kind === 'type.property.declare' ||
                 (command.payload.kind === 'property.value.set' && command.payload.target_kind === 'object_type')) {
        navigate('/types')
      } else if (command.payload.kind === 'tag.create' || command.payload.kind === 'tag.edit' ||
                 command.payload.kind === 'tag.parents.set' ||
                 (command.payload.kind === 'definition.delete' && command.payload.entity_kind === 'tag')) {
        navigate('/tags')
      } else if (command.payload.kind === 'definition.delete' &&
                 command.payload.entity_kind === 'object_type') {
        navigate('/types')
      } else {
        navigate(`/objects/${result.entity_id}`)
      }
      const editedTypeId = command.payload.kind === 'property.definition.create'
        || command.payload.kind === 'type.property.declare' ? command.payload.type_id
        : command.payload.kind === 'property.value.set' && command.payload.target_kind === 'object_type'
          ? command.payload.target_id : undefined
      if (editedTypeId) {
        const types = await api<ObjectType[]>('/types')
        const type = types.find(item => item.id === editedTypeId)
        if (type) setDialog({ kind: 'type-properties', type })
      }
    } catch (failure) {
      setError(failure)
      if (failure instanceof ApiError && failure.status < 500) {
        sessionStorage.removeItem(storageKey)
        setPending(null)
        if (failure.status === 401) void cache.invalidateQueries({ queryKey: ['session'] })
      }
    } finally { setBusy(false); inFlight.current = false }
  }

  function submit(payload: Payload, authorityEpoch = 1) {
    if (pending || inFlight.current) return
    const command = makeCommand(session, payload, authorityEpoch)
    try { sessionStorage.setItem(storageKey, JSON.stringify(command)) }
    catch { setError(new Error('Browser session storage is unavailable. No command was sent.')); return }
    setPending(command)
    void deliver(command)
  }

  async function logout() {
    try {
      await api('/auth/logout', { method: 'POST', headers: { 'X-CSRF-Token': session.csrf_token } })
      cache.clear()
      navigate('/')
    } catch (failure) { setError(failure) }
  }

  return <div className="workspace">
    <header className="app-header">
      <Link href="/" className="brand"><img src={logoUrl} alt="inventoryzing" width={210} height={26} /></Link>
      <div className="account"><span>{session.login}</span><IconButton label="Sign out" disabled={!!pending} onClick={() => void logout()}><LogOut size={18} /></IconButton></div>
    </header>
    <aside className="sidebar">
      <Text className="eyebrow nav-heading">WORKSPACE</Text>
      <nav aria-label="Main navigation">
        <Link href="/" className={location === '/' || !!objectId || !!subtreeId || !!tagFilterId ? 'nav-link active' : 'nav-link'}><Boxes size={19} />Inventory</Link>
        <Link href="/types" className={location === '/types' ? 'nav-link active' : 'nav-link'}><Layers size={19} />Object types</Link>
        <Link href="/tags" className={location === '/tags' ? 'nav-link active' : 'nav-link'}><Tags size={19} />Tags</Link>
        {can('label.print') && <Link href="/labels" className={location === '/labels' ? 'nav-link active' : 'nav-link'}><Pencil size={19} />Labels</Link>}
        <Link href="/properties" className={location === '/properties' ? 'nav-link active' : 'nav-link'}><Layers size={19} />Properties</Link>
        <Link href="/scanner" className={location === '/scanner' || !!scanMoveDestinationId ? 'nav-link active' : 'nav-link'}><ScanLine size={19} />Scanner</Link>
        {can('inventory.create') && <Link href="/intake" className={location === '/intake' ? 'nav-link active' : 'nav-link'}><Plus size={19} />Quick create</Link>}
        {can('system.config.view') && <Link href="/settings" className={location === '/settings' ? 'nav-link active' : 'nav-link'}><Settings size={19} />Site settings</Link>}
        {(can('role.manage') || can('user.manage')) && <Link href="/access" className={location === '/access' ? 'nav-link active' : 'nav-link'}><Settings size={19} />Accounts and roles</Link>}
        {can('principal.manage') && <Link href="/principals" className={location === '/principals' ? 'nav-link active' : 'nav-link'}><Settings size={19} />Principals</Link>}
      </nav>
      <div className="site-mark"><span className="site-dot" /><div><Text size="sm" fw={500}>{session.site_name}</Text><Text size="xs" c="dimmed">Local site</Text></div></div>
    </aside>
    <main className="main-content">
      <Scanner navigate={navigate} />
      <ScannerWorkflow destinationId={scanMoveDestinationId} csrfToken={session.csrf_token}
        onStatus={setScannerStatus}
        lookupTarget={location === '/intake/select' ? 'intake' : 'object'}
        visible={location === '/scanner' || !!scanMoveDestinationId} />
      {pending && <Alert mb="lg" color="yellow" title={busy ? 'Saving command' : 'Command outcome not confirmed'} role="status">
        <Group justify="space-between" gap="sm"><Text size="sm" className="mono command-id">{pending.command_id}</Text>
          {!busy && <Button size="xs" variant="light" color="yellow" onClick={() => void deliver(pending)} leftSection={<RefreshCw size={14} />}>Retry original command</Button>}
        </Group>
      </Alert>}
      {error != null && !dialog && <div className="error-block"><Failure error={error} /></div>}
      {location === '/scanner' || scanMoveDestinationId ? null
      : location === '/intake' || location.startsWith('/intake/') ? <QuickCreate session={session} scannerStatus={scannerStatus} />
      : location === '/settings' ? <SiteSettingsPage session={session} />
      : location === '/access' ? <AccessAdministration session={session} />
      : location === '/principals' ? <PrincipalsAdministration session={session} />
      : location === '/properties' ? <PropertiesPage create={() => setDialog({ kind: 'property-definition' })}
        edit={field => setDialog({ kind: 'property-edit', field })}
        remove={field => setDialog({ kind: 'definition-delete', entityId: field.id!,
          entityKind: 'property_definition', name: field.label, version: field.version })}
        canManage={can('inventory.edit') && !pending} /> : location === '/labels' ? <LabelEditor csrfToken={session.csrf_token}
        canManage={can('label.template.manage')} />
        : objectId ? <ObjectDetail key={objectId} id={objectId} siteName={session.site_name}
        csrfToken={session.csrf_token} can={can} disabled={!!pending}
        duplicate={object => void duplicate(object, 'object')}
        edit={object => setDialog({ kind: 'edit', object })} move={object => setDialog({ kind: 'move', object })}
        changeType={object => setDialog({ kind: 'object-type', object })}
        editProperty={(object, field) => setDialog({ kind: 'property-value', targetId: object.id,
          targetKind: 'object', targetVersion: object.version, field })}
        editTags={object => setDialog({ kind: 'tags', target: { id: object.id, name: object.name,
          version: object.version, entityKind: 'object' } })} submit={submit} />
        : location === '/tags' ? <TagsPage create={() => setDialog({ kind: 'tag-create' })}
          duplicate={tag => setDialog({ kind: 'tag-create', tag })}
          edit={tag => setDialog({ kind: 'tag-edit', tag })}
          parents={tag => setDialog({ kind: 'tag-parents', tag })}
          remove={tag => setDialog({ kind: 'definition-delete', entityId: tag.id, entityKind: 'tag',
            name: tag.name, version: tag.version })}
          canEdit={can('inventory.edit') && !pending} canCreate={can('inventory.create') && !pending} />
        : location === '/types' ? <TypesPage create={() => setDialog({ kind: 'type' })}
          duplicate={type => void duplicate(type, 'type')}
          edit={type => setDialog({ kind: 'type', type })}
          properties={type => setDialog({ kind: 'type-properties', type })}
          stockPolicy={type => setDialog({ kind: 'stock-policy', type })}
          assign={type => setDialog({ kind: 'tags', target: { id: type.id, name: type.name,
            version: type.version, entityKind: 'object_type' } })}
          remove={type => setDialog({ kind: 'definition-delete', entityId: type.id,
            entityKind: 'object_type', name: type.name, version: type.version })}
          canCreate={can('inventory.create') && !pending} canEdit={can('inventory.edit') && !pending} />
          : <InventoryPage key={subtreeId ?? tagFilterId ?? 'inventory'} rootId={subtreeId}
            initialTagId={tagFilterId} siteName={session.site_name}
            create={() => setDialog({ kind: 'create', parentId: subtreeId })} canCreate={can('inventory.create') && !pending} />}
    </main>
    <Modal opened={dialog !== null} onClose={() => { if (!pending) { setDialog(null); setError(undefined) } }}
      title={dialog?.kind === 'create' ? 'New object' : dialog?.kind === 'type' ? (dialog.type ? 'Edit object type' : 'New object type')
        : dialog?.kind === 'move' ? 'Move object' : dialog?.kind === 'edit' ? 'Edit object'
          : dialog?.kind === 'object-type' ? 'Change object type'
          : dialog?.kind === 'tag-create' ? 'New tag' : dialog?.kind === 'tag-edit' ? 'Edit tag'
          : dialog?.kind === 'tag-parents' ? 'Manage tag parents'
          : dialog?.kind === 'property-definition' ? 'New property definition'
            : dialog?.kind === 'property-edit' ? `Edit ${dialog.field.label}`
              : dialog?.kind === 'type-properties' ? `Properties for ${dialog.type.name}`
              : dialog?.kind === 'stock-policy' ? `Stock policy for ${dialog.type.name}`
              : dialog?.kind === 'definition-delete' ? `Delete ${dialog.name}`
              : dialog?.kind === 'property-value' ? `Edit ${dialog.field.label}` : 'Assign tags'}
      closeButtonProps={{ 'aria-label': 'Close dialog' }}
      closeOnClickOutside={!pending} closeOnEscape={!pending} withCloseButton={!pending} size="md" centered>
      {error != null && <div className="error-block"><Failure error={error} /></div>}
      {pending && !busy && <Button mb="md" fullWidth onClick={() => void deliver(pending)} leftSection={<RefreshCw size={16} />}>Retry original command</Button>}
      {dialog && (dialog.kind === 'tag-create' || dialog.kind === 'tag-edit' || dialog.kind === 'tag-parents'
        ? <TagForm key={dialog.kind} dialog={dialog} busy={busy} disabled={!!pending} submit={submit} />
        : dialog.kind === 'tags'
          ? <TagAssignmentForm key={`${dialog.target.entityKind}:${dialog.target.id}`} target={dialog.target}
            busy={busy} disabled={!!pending} submit={submit} />
          : dialog.kind === 'property-definition'
            ? <PropertyDefinitionForm busy={busy} disabled={!!pending} submit={submit} />
          : dialog.kind === 'property-edit'
            ? <PropertyDefinitionEditor field={dialog.field} busy={busy} disabled={!!pending} submit={submit} />
          : dialog.kind === 'definition-delete'
            ? <DeleteDefinitionForm dialog={dialog} busy={busy} disabled={!!pending} submit={submit} />
          : dialog.kind === 'type-properties'
            ? <TypePropertiesEditor key={`${dialog.type.id}:${dialog.type.version}`} typeId={dialog.type.id} typeVersion={dialog.type.version}
              disabled={!!pending} submit={submit} editValue={field => setDialog({
                kind: 'property-value', targetId: dialog.type.id, targetKind: 'object_type',
                targetVersion: dialog.type.version, field })} />
          : dialog.kind === 'stock-policy'
            ? <StockPolicyForm key={`${dialog.type.id}:${dialog.type.version}`} type={dialog.type}
              busy={busy} disabled={!!pending} submit={submit} />
          : dialog.kind === 'property-value'
            ? <PropertyValueForm field={dialog.field} targetId={dialog.targetId}
              targetKind={dialog.targetKind} targetVersion={dialog.targetVersion}
              busy={busy} disabled={!!pending} submit={submit} />
          : <ObjectForm key={dialog.kind} dialog={dialog} busy={busy} disabled={!!pending} submit={submit} />)}
    </Modal>
  </div>
}

function Scanner({ navigate }: { navigate: (path: string) => void }) {
  const [value, setValue] = useState('')
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  async function resolve(event: FormEvent) {
    event.preventDefault()
    if (!value || busy) return
    setBusy(true)
    setError(undefined)
    try {
      const object = await api<InventoryObject>(`/resolve?value=${encodeURIComponent(value)}`)
      navigate(`/objects/${object.id}`)
      setValue('')
    } catch (failure) { setError(failure) }
    finally { setBusy(false); input.current?.focus() }
  }
  return <section className="scanner" aria-label="Identifier scanner">
    <form onSubmit={resolve}>
      <TextInput ref={input} aria-label="Scan identifier" placeholder="Scan identifier" value={value}
        onChange={event => setValue(event.currentTarget.value)} leftSection={<ScanLine size={20} />} autoComplete="off" spellCheck={false}
        rightSection={busy ? <Loader size={16} /> : <ActionIcon type="submit" variant="subtle" aria-label="Resolve identifier"><ArrowRight size={18} /></ActionIcon>} />
    </form>
    {error != null && <Failure error={error} />}
  </section>
}

function InventoryPage({ create, canCreate, rootId, initialTagId, siteName }: {
  create: () => void, canCreate: boolean, rootId?: string, initialTagId?: string, siteName: string,
}) {
  const [search, setSearch] = useState('')
  const deferredSearch = useDeferredValue(search)
  const [view, setView] = useState('tree')
  const [page, setPage] = useState(1)
  const [tagFilter, setTagFilter] = useState<string | null>(initialTagId ?? null)
  const [typeFilter, setTypeFilter] = useState<string | null>(null)
  const cache = useQueryClient()
  const showList = view === 'list' || !!deferredSearch || !!tagFilter || !!typeFilter
  const root = useQuery({ queryKey: ['inventory', 'object', rootId],
    queryFn: () => api<InventoryObject>(`/objects/${rootId}`), enabled: !!rootId })
  const tags = useQuery({ queryKey: ['inventory', 'tags'], queryFn: () => api<Tag[]>('/tags') })
  const types = useQuery({ queryKey: ['inventory', 'types'], queryFn: () => api<ObjectType[]>('/types') })
  const query = useQuery({
    queryKey: ['inventory', 'objects', deferredSearch, rootId, tagFilter, typeFilter, page],
    queryFn: () => {
      const parameters = new URLSearchParams({ query: deferredSearch, offset: String((page - 1) * 50) })
      if (rootId) parameters.set('subtree_id', rootId)
      if (tagFilter) parameters.set('tag_id', tagFilter)
      if (typeFilter) parameters.set('type_id', typeFilter)
      return api<ObjectPage>(`/objects?${parameters}`)
    },
    enabled: showList,
  })
  const selectedTag = tags.data?.find(tag => tag.id === tagFilter)
  if (rootId && root.isPending) return <Loader aria-label="Loading subtree" />
  if (rootId && root.error) return <Failure error={root.error} retry={() => void root.refetch()} />
  return <>
    {root.data && <div className="subtree-path"><LocationPath path={root.data.location_path} siteName={siteName} subtreeLinks /></div>}
    <div className="page-title"><div><Text className="eyebrow">{rootId ? 'SUBTREE' : selectedTag ? 'CATEGORY' : 'CATALOG'}</Text><Title order={1}>{root.data?.name ?? selectedTag?.name ?? 'Inventory'} <span className="count">{showList ? query.data?.total ?? '' : ''}</span></Title></div>
      <Button leftSection={<Plus size={17} />} onClick={create} disabled={!canCreate}>New object</Button></div>
    <div className="list-toolbar"><TextInput aria-label="Search inventory" placeholder="Search inventory" leftSection={<Search size={17} />} value={search}
      onChange={event => { setSearch(event.currentTarget.value); setPage(1) }} />
      <Select aria-label="Filter by tag" placeholder="All tags" searchable clearable value={tagFilter}
        onChange={value => { setTagFilter(value); setPage(1) }} className="filter-control"
        data={(tags.data ?? []).map(tag => ({ value: tag.id, label: tag.name }))} />
      <Select aria-label="Filter by object type" placeholder="All types" searchable clearable value={typeFilter}
        onChange={value => { setTypeFilter(value); setPage(1) }} className="filter-control"
        data={(types.data ?? []).map(type => ({ value: type.id, label: type.name }))} />
      <SegmentedControl aria-label="Inventory view" value={view} onChange={value => { setView(value); setPage(1) }}
        data={[{ label: <Group gap={6}><FolderTree size={15} />Tree</Group>, value: 'tree' }, { label: 'All objects', value: 'list' }]} />
      <IconButton label="Refresh inventory" onClick={() => void cache.invalidateQueries({ queryKey: ['inventory'] })}><RefreshCw size={17} /></IconButton></div>
    {showList ? <>
      {query.isPending ? <Loader mt="xl" aria-label="Loading objects" /> : query.error ? <Failure error={query.error} retry={() => void query.refetch()} />
        : query.data.items.length ? <ObjectTable items={query.data.items} siteName={siteName} /> : <Empty title={search ? 'No matching objects' : 'No objects yet'} icon={<Box size={32} />} />}
      {!!query.data && query.data.total > 50 && <Pagination mt="lg" value={page} onChange={setPage} total={Math.ceil(query.data.total / 50)} />}
    </> : <section aria-label="Inventory hierarchy">
      {root.data ? <ul className="inventory-tree"><TreeNode object={root.data} initiallyExpanded /></ul> : <TreeLevel />}
    </section>}
  </>
}

function SubtreeLink({ object }: { object: InventoryObject }) {
  const label = `View subtree of ${object.name}`
  return <Tooltip label={label}><ActionIcon component={Link} href={`/tree/${object.id}`}
    variant="subtle" color="gray" size="lg" aria-label={label}><FolderTree size={18} /></ActionIcon></Tooltip>
}

function LocationPath({ path, siteName, subtreeLinks = false }: {
  path: InventoryObject['location_path'], siteName: string, subtreeLinks?: boolean,
}) {
  return <span className="location-path"><Link href="/">{siteName}</Link>{path.map(segment =>
    <span key={segment.id}><span className="path-separator" aria-hidden="true"> / </span>
      <Link href={`/${subtreeLinks ? 'tree' : 'objects'}/${segment.id}`}>{segment.name}</Link></span>)}</span>
}

function TreeLevel({ parentId, parentName, groupId }: { parentId?: string, parentName?: string, groupId?: string }) {
  const query = useInfiniteQuery({
    queryKey: ['inventory', 'tree', parentId], initialPageParam: 0,
    queryFn: ({ pageParam }) => api<ObjectPage>(`/objects?${parentId ? `parent_id=${parentId}` : 'roots=true'}&offset=${pageParam}`),
    getNextPageParam: (last, pages) => {
      const loaded = pages.reduce((total, page) => total + page.items.length, 0)
      return loaded < last.total ? loaded : undefined
    },
  })
  const objects = query.data?.pages.flatMap(page => page.items) ?? []
  return <ul id={groupId} className={`inventory-tree${parentId ? ' tree-children' : ''}`} aria-label={parentName ? `Contents of ${parentName}` : 'Site objects'}>
    {objects.map(object => <TreeNode key={object.id} object={object} />)}
    {query.isPending && <li className="tree-status"><Loader size="sm" aria-label="Loading branch" /></li>}
    {query.error && <li className="tree-status"><Failure error={query.error} retry={() => void (query.isFetchNextPageError ? query.fetchNextPage() : query.refetch())} /></li>}
    {!query.isPending && !query.error && !objects.length && <li className="tree-status"><Text size="sm" c="dimmed">{parentId ? 'No contents' : 'No objects yet'}</Text></li>}
    {query.hasNextPage && <li className="tree-status"><Button variant="subtle" size="xs" loading={query.isFetchingNextPage}
      leftSection={<ChevronDown size={14} />} onClick={() => void query.fetchNextPage()}
      aria-label={parentName ? `Load more children of ${parentName}` : 'Load more root objects'}>
      Load more ({objects.length} of {query.data?.pages[0].total})</Button></li>}
  </ul>
}

function TreeNode({ object, initiallyExpanded = false }: { object: InventoryObject, initiallyExpanded?: boolean }) {
  const [expanded, setExpanded] = useState(initiallyExpanded)
  const groupId = useId()
  const hasChildren = object.child_count > 0
  return <li data-object-id={object.id}>
    <div className="tree-row">
      {hasChildren ? <ActionIcon variant="subtle" color="gray" size="sm" className="tree-toggle"
        aria-label={`${expanded ? 'Collapse' : 'Expand'} ${object.name}`} aria-expanded={expanded}
        aria-controls={expanded ? groupId : undefined} onClick={() => setExpanded(!expanded)}>
        {expanded ? <ChevronDown size={17} /> : <ChevronRight size={17} />}</ActionIcon> : <span className="tree-toggle tree-leaf" />}
      <Link href={`/objects/${object.id}`} className="object-link tree-object">
        <span className="object-symbol">{hasChildren ? <FolderTree size={19} /> : <Box size={19} />}</span>
        <span className="object-name">{object.name}<small className="mono">{object.alias ?? object.id.slice(0, 8)}</small></span>
      </Link>
      {hasChildren && <Text size="xs" c="dimmed" className="tree-count">{object.child_count} {object.child_count === 1 ? 'item' : 'items'}</Text>}
      <SubtreeLink object={object} />
    </div>
    {expanded && hasChildren && <TreeLevel parentId={object.id} parentName={object.name} groupId={groupId} />}
  </li>
}

function ObjectTable({ items, siteName }: { items: InventoryObject[], siteName: string }) {
  return <Table.ScrollContainer minWidth={600}><Table verticalSpacing="md" className="object-table">
    <Table.Thead><Table.Tr><Table.Th>Object</Table.Th><Table.Th>Type</Table.Th><Table.Th>Location path</Table.Th><Table.Th>Updated</Table.Th><Table.Th><span className="sr-only">Subtree</span></Table.Th></Table.Tr></Table.Thead>
    <Table.Tbody>{items.map(object => <Table.Tr key={object.id}>
      <Table.Td><Link href={`/objects/${object.id}`} className="object-link"><span className="object-symbol"><Box size={19} strokeWidth={1.5} /></span><span className="object-name">{object.name}<small className="mono">{object.alias ?? object.id.slice(0, 8)}</small></span></Link></Table.Td>
      <Table.Td><Text size="sm" c="dimmed">{object.type_name ?? 'Unspecified'}</Text></Table.Td>
      <Table.Td><LocationPath path={object.location_path} siteName={siteName} /></Table.Td>
      <Table.Td><Text size="xs" c="dimmed">{formatTime(object.updated_at)}</Text></Table.Td>
      <Table.Td><SubtreeLink object={object} /></Table.Td>
    </Table.Tr>)}</Table.Tbody>
  </Table></Table.ScrollContainer>
}

function Empty({ title, icon }: { title: string, icon: ReactNode }) {
  return <div className="empty-state">{icon}<Text fw={500}>{title}</Text></div>
}

function EffectiveTags({ data, compact = false }: { data: EntityTags, compact?: boolean }) {
  if (!data.effective_tags.length) return <Text size="sm" c="dimmed">No tags</Text>
  return <div className={compact ? 'tag-chips compact' : 'effective-tags'}>{data.effective_tags.map(tag => {
    const direct = data.explicit_tag_ids.includes(tag.id)
    const detail = tag.sources.map(source => {
      if (direct && source.tag_id === tag.id) return 'Assigned directly'
      const owner = source.source_kind === 'object' ? 'object' : 'object type'
      return source.tag_id === tag.id ? `Inherited from ${owner}` : `Via ${source.tag_name} (${owner})`
    }).join('; ')
    return <span className="effective-tag" key={tag.id} title={detail}>
      <Link href={`/tag/${tag.id}`}><Badge variant={direct ? 'filled' : 'outline'} color={direct ? 'workshop' : 'gray'}>{tag.name}</Badge></Link>
      {!compact && <Text component="span" size="xs" c="dimmed">{detail}</Text>}
    </span>
  })}</div>
}

function EntityTagSummary({ target, edit, disabled, compact = false }: {
  target: AssignmentTarget, edit: () => void, disabled: boolean, compact?: boolean,
}) {
  const resource = target.entityKind === 'object' ? 'objects' : 'types'
  const query = useQuery({ queryKey: ['inventory', 'tags', target.entityKind, target.id],
    queryFn: () => api<EntityTags>(`/${resource}/${target.id}/tags`) })
  if (query.isPending) return <Loader size="xs" aria-label={`Loading tags for ${target.name}`} />
  if (query.error) return <Text size="xs" c="red">Tags unavailable</Text>
  return <div className={compact ? 'tag-summary compact' : 'tag-summary'}>
    <EffectiveTags data={query.data} compact={compact} />
    <Button size="compact-xs" variant="subtle" onClick={edit} disabled={disabled}>Edit tags</Button>
  </div>
}

function TypesPage({ create, edit, duplicate, properties, stockPolicy, assign, remove, canCreate, canEdit }: {
  duplicate: (type: ObjectType) => void,
  create: () => void, edit: (type: ObjectType) => void, properties: (type: ObjectType) => void,
  stockPolicy: (type: ObjectType) => void,
  assign: (type: ObjectType) => void, remove: (type: ObjectType) => void,
  canCreate: boolean, canEdit: boolean,
}) {
  const query = useQuery({ queryKey: ['inventory', 'types'], queryFn: () => api<ObjectType[]>('/types') })
  return <><div className="page-title"><div><Text className="eyebrow">CLASSIFICATION</Text><Title order={1}>Object types</Title></div>
    <Button onClick={create} disabled={!canCreate} leftSection={<Plus size={17} />}>New type</Button></div>
    {query.isPending ? <Loader aria-label="Loading types" /> : query.error ? <Failure error={query.error} /> : query.data.length ?
      <Table.ScrollContainer minWidth={760}><Table verticalSpacing="md"><Table.Thead><Table.Tr><Table.Th>Name</Table.Th><Table.Th>Parent</Table.Th><Table.Th>Tags</Table.Th><Table.Th>Description</Table.Th><Table.Th><span className="sr-only">Actions</span></Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>{query.data.map(type => <Table.Tr key={type.id}><Table.Td><Group gap="xs">{type.name}{type.abstract && <Badge variant="light" color="gray">Abstract</Badge>}</Group></Table.Td>
          <Table.Td><Text size="sm" c="dimmed">{type.parent_name ?? 'Root'}</Text></Table.Td>
          <Table.Td><EntityTagSummary target={{ id: type.id, name: type.name, version: type.version,
            entityKind: 'object_type' }} edit={() => assign(type)} disabled={!canEdit} compact /></Table.Td>
          <Table.Td>{type.description || <Text c="dimmed">Not set</Text>}</Table.Td>
          <Table.Td><Group gap={2}><Button size="compact-xs" variant="subtle" disabled={!canEdit}
            onClick={() => properties(type)}>Properties</Button>
            <Button size="compact-xs" variant="subtle" disabled={!canEdit}
              onClick={() => stockPolicy(type)}>Stock</Button>
            <IconButton label={`Edit ${type.name}`} disabled={!canEdit} onClick={() => edit(type)}><Pencil size={17} /></IconButton>
            <Button size="compact-xs" variant="subtle" disabled={!canCreate} onClick={() => duplicate(type)}>Duplicate</Button>
            <Button size="compact-xs" variant="subtle" color="red" disabled={!canEdit}
              onClick={() => remove(type)}>Delete</Button></Group></Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>
      : <Empty title="No object types yet" icon={<Layers size={32} />} />}</>
}

function TagsPage({ create, edit, duplicate, parents, remove, canCreate, canEdit }: {
  duplicate: (tag: Tag) => void,
  create: () => void, edit: (tag: Tag) => void, parents: (tag: Tag) => void,
  remove: (tag: Tag) => void,
  canCreate: boolean, canEdit: boolean,
}) {
  const [search, setSearch] = useState('')
  const query = useQuery({ queryKey: ['inventory', 'tags'], queryFn: () => api<Tag[]>('/tags') })
  const normalized = search.trim().toLocaleLowerCase()
  const matches = (query.data ?? []).filter(tag => !normalized || tag.name.toLocaleLowerCase().includes(normalized))
  const roots = matches.filter(tag => tag.parent_ids.length === 0)
  return <><div className="page-title"><div><Text className="eyebrow">CLASSIFICATION</Text><Title order={1}>Tags</Title></div>
    <Button onClick={create} disabled={!canCreate} leftSection={<Plus size={17} />}>New tag</Button></div>
    <div className="list-toolbar"><TextInput aria-label="Search tags" placeholder="Search tags" leftSection={<Search size={17} />}
      value={search} onChange={event => setSearch(event.currentTarget.value)} /></div>
    {query.isPending ? <Loader aria-label="Loading tags" /> : query.error ? <Failure error={query.error} retry={() => void query.refetch()} />
      : !matches.length ? <Empty title={search ? 'No matching tags' : 'No tags yet'} icon={<Tags size={32} />} />
        : search ? <ul className="tag-tree">{matches.map(tag => <TagTreeNode key={tag.id} tag={tag} tags={[]}
          edit={edit} duplicate={duplicate} canCreate={canCreate} parents={parents} remove={remove} canEdit={canEdit} path={[]} />)}</ul>
          : <ul className="tag-tree">{roots.map(tag => <TagTreeNode key={tag.id} tag={tag} tags={query.data}
            edit={edit} duplicate={duplicate} canCreate={canCreate} parents={parents} remove={remove} canEdit={canEdit} path={[]} />)}</ul>}
  </>
}

function TagTreeNode({ tag, tags, edit, duplicate, canCreate, parents, remove, canEdit, path }: {
  duplicate: (tag: Tag) => void, canCreate: boolean,
  tag: Tag, tags: Tag[], edit: (tag: Tag) => void, parents: (tag: Tag) => void,
  remove: (tag: Tag) => void,
  canEdit: boolean, path: string[],
}) {
  const [expanded, setExpanded] = useState(false)
  const groupId = useId()
  const children = tags.filter(candidate => candidate.parent_ids.includes(tag.id))
  const nextPath = [...path, tag.id]
  return <li data-tag-id={tag.id}><div className="tag-row">
    {children.length ? <ActionIcon variant="subtle" color="gray" size="sm" aria-label={`${expanded ? 'Collapse' : 'Expand'} ${tag.name}`}
      aria-expanded={expanded} aria-controls={expanded ? groupId : undefined} onClick={() => setExpanded(!expanded)}>
      {expanded ? <ChevronDown size={17} /> : <ChevronRight size={17} />}</ActionIcon> : <span className="tree-toggle tree-leaf" />}
    <div className="tag-identity"><Text fw={500}>{tag.name}</Text>{tag.description && <Text size="xs" c="dimmed">{tag.description}</Text>}</div>
    <Text size="xs" c="dimmed" className="tag-usage">{tag.direct_object_count} objects · {tag.direct_type_count} types</Text>
    <Tooltip label={`View objects tagged ${tag.name}`}><ActionIcon component={Link} href={`/tag/${tag.id}`} variant="subtle" color="gray"
      aria-label={`View objects tagged ${tag.name}`}><Search size={17} /></ActionIcon></Tooltip>
    <IconButton label={`Edit ${tag.name}`} disabled={!canEdit} onClick={() => edit(tag)}><Pencil size={17} /></IconButton>
    <Button size="compact-xs" variant="subtle" disabled={!canCreate} onClick={() => duplicate(tag)}>Duplicate</Button>
    <IconButton label={`Manage parents of ${tag.name}`} disabled={!canEdit} onClick={() => parents(tag)}><GitBranch size={17} /></IconButton>
    <Button size="compact-xs" variant="subtle" color="red" disabled={!canEdit}
      onClick={() => remove(tag)}>Delete</Button>
  </div>{expanded && children.length > 0 && <ul id={groupId} className="tag-tree tag-children">
    {children.filter(child => !nextPath.includes(child.id)).map(child => <TagTreeNode key={`${nextPath.join(':')}:${child.id}`}
      tag={child} tags={tags} edit={edit} duplicate={duplicate} canCreate={canCreate} parents={parents} remove={remove} canEdit={canEdit} path={nextPath} />)}
  </ul>}</li>
}

function DeleteDefinitionForm({ dialog, busy, disabled, submit }: {
  dialog: Extract<Dialog, { kind: 'definition-delete' }>, busy: boolean,
  disabled: boolean, submit: (payload: Payload) => void,
}) {
  const label = dialog.entityKind === 'tag' ? 'tag' : dialog.entityKind === 'object_type'
    ? 'object type' : 'property'
  return <Stack>
    <Alert color="yellow">Delete {label} <strong>{dialog.name}</strong>? This removes it from the active catalog. Existing history is retained.</Alert>
    <Text size="sm">Deletion is blocked when objects, child definitions, assignments, or stored values still depend on it. A type's own tags and property setup are removed with the unused type.</Text>
    <Group justify="end"><Button color="red" loading={busy} disabled={disabled} onClick={() => submit({
      kind: 'definition.delete', entity_id: dialog.entityId, entity_kind: dialog.entityKind,
      expected_version: dialog.version,
    })}>Delete {label}</Button></Group>
  </Stack>
}

function ObjectPicker({ value, onChange, exclude, label = 'Parent object' }: {
  value: string | null, onChange: (value: string | null) => void, exclude?: string, label?: string,
}) {
  const [search, setSearch] = useState('')
  const deferred = useDeferredValue(search)
  const query = useQuery({ queryKey: ['inventory', 'picker', deferred], queryFn: () => api<ObjectPage>(`/objects?query=${encodeURIComponent(deferred)}`) })
  const selected = useQuery({ queryKey: ['inventory', 'object', value], queryFn: () => api<InventoryObject>(`/objects/${value}`), enabled: !!value })
  const objects = new Map((query.data?.items ?? []).map(object => [object.id, object]))
  if (selected.data) objects.set(selected.data.id, selected.data)
  return <Select label={label} placeholder="Site root" searchable clearable value={value} onChange={onChange}
    searchValue={search} onSearchChange={setSearch} nothingFoundMessage={query.isPending ? 'Loading' : 'No matching objects'}
    error={query.error ? errorText(query.error) : undefined} filter={({ options }) => options}
    data={[...objects.values()].filter(object => object.id !== exclude).map(object => ({ value: object.id, label: `${object.name}${object.alias ? ` (${object.alias})` : ''}` }))} />
}

function TagPicker({ value, onChange, exclude, label = 'Tags' }: {
  value: string[], onChange: (value: string[]) => void, exclude?: string, label?: string,
}) {
  const query = useQuery({ queryKey: ['inventory', 'tags'], queryFn: () => api<Tag[]>('/tags') })
  return <MultiSelect label={label} placeholder="None" searchable clearable hidePickedOptions value={value}
    onChange={onChange} disabled={query.isPending}
    error={query.error ? errorText(query.error) : undefined}
    data={(query.data ?? []).filter(tag => tag.id !== exclude).map(tag => ({ value: tag.id, label: tag.name }))} />
}

function TagForm({ dialog, busy, disabled, submit }: {
  dialog: TagDialog, busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const tag = 'tag' in dialog ? dialog.tag : null
  const [name, setName] = useState(tag ? (dialog.kind === 'tag-create' ? `${tag.name.slice(0, 153)} (copy)` : tag.name) : '')
  const [description, setDescription] = useState(tag?.description ?? '')
  const [parentIds, setParentIds] = useState<string[]>(tag?.parent_ids ?? [])
  function save(event: FormEvent) {
    event.preventDefault()
    if (dialog.kind === 'tag-create') {
      submit({ kind: 'tag.create', name, description, parent_ids: parentIds })
    } else if (dialog.kind === 'tag-edit') {
      submit({ kind: 'tag.edit', tag_id: dialog.tag.id, expected_version: dialog.tag.version,
        name, description })
    } else {
      submit({ kind: 'tag.parents.set', tag_id: dialog.tag.id,
        expected_version: dialog.tag.version, parent_ids: parentIds })
    }
  }
  return <form onSubmit={save}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    {dialog.kind !== 'tag-parents' && <><TextInput label="Name" value={name}
      onChange={event => setName(event.currentTarget.value)} required maxLength={160} autoFocus />
      <Textarea label="Description" value={description}
        onChange={event => setDescription(event.currentTarget.value)} maxLength={10000} minRows={3} autosize /></>}
    {dialog.kind !== 'tag-edit' && <TagPicker label="Parent tags" value={parentIds} onChange={setParentIds} exclude={tag?.id} />}
    {dialog.kind === 'tag-parents' && <Text size="sm" c="dimmed">Choose parent tags. A tag cannot inherit from its descendants.</Text>}
    <Button type="submit" mt="sm" loading={busy} leftSection={dialog.kind === 'tag-parents' ? <GitBranch size={17} /> : <Plus size={17} />}>
      {dialog.kind === 'tag-create' ? 'Create tag' : dialog.kind === 'tag-edit' ? 'Save tag' : 'Save parents'}</Button>
  </Stack></fieldset></form>
}

function TagAssignmentForm({ target, busy, disabled, submit }: {
  target: AssignmentTarget, busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const resource = target.entityKind === 'object' ? 'objects' : 'types'
  const query = useQuery({ queryKey: ['inventory', 'tags', target.entityKind, target.id],
    queryFn: () => api<EntityTags>(`/${resource}/${target.id}/tags`) })
  if (query.isPending) return <Loader aria-label={`Loading tags for ${target.name}`} />
  if (query.error) return <Failure error={query.error} retry={() => void query.refetch()} />
  return <TagAssignmentEditor target={target} initial={query.data.explicit_tag_ids}
    busy={busy} disabled={disabled} submit={submit} />
}

function TagAssignmentEditor({ target, initial, busy, disabled, submit }: {
  target: AssignmentTarget, initial: string[], busy: boolean, disabled: boolean,
  submit: (payload: Payload) => void,
}) {
  const [tagIds, setTagIds] = useState(initial)
  return <form onSubmit={event => {
    event.preventDefault()
    submit({ kind: 'tags.set', entity_id: target.id, entity_kind: target.entityKind,
      expected_version: target.version, tag_ids: tagIds })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    <Text size="sm">{target.name}</Text>
    <TagPicker value={tagIds} onChange={setTagIds} label="Direct tags" />
    <Text size="xs" c="dimmed">Ancestor tags are inherited automatically.</Text>
    <Button type="submit" mt="sm" loading={busy}>Save tags</Button>
  </Stack></fieldset></form>
}

function ObjectForm({ dialog, busy, disabled, submit }: {
  dialog: ObjectDialog, busy: boolean, disabled: boolean, submit: (payload: Payload, epoch?: number) => void,
}) {
  const object = 'object' in dialog ? dialog.object : null
  const editedType = dialog.kind === 'type' ? dialog.type ?? dialog.copy : undefined
  const copying = (dialog.kind === 'create' && !!object) || (dialog.kind === 'type' && !!dialog.copy)
  const originalName = object?.name ?? editedType?.name ?? ''
  const [name, setName] = useState(copying ? `${originalName.slice(0, 153)} (copy)` : originalName)
  const [description, setDescription] = useState(object?.description ?? editedType?.description ?? '')
  const [parent, setParent] = useState<string | null>(object?.parent_id ?? (dialog.kind === 'create' ? dialog.parentId : null) ?? null)
  const [objectType, setObjectType] = useState<string | null>(object?.object_type_id ?? null)
  const [tagIds, setTagIds] = useState<string[]>('tagIds' in dialog ? dialog.tagIds ?? [] : [])
  const [alias, setAlias] = useState(true)
  const [relation, setRelation] = useState(object?.relation ?? 'contained_in')
  const [parentType, setParentType] = useState<string | null>(editedType?.parent_type_id ?? null)
  const [abstractType, setAbstractType] = useState(editedType?.abstract ?? false)
  const types = useQuery({ queryKey: ['inventory', 'types'], queryFn: () => api<ObjectType[]>('/types'),
    enabled: dialog.kind === 'create' || dialog.kind === 'object-type' || dialog.kind === 'type' })
  const descendants = editedType && types.data ? (() => {
    const result: ObjectType[] = []
    const pending = [editedType.id]
    while (pending.length) {
      const parentId = pending.pop()!
      const children = types.data.filter(type => type.parent_type_id === parentId)
      result.push(...children)
      pending.push(...children.map(type => type.id))
    }
    return result
  })() : []
  const reparenting = !copying && !!editedType && parentType !== editedType.parent_type_id
  function save(event: FormEvent) {
    event.preventDefault()
    if (dialog.kind === 'move') submit({ kind: 'object.move', object_id: dialog.object.id,
      expected_version: dialog.object.version, parent_id: parent, relation: relation as 'contained_in' }, dialog.object.authority_epoch)
    else if (dialog.kind === 'edit') submit({ kind: 'object.edit', object_id: dialog.object.id,
      expected_version: dialog.object.version, name, description }, dialog.object.authority_epoch)
    else if (dialog.kind === 'object-type') submit({ kind: 'object.type.set', object_id: dialog.object.id,
      expected_version: dialog.object.version, object_type_id: objectType }, dialog.object.authority_epoch)
    else if (dialog.kind === 'type' && dialog.type) submit({ kind: 'type.edit', type_id: dialog.type.id,
      expected_version: dialog.type.version, name, description, parent_type_id: parentType, abstract: abstractType })
    else if (dialog.kind === 'type') submit({ kind: 'type.create', name, description,
      parent_type_id: parentType, abstract: abstractType, tag_ids: tagIds,
      copy_properties_from: dialog.copy ? { id: dialog.copy.id, expected_version: dialog.copy.version } : null })
    else submit({ kind: 'object.create', name, description, object_type_id: objectType,
      parent_id: parent, relation: relation as 'contained_in', tag_ids: tagIds, allocate_alias: alias,
      copy_properties_from: object ? { id: object.id, expected_version: object.version } : null })
  }
  return <form onSubmit={save}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    {copying && <Alert color="blue">Creating an independent copy with its own ID. Direct tags and saved property values/defaults are copied; inherited values follow the selected type or parent type. Contents and history are not copied.</Alert>}
    {dialog.kind === 'move' ? <><Text fw={500}>{object?.name}</Text><ObjectPicker value={parent} onChange={setParent} exclude={object?.id} label="Destination" />
      <Select label="Placement" value={relation} onChange={value => setRelation(value ?? 'contained_in')}
        data={[{ value: 'contained_in', label: 'Contained in' }, { value: 'located_in', label: 'Located in' }, { value: 'installed_in', label: 'Installed in' }, { value: 'mounted_in', label: 'Mounted in' }]} /></>
      : dialog.kind === 'object-type' ? <><Text fw={500}>{object?.name}</Text>
        <Select<string> label="Object type" placeholder="Unspecified" value={objectType} onChange={setObjectType} clearable searchable
          data={(types.data ?? []).filter(type => !type.abstract).map(type => ({ value: type.id, label: type.name }))} error={types.error ? errorText(types.error) : undefined} /></>
      : <><TextInput label="Name" value={name} onChange={event => setName(event.currentTarget.value)} required maxLength={160} autoFocus />
        <Textarea label="Description" value={description} onChange={event => setDescription(event.currentTarget.value)} maxLength={10000} minRows={3} autosize /></>}
    {dialog.kind === 'create' && <><Select<string> label="Object type" placeholder="Unspecified" value={objectType} onChange={setObjectType} clearable searchable
      data={(types.data ?? []).filter(type => !type.abstract).map(type => ({ value: type.id, label: type.name }))} error={types.error ? errorText(types.error) : undefined} />
      <ObjectPicker value={parent} onChange={setParent} />
      <Select label="Initial placement" value={relation} onChange={value => setRelation(value ?? 'contained_in')}
        data={[{ value: 'contained_in', label: 'Contained in' }, { value: 'located_in', label: 'Located in' }, { value: 'installed_in', label: 'Installed in' }, { value: 'mounted_in', label: 'Mounted in' }]} />
      <TagPicker value={tagIds} onChange={setTagIds} />
      <Checkbox label="Allocate a local identifier" checked={alias} onChange={event => setAlias(event.currentTarget.checked)} /></>}
    {dialog.kind === 'type' && <><Select<string> label="Parent type" placeholder="Root type" value={parentType}
      onChange={setParentType} clearable searchable disabled={types.isPending}
      data={(types.data ?? []).filter(type => type.id !== dialog.type?.id).map(type => ({ value: type.id, label: type.name }))}
      error={types.error ? errorText(types.error) : undefined} />
      {reparenting && <Alert color={descendants.some(type => type.id === parentType) ? 'red' : 'blue'}>
        {descendants.some(type => type.id === parentType)
          ? 'This parent is a descendant and would create a cycle.'
          : <>This change affects this type{descendants.length ? ` and ${descendants.length} descendant ${descendants.length === 1 ? 'type' : 'types'}` : ''}. Their inherited tags and properties will be recalculated.</>}
      </Alert>}
      <Checkbox label="Abstract type (cannot be assigned directly to an object)" checked={abstractType}
        onChange={event => setAbstractType(event.currentTarget.checked)} />
      {!dialog.type && <TagPicker value={tagIds} onChange={setTagIds} />}</>}
    <Button type="submit" mt="sm" loading={busy} leftSection={dialog.kind === 'move' ? <ArrowRightLeft size={17} /> : <Plus size={17} />}>
      {dialog.kind === 'move' ? 'Move object' : dialog.kind === 'edit' ? 'Save changes'
        : dialog.kind === 'object-type' ? 'Save object type' : dialog.kind === 'type' ? (dialog.type ? 'Save type' : 'Create type') : 'Create object'}</Button>
  </Stack></fieldset></form>
}

function ObjectDetail({ id, siteName, csrfToken, can, disabled, edit, duplicate, move, changeType,
  editTags, editProperty, submit }: {
  id: string, siteName: string, csrfToken: string,
  can: (permission: string) => boolean, disabled: boolean,
  edit: (object: InventoryObject) => void, move: (object: InventoryObject) => void,
  duplicate: (object: InventoryObject) => void,
  changeType: (object: InventoryObject) => void,
  editTags: (object: InventoryObject) => void,
  editProperty: (object: InventoryObject, field: PropertyValue) => void,
  submit: (payload: Payload, epoch?: number) => void,
}) {
  const [tab, setTab] = useState<string | null>('overview')
  const query = useQuery({ queryKey: ['inventory', 'object', id], queryFn: () => api<InventoryObject>(`/objects/${id}`) })
  if (query.isPending) return <Loader aria-label="Loading object" />
  if (query.error) return <Failure error={query.error} retry={() => void query.refetch()} />
  const object = query.data
  return <>
    <Breadcrumbs separator={<ChevronRight size={13} />} className="breadcrumbs">
      <Link href="/"><ArrowLeft size={13} /> Inventory</Link>
      {object.location_path.map(parent => <Link href={`/objects/${parent.id}`} key={parent.id}>{parent.name}</Link>)}
    </Breadcrumbs>
    <div className="page-title detail-title"><div><Text className="eyebrow mono">{object.alias ?? 'UNLABELED'}</Text><Title order={1}>{object.name}</Title>
      <Group gap="xs" mt={7}><Badge variant="light" color="gray">{object.stock ? 'Stock holding' : 'Asset'}</Badge><Text size="sm" c="dimmed">{object.type_name ?? 'Unspecified type'}</Text></Group></div>
      <Group gap="xs"><SubtreeLink object={object} />
        <Button component={Link} href={`/intake/copy/${object.id}`} variant="light"
          disabled={disabled || !can('inventory.create')}>Copy to bulk add</Button>
        <Button variant="light" disabled={disabled || !can('inventory.create')} onClick={() => duplicate(object)}>Duplicate</Button>
        <Button component={Link} href={`/scanner/move/${object.id}`} variant="light"
          leftSection={<ScanLine size={17} />} disabled={disabled || !can('inventory.move')}>Move into here</Button>
        <IconButton label="Change object type" disabled={disabled || !can('inventory.edit')} onClick={() => changeType(object)}><Layers size={18} /></IconButton>
        <IconButton label="Edit object" disabled={disabled || !can('inventory.edit')} onClick={() => edit(object)}><Pencil size={18} /></IconButton>
        <Button variant="light" leftSection={<ArrowRightLeft size={17} />} onClick={() => move(object)} disabled={disabled || !can('inventory.move')}>Move</Button></Group></div>
    <Tabs value={tab} onChange={setTab}>
      <Tabs.List><Tabs.Tab value="overview" leftSection={<Box size={16} />}>Overview</Tabs.Tab><Tabs.Tab value="contents" leftSection={<FolderTree size={16} />}>Contents</Tabs.Tab><Tabs.Tab value="history" leftSection={<History size={16} />}>History</Tabs.Tab></Tabs.List>
      <Tabs.Panel value="overview" pt="xl"><div className="detail-grid">
        <section><Title order={2}>Object record</Title><dl className="record-fields">
          <dt>Location path</dt><dd className="location-value"><MapPin size={15} /><LocationPath path={object.location_path} siteName={siteName} /></dd>
          <dt>Description</dt><dd className="description">{object.description || <Text c="dimmed" size="sm">Not set</Text>}</dd>
          <dt>Tags</dt><dd><EntityTagSummary target={{ id: object.id, name: object.name,
            version: object.version, entityKind: 'object' }} edit={() => editTags(object)}
            disabled={disabled || !can('inventory.edit')} /></dd>
          <dt>Canonical ID</dt><dd className="mono canonical-id">{object.id}</dd>
          <dt>Version</dt><dd>{object.version}</dd><dt>Updated</dt><dd>{formatTime(object.updated_at)}</dd>
        </dl><ObjectProperties objectId={id} canEdit={!disabled && can('inventory.edit')}
          edit={field => editProperty(object, field)} /></section>
        {object.stock && <StockHoldingControls object={object} disabled={disabled || !can('inventory.edit')}
          submit={submit} />}
        {can('label.print') && <LabelPanel object={object} csrfToken={csrfToken}
          disabled={disabled} allocate={() => submit({ kind: 'identifier.allocate',
            object_id: id, expected_version: object.version }, object.authority_epoch)} />}
      </div></Tabs.Panel>
      <Tabs.Panel value="contents" pt="lg">{tab === 'contents' && <TreeLevel parentId={id} parentName={object.name} />}</Tabs.Panel>
      <Tabs.Panel value="history" pt="lg">{tab === 'history' && <ObjectHistory id={id} />}</Tabs.Panel>
    </Tabs>
  </>
}

function ObjectHistory({ id }: { id: string }) {
  const [page, setPage] = useState(1)
  const query = useQuery({ queryKey: ['inventory', 'history', id, page], queryFn: () => api<HistoryEntry[]>(`/objects/${id}/history?limit=50&offset=${(page - 1) * 50}`) })
  const names: Record<string, string> = { 'object.create': 'Object created', 'object.edit': 'Record updated', 'object.type.set': 'Object type changed', 'object.move': 'Object moved', 'tags.set': 'Tags updated', 'identifier.allocate': 'Local identifier allocated' }
  if (query.isPending) return <Loader aria-label="Loading history" />
  if (query.error) return <Failure error={query.error} />
  return <><ol className="history-list">{query.data.map(entry => <li key={entry.id}><span className="history-marker"><History size={16} /></span><div>
    <Text fw={500}>{names[entry.event_type] ?? entry.event_type}</Text><Text size="sm" c="dimmed">{entry.actor ?? 'System'} · {formatTime(entry.occurred_at)} · Version {entry.version}</Text>
    <details><summary>Event data</summary><pre>{JSON.stringify(entry.payload, null, 2)}</pre></details>
  </div></li>)}</ol><Group><Button variant="default" size="xs" disabled={page === 1} onClick={() => setPage(page - 1)}>Newer</Button>
    <Button variant="default" size="xs" disabled={query.data.length < 50} onClick={() => setPage(page + 1)}>Older</Button></Group></>
}
