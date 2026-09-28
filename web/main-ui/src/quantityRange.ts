export type QuantityRangeInputValue = {
  mode: 'fixed' | 'range', amount?: string, min?: string, max?: string, unit: string,
}

export function quantityRangeInput(value: unknown, defaultUnit: string): QuantityRangeInputValue {
  const quantity = (value ?? {}) as Record<string, unknown>
  const unit = String(quantity.display_unit ?? quantity.unit ?? defaultUnit)
  return quantity.mode === 'range'
    ? { mode: 'range', min: String(quantity.display_lower ?? quantity.min ?? ''),
      max: String(quantity.display_upper ?? quantity.max ?? ''), unit }
    : { mode: 'fixed', amount: String(quantity.display_lower ?? quantity.amount ?? ''), unit }
}
