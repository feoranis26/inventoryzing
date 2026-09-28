import { Group, Select, Stack, TextInput } from '@mantine/core'
import type { QuantityRangeInputValue } from './quantityRange'

export function QuantityRangeInput({ label, value, onChange, units, optional = false }: {
  label: string, value?: QuantityRangeInputValue, onChange: (value: QuantityRangeInputValue | undefined) => void,
  units: { value: string, label: string }[], optional?: boolean,
}) {
  const current = value ?? { mode: 'fixed', amount: '', unit: units[0]?.value ?? '' }
  return <Stack gap="xs">
    <Select label={label} value={value?.mode ?? (optional ? 'inherit' : 'fixed')}
      data={[...(optional ? [{ value: 'inherit', label: 'Use inherited value' }] : []),
        { value: 'fixed', label: 'Fixed value' }, { value: 'range', label: 'Adjustable range' }]}
      onChange={mode => onChange(mode === 'inherit' ? undefined : mode === 'range'
        ? { mode: 'range', min: '', max: '', unit: current.unit }
        : { mode: 'fixed', amount: '', unit: current.unit })} />
    {(!optional || value) && <Group grow align="start">
      {current.mode === 'fixed'
        ? <TextInput label="Amount" inputMode="decimal" required value={current.amount ?? ''}
          onChange={event => onChange({ ...current, amount: event.currentTarget.value })} />
        : <><TextInput label="Minimum" inputMode="decimal" required value={current.min ?? ''}
          onChange={event => onChange({ ...current, min: event.currentTarget.value })} />
          <TextInput label="Maximum" inputMode="decimal" required value={current.max ?? ''}
            onChange={event => onChange({ ...current, max: event.currentTarget.value })} /></>}
      <Select label="Unit" required value={current.unit} data={units}
        onChange={unit => onChange({ ...current, unit: unit ?? '' })} />
    </Group>}
  </Stack>
}
