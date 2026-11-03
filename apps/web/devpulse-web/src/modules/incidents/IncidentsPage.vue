<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { errorMessage, incidentsApi } from '@/api/client'
import IncidentBadges from '@/components/IncidentBadges.vue'
import { failureLabel, formatDateTime, formatDuration } from '@/composables/format'

const filters = [
  { value: 'Active', label: 'En cours' },
  { value: 'Resolved', label: 'Résolus' },
  { value: 'All', label: 'Tous' },
] as const
const filter = ref<(typeof filters)[number]['value']>('Active')

// Le temps réel invalide ces requêtes ; l'interrogation lente n'est qu'un filet de sécurité.
const incidents = useQuery({
  queryKey: computed(() => ['incidents', filter.value]),
  queryFn: () => incidentsApi.list(filter.value),
  refetchInterval: 30_000,
})
const stats = useQuery({ queryKey: ['incident-stats'], queryFn: incidentsApi.stats, refetchInterval: 60_000 })

const tiles = computed(() => [
  { label: 'Ouverts', value: String(stats.data.value?.open ?? 0), tone: 'text-bad', id: 'open' },
  { label: 'Pris en charge', value: String(stats.data.value?.acknowledged ?? 0), tone: 'text-warn', id: 'acknowledged' },
  { label: 'Résolus (30 j)', value: String(stats.data.value?.resolvedInPeriod ?? 0), tone: 'text-ok', id: 'resolved' },
  { label: 'Durée moyenne', value: formatDuration(stats.data.value?.meanTimeToResolveSeconds), tone: 'text-ink', id: 'mttr' },
])
</script>

<template>
  <h1 class="mb-5 text-2xl font-bold">Incidents</h1>

  <dl class="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
    <div v-for="tile in tiles" :key="tile.id" class="rounded-lg border border-line bg-surface p-4">
      <dt class="text-sm text-muted">{{ tile.label }}</dt>
      <dd class="mt-1 text-2xl font-bold tabular-nums" :class="tile.tone" :data-testid="`stat-${tile.id}`">{{ tile.value }}</dd>
    </div>
  </dl>

  <div class="mb-4 flex gap-1 text-sm" role="tablist">
    <button
      v-for="f in filters"
      :key="f.value"
      role="tab"
      :aria-selected="filter === f.value"
      class="rounded-md px-3 py-1.5"
      :class="filter === f.value ? 'bg-raised text-ink' : 'text-muted hover:text-ink'"
      @click="filter = f.value"
    >
      {{ f.label }}
    </button>
  </div>

  <p v-if="incidents.isError.value" class="rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ errorMessage(incidents.error.value) }}
  </p>

  <p v-else-if="incidents.isSuccess.value && incidents.data.value?.length === 0" class="rounded-lg border border-dashed border-line p-10 text-center text-muted">
    {{ filter === 'Active' ? 'Aucun incident en cours.' : 'Aucun incident.' }}
  </p>

  <div v-else-if="incidents.data.value?.length" class="overflow-x-auto rounded-lg border border-line bg-surface">
    <table class="w-full min-w-[720px] text-sm">
      <thead class="border-b border-line text-left text-muted">
        <tr>
          <th scope="col" class="px-4 py-2 font-medium">Incident</th>
          <th scope="col" class="px-4 py-2 font-medium">Gravité et état</th>
          <th scope="col" class="px-4 py-2 font-medium">Détecté</th>
          <th scope="col" class="px-4 py-2 text-right font-medium">Durée</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="incident in incidents.data.value" :key="incident.id" class="border-b border-line last:border-0" data-testid="incident-row">
          <td class="px-4 py-3">
            <RouterLink :to="`/incidents/${incident.id}`" class="font-medium hover:text-accent">{{ incident.title }}</RouterLink>
            <p class="mt-0.5 text-xs text-muted">
              {{ incident.environment }} · {{ failureLabel(incident.failure) }}
              <template v-if="incident.source === 'Agent'"> · agent</template>
              <template v-if="incident.hasAnalysis"> · analyse disponible</template>
            </p>
          </td>
          <td class="px-4 py-3"><IncidentBadges :severity="incident.severity" :status="incident.status" /></td>
          <td class="px-4 py-3 text-muted">{{ formatDateTime(incident.detectedAt) }}</td>
          <td class="px-4 py-3 text-right font-mono tabular-nums">{{ formatDuration(incident.durationSeconds) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
