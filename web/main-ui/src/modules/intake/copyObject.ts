import { api } from '../../api'
import type { EntityTags, InventoryObject } from '../../api'
import type { PropertyValue } from '../../Properties'

export async function loadIntakeCopy(id: string, signal?: AbortSignal) {
  const [object, tags, properties] = await Promise.all([
    api<InventoryObject>(`/objects/${id}`, { signal }),
    api<EntityTags>(`/objects/${id}/tags`, { signal }),
    api<PropertyValue[]>(`/objects/${id}/properties`, { signal }),
  ])
  const values: Record<string, unknown> = {}
  for (const field of properties) {
    if (!field.id || !field.editable || field.local_state === 'inherit') continue
    if (field.local_state === 'unset') values[field.id] = null
    else if (field.type === 'quantity' && field.value && typeof field.value === 'object') {
      const quantity = field.value as Record<string, unknown>
      values[field.id] = { amount: quantity.display_amount ?? quantity.amount,
        unit: quantity.display_unit ?? quantity.unit }
    } else values[field.id] = field.value
  }
  return { object, tagIds: tags.explicit_tag_ids, values }
}
