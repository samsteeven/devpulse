import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import StatusBadge from '@/components/StatusBadge.vue'
import type { HealthStatus } from '@/types/api'

describe('StatusBadge', () => {
  it.each<[HealthStatus, string]>([
    ['Operational', 'Opérationnel'],
    ['Degraded', 'Dégradé'],
    ['Down', 'En panne'],
    ['Unknown', 'En attente'],
  ])('affiche %s en toutes lettres', (status, label) => {
    const badge = mount(StatusBadge, { props: { status } })

    expect(badge.text()).toContain(label)
    expect(badge.attributes('data-status')).toBe(status)
  })
})
