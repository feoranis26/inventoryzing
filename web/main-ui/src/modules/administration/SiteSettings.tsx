import { useRef, useState } from 'react'
import { Alert, Button, FileButton, Group, Image, Stack, Text, TextInput, Title } from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ImagePlus, Trash2 } from 'lucide-react'
import { api } from '../../api'
import type { Session } from '../../api'

type SiteSettings = {
  id: string, display_name: string, settings_version: number, has_logo: boolean, logo_version?: number | null,
}

const errorText = (error: unknown) => error instanceof Error ? error.message : 'The request could not be completed.'

function base64(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onerror = () => reject(reader.error)
    reader.onload = () => resolve(String(reader.result).split(',', 2)[1] ?? '')
    reader.readAsDataURL(file)
  })
}

export function SiteSettingsPage({ session }: { session: Session }) {
  const cache = useQueryClient()
  const upload = useRef<() => void>(null)
  const [name, setName] = useState('')
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const query = useQuery({ queryKey: ['site-settings'], queryFn: () => api<SiteSettings>('/site/settings') })
  const settings = query.data
  const displayName = name || settings?.display_name || ''
  async function changed(next: SiteSettings) {
    setName('')
    cache.setQueryData(['site-settings'], next)
    cache.setQueryData<Session>(['session'], current => current ? { ...current, site_name: next.display_name } : current)
  }
  async function saveName(event: React.FormEvent) {
    event.preventDefault()
    if (!settings || !displayName.trim() || displayName === settings.display_name) return
    setBusy(true); setError(undefined)
    try {
      await changed(await api<SiteSettings>('/site/settings', { method: 'PUT', headers: { 'X-CSRF-Token': session.csrf_token },
        body: JSON.stringify({ display_name: displayName.trim(), expected_version: settings.settings_version }) }))
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  async function uploadLogo(file: File | null) {
    if (!file || !settings) return
    setBusy(true); setError(undefined)
    try {
      const logo_base64 = await base64(file)
      await changed(await api<SiteSettings>('/site/logo', { method: 'PUT', headers: { 'X-CSRF-Token': session.csrf_token },
        body: JSON.stringify({ logo_base64, expected_version: settings.settings_version }) }))
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  async function removeLogo() {
    if (!settings) return
    setBusy(true); setError(undefined)
    try {
      await changed(await api<SiteSettings>(`/site/logo?expected_version=${settings.settings_version}`, {
        method: 'DELETE', headers: { 'X-CSRF-Token': session.csrf_token },
      }))
    } catch (failure) { setError(failure) } finally { setBusy(false) }
  }
  if (query.isPending) return <Text>Loading site settings…</Text>
  if (query.error || !settings) return <Alert color="red">{errorText(query.error)}</Alert>
  const logo = settings.has_logo ? `/api/site/logo?v=${settings.logo_version}` : undefined
  return <section><div className="page-title"><div><Text className="eyebrow">ADMINISTRATION</Text><Title order={1}>Site settings</Title>
    </div></div>
    <Stack maw={620}>
      {error && <Alert color="red">{errorText(error)}</Alert>}
      <form onSubmit={saveName}><Group align="end"><TextInput label="Site name" value={displayName} onChange={event => setName(event.currentTarget.value)} required maxLength={160} style={{ flex: 1 }} />
        <Button type="submit" loading={busy} disabled={displayName.trim() === settings.display_name}>Save name</Button></Group></form>
      <div><Text fw={600} mb="xs">Site logo</Text><Text size="sm" c="dimmed" mb="sm">Used on labels.</Text>
        {logo ? <Image src={logo} alt={`${settings.display_name} logo`} fit="contain" h={140} w={280} />
          : <Alert color="gray" mb="sm">No logo configured. Labels use the current site name.</Alert>}
        <Group mt="sm"><FileButton resetRef={upload} onChange={uploadLogo} accept="image/png,image/jpeg">
          {props => <Button {...props} loading={busy} leftSection={<ImagePlus size={17} />}>Replace logo</Button>}
        </FileButton>{logo && <Button variant="light" color="red" loading={busy} onClick={() => void removeLogo()} leftSection={<Trash2 size={17} />}>Remove logo</Button>}</Group>
        <Text size="xs" c="dimmed" mt="xs">PNG or JPEG.</Text>
      </div>
    </Stack>
  </section>
}
