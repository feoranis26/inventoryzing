import { Stack, Text } from '@mantine/core'
import type { LabelTemplate } from '../../api'

export function IntakeLabelPreview({ template, object }: {
  template?: LabelTemplate, object?: { id: string, name: string },
}) {
  if (!template) return null
  const source = `/api/label-templates/${template.id}/preview.svg${object ? `?object_id=${object.id}` : ''}`
  return <Stack gap="xs">
    <Text size="sm" fw={600}>Label preview · {object ? `last created: ${object.name}` : 'sample object'}</Text>
    <img src={source} alt={object ? `Label for ${object.name}` : 'Sample label'}
      style={{ width: 320, maxWidth: '100%', aspectRatio: `${template.width_mm} / ${template.height_mm}`, objectFit: 'contain', background: 'white' }} />
    <Text size="xs" c="dimmed">{object ? 'Shows the last created object with the selected template.' : 'The new object’s ID and fields will appear after adding it.'}</Text>
  </Stack>
}
