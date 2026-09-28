import { expect, test, vi } from 'vitest'
import { randomUuid } from './uuid'

test('creates UUID v4 without the secure-context randomUUID API', () => {
  const getRandomValues = crypto.getRandomValues.bind(crypto)
  vi.stubGlobal('crypto', { getRandomValues })
  try {
    const first = randomUuid()
    expect(first).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(randomUuid()).not.toBe(first)
  } finally {
    vi.unstubAllGlobals()
  }
})
