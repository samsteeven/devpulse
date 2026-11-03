import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import IncidentBadges from '@/components/IncidentBadges.vue'

describe('IncidentBadges', () => {
  it('affiche la gravité et l’état en toutes lettres', () => {
    const badges = mount(IncidentBadges, { props: { severity: 'Critical', status: 'Acknowledged' } })

    expect(badges.text()).toContain('Critique')
    expect(badges.text()).toContain('Pris en charge')
    expect(badges.find('[data-severity]').attributes('data-severity')).toBe('Critical')
  })
})