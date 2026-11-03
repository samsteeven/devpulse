import { describe, expect, it } from 'vitest'
import { failureLabel, formatMs, formatPercent, formatRelative } from '@/composables/format'

describe('formatRelative', () => {
  const now = Date.parse('2026-11-03T12:00:00Z')

  it.each([
    [null, 'jamais'],
    ['2026-11-03T11:59:48Z', 'il y a 12 s'],
    ['2026-11-03T11:55:00Z', 'il y a 5 min'],
    ['2026-11-03T09:00:00Z', 'il y a 3 h'],
    ['2026-11-01T12:00:00Z', 'il y a 2 j'],
    ['2026-11-03T12:00:05Z', 'il y a 0 s'],
  ])('%s donne %s', (iso, expected) => {
    expect(formatRelative(iso, now)).toBe(expected)
  })
})

describe('valeurs absentes', () => {
  it('affiche un tiret plutôt que 0 quand il n\'y a pas de mesure', () => {
    expect(formatMs(null)).toBe('–')
    expect(formatPercent(null)).toBe('–')
  })

  it('garde une valeur nulle réelle', () => {
    expect(formatMs(0)).toBe('0 ms')
    expect(formatPercent(0)).toBe('0 %')
  })
})

describe('failureLabel', () => {
  it('traduit une cause connue et laisse passer une cause inconnue', () => {
    expect(failureLabel('Timeout')).toBe('Délai dépassé')
    expect(failureLabel('Nouvelle')).toBe('Nouvelle')
    expect(failureLabel(null)).toBeNull()
  })
})
