import { useEffect, useState } from 'react'

import { Alert, Button, Group, Select, Stack, Switch, Text, TextInput } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'

import { api } from '../../api'
import type { InventoryObject, ObjectType, Payload } from '../../api'

type UnitDimension = {
  id: 'count' | 'volume' | 'length' | 'mass' | 'area'
  label: string
  canonical_unit: string
  units: { id: string, label: string }[]
}

export function StockPolicyForm({ type, busy, disabled, submit }: {
  type: ObjectType, busy: boolean, disabled: boolean, submit: (payload: Payload) => void,
}) {
  const catalog = useQuery({ queryKey: ['property-units'],
    queryFn: () => api<UnitDimension[]>('/property-units') })
  const current = useQuery({ queryKey: ['stock-policy', type.id],
    queryFn: () => api<unknown>(`/types/${type.id}/stock-policy`), retry: false })
  const saved = current.data as { policy_type_id: string, quantity_dimension: UnitDimension['id'],
    canonical_unit: string, granularity: string, allow_negative: boolean } | undefined
  const [dimension, setDimension] = useState<UnitDimension['id']>(saved?.quantity_dimension ?? 'count')
  const [unit, setUnit] = useState(saved?.canonical_unit ?? 'each')
  const [granularity, setGranularity] = useState(saved?.granularity ?? '1')
  const [allowNegative, setAllowNegative] = useState(saved?.allow_negative ?? false)
  const [initialized, setInitialized] = useState(false)
  useEffect(() => {
    if (!saved || initialized) return
    setDimension(saved.quantity_dimension)
    setUnit(saved.canonical_unit)
    setGranularity(saved.granularity)
    setAllowNegative(saved.allow_negative)
    setInitialized(true)
  }, [saved, initialized])
  const selected = catalog.data?.find(item => item.id === dimension)
  const inherited = saved && saved.policy_type_id !== type.id
  return <form onSubmit={event => {
    event.preventDefault()
    submit({ kind: 'stock.policy.set', type_id: type.id, expected_version: type.version,
      quantity_dimension: dimension, unit, granularity, allow_negative: allowNegative })
  }}><fieldset disabled={disabled} className="form-fieldset"><Stack>
    {inherited && <Alert color="blue">This type currently inherits its stock policy. Saving creates its own policy.</Alert>}
    {current.error && !saved && <Text size="sm" c="dimmed">No stock policy is configured yet.</Text>}
    <Select label="Inventory measurement" value={dimension} disabled={catalog.isPending}
      data={(catalog.data ?? []).map(item => ({ value: item.id, label: item.label }))}
      onChange={value => { const next = (value ?? 'count') as UnitDimension['id']; setDimension(next)
        setUnit(catalog.data?.find(item => item.id === next)?.canonical_unit ?? 'each') }} />
    <Select label="Input unit" value={unit} disabled={!selected}
      data={(selected?.units ?? []).map(item => ({ value: item.id, label: item.label }))}
      onChange={value => setUnit(value ?? selected?.canonical_unit ?? 'each')} />
    <TextInput label="Smallest increment" value={granularity} inputMode="decimal"
      description="Stored in the selected measurement's canonical unit." onChange={event => setGranularity(event.currentTarget.value)} required />
    <Switch label="Allow quantities below zero" checked={allowNegative}
      onChange={event => setAllowNegative(event.currentTarget.checked)} />
    <Button type="submit" loading={busy} disabled={catalog.isPending || !selected}>Save stock policy</Button>
  </Stack></fieldset></form>
}

export function StockHoldingControls({ object, disabled, submit }: {
  object: InventoryObject, disabled: boolean, submit: (payload: Payload, epoch?: number) => void,
}) {
  const [amount, setAmount] = useState('')
  const [reason, setReason] = useState('')
  if (!object.stock) return null
  const run = (kind: 'stock.receive' | 'stock.consume' | 'stock.adjust') => {
    if (!amount.trim()) return
    submit({ kind, holding_id: object.id, expected_version: object.version, amount,
      unit: object.stock.canonical_unit, reason }, object.authority_epoch)
  }
  return <section><Text fw={600}>Stock</Text>
    <Text size="xl" fw={700}>{object.stock.quantity} {object.stock.canonical_unit}</Text>
    <Text size="xs" c="dimmed">{object.stock.quantity_dimension} · increment {object.stock.granularity}</Text>
    <Group mt="sm" align="end"><TextInput label={`Amount (${object.stock.canonical_unit})`} value={amount}
      inputMode="decimal" onChange={event => setAmount(event.currentTarget.value)} />
      <TextInput label="Reason" value={reason} onChange={event => setReason(event.currentTarget.value)} />
    </Group><Group mt="xs"><Button size="xs" onClick={() => run('stock.receive')} disabled={disabled || !amount}>Receive</Button>
      <Button size="xs" variant="default" onClick={() => run('stock.consume')} disabled={disabled || !amount}>Consume</Button>
      <Button size="xs" variant="light" onClick={() => run('stock.adjust')} disabled={disabled || !amount}>Adjust</Button>
    </Group>
  </section>
}
