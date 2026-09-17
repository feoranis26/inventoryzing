import { useDeferredValue, useState } from 'react'
import { Alert, Button, Modal, Select, Stack, Text } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../../api'
import type { ObjectPage } from '../../api'
import type { ScannerStatus } from '../scanning/ScannerWorkflows'

export function IntakeSourcePicker({ close, select, scannerStatus }: {
  close: () => void, select: (id: string) => void, scannerStatus?: ScannerStatus,
}) {
  const [search, setSearch] = useState('')
  const [id, setId] = useState<string | null>(null)
  const deferred = useDeferredValue(search)
  const objects = useQuery({ queryKey: ['inventory', 'intake-source', deferred],
    queryFn: () => api<ObjectPage>(`/objects?query=${encodeURIComponent(deferred)}`) })
  return <Modal opened onClose={close} title="Scan or select an object" centered>
    <Stack>
      <Text>Scan a label with this terminal’s scanner, or search for an object below.</Text>
      <Alert color={scannerStatus?.error ? 'yellow' : 'blue'}>
        {scannerStatus?.error ?? (scannerStatus?.ready ? 'Ready to receive scans from this terminal.' : 'Connecting to the scanner terminal. You can still select an object below.')}
        {scannerStatus?.takeControl && <Button size="xs" variant="light" onClick={scannerStatus.takeControl}>Take control of this terminal</Button>}
      </Alert>
      <Text size="sm" c="dimmed">Its details will replace the bulk-add inputs. Nothing is created or printed until you click Add.</Text>
      {objects.error && <Alert color="red">{objects.error.message}</Alert>}
      <Select<string> label="Object to copy" searchable clearable autoFocus value={id} onChange={setId}
        searchValue={search} onSearchChange={setSearch} filter={({ options }) => options}
        nothingFoundMessage={objects.isPending ? 'Loading…' : 'No matching objects'}
        data={(objects.data?.items ?? []).map(object => ({ value: object.id,
          label: `${object.name}${object.alias ? ` (${object.alias})` : ''}` }))} />
      <Button disabled={!id} onClick={() => id && select(id)}>Copy to bulk add</Button>
    </Stack>
  </Modal>
}
