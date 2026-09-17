import { useEffect, useState } from 'react'

// Browser-local drafts are never submitted or replayed automatically.
export function useIntakeState<T>(scope: string, field: string, initial: T) {
  const key = `inventoryzing.intake.v1.${scope}.${field}`
  const [value, setValue] = useState<T>(() => {
    try {
      const saved = localStorage.getItem(key)
      return saved === null ? initial : JSON.parse(saved) as T
    } catch { return initial }
  })
  useEffect(() => {
    try { localStorage.setItem(key, JSON.stringify(value)) } catch { /* Storage may be unavailable or full. */ }
  }, [key, value])
  return [value, setValue] as const
}
