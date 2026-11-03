<script setup lang="ts">
import { computed } from 'vue'
import { INCIDENT_STATUS_LABELS, SEVERITY_LABELS } from '@/composables/format'
import type { IncidentSeverity, IncidentStatus } from '@/types/api'

const props = defineProps<{ severity: IncidentSeverity; status: IncidentStatus }>()

const severityClass = computed(() => ({
  Critical: 'text-bad border-bad/40 bg-bad/10',
  Major: 'text-warn border-warn/40 bg-warn/10',
  Minor: 'text-idle border-idle/40 bg-idle/10',
})[props.severity])

const statusClass = computed(() => ({
  Open: 'text-bad',
  Acknowledged: 'text-warn',
  Resolved: 'text-ok',
})[props.status])
</script>

<template>
  <span class="inline-flex flex-wrap items-center gap-2 text-xs font-semibold">
    <span class="rounded-full border px-2 py-0.5" :class="severityClass" :data-severity="severity">{{ SEVERITY_LABELS[severity] }}</span>
    <span :class="statusClass" :data-incident-status="status">{{ INCIDENT_STATUS_LABELS[status] }}</span>
  </span>
</template>
