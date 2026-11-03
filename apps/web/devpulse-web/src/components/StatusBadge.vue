<script setup lang="ts">
import { computed } from 'vue'
import { STATUS_LABELS } from '@/composables/format'
import type { HealthStatus } from '@/types/api'

const props = defineProps<{ status: HealthStatus }>()

// Forme et libellé en plus de la couleur : l'état reste lisible pour un daltonien.
const look = computed(() => ({
  Operational: { color: 'text-ok border-ok/40 bg-ok/10', icon: '●' },
  Degraded: { color: 'text-warn border-warn/40 bg-warn/10', icon: '▲' },
  Down: { color: 'text-bad border-bad/40 bg-bad/10', icon: '■' },
  Unknown: { color: 'text-idle border-idle/40 bg-idle/10', icon: '○' },
})[props.status])
</script>

<template>
  <span
    class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-0.5 text-xs font-semibold"
    :class="look.color"
    :data-status="status"
  >
    <span aria-hidden="true">{{ look.icon }}</span>
    {{ STATUS_LABELS[status] }}
  </span>
</template>
