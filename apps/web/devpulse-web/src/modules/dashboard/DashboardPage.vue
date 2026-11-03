<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { errorMessage, incidentsApi, monitoringApi } from '@/api/client'
import IncidentBadges from '@/components/IncidentBadges.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { failureLabel, formatDuration, formatMs, formatRelative } from '@/composables/format'

// Le temps réel rafraîchit dès qu'un état change ; l'interrogation reste en filet de sécurité.
const overview = useQuery({
  queryKey: ['overview'],
  queryFn: monitoringApi.overview,
  refetchInterval: 10_000,
})
const incidents = useQuery({
  queryKey: ['incidents', 'Active'],
  queryFn: () => incidentsApi.list('Active'),
  refetchInterval: 30_000,
})

const tiles = computed(() => {
  const data = overview.data.value
  return [
    { label: 'Services', value: data?.total ?? 0, tone: 'text-ink', id: 'total' },
    { label: 'Opérationnels', value: data?.operational ?? 0, tone: 'text-ok', id: 'operational' },
    { label: 'Dégradés', value: data?.degraded ?? 0, tone: 'text-warn', id: 'degraded' },
    { label: 'En panne', value: data?.down ?? 0, tone: 'text-bad', id: 'down' },
  ]
})

// Les pannes d'abord : c'est ce qu'on vient chercher sur cette page.
const severity = { Down: 0, Degraded: 1, Unknown: 2, Operational: 3 }
const services = computed(() =>
  [...(overview.data.value?.services ?? [])].sort(
    (a, b) => severity[a.status] - severity[b.status] || a.name.localeCompare(b.name, 'fr'),
  ),
)
</script>

<template>
  <h1 class="mb-5 text-2xl font-bold">Tableau de bord</h1>

  <p v-if="overview.isError.value" class="mb-4 rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ errorMessage(overview.error.value) }}
  </p>

  <section v-if="incidents.data.value?.length" class="mb-6 rounded-lg border border-bad/40 bg-bad/5 p-4" data-testid="active-incidents">
    <h2 class="mb-2 text-sm font-semibold text-bad">{{ incidents.data.value.length }} incident(s) en cours</h2>
    <ul class="space-y-1 text-sm">
      <li v-for="incident in incidents.data.value" :key="incident.id" class="flex flex-wrap items-center gap-3">
        <RouterLink :to="`/incidents/${incident.id}`" class="font-medium hover:text-accent">{{ incident.title }}</RouterLink>
        <IncidentBadges :severity="incident.severity" :status="incident.status" />
        <span class="text-xs text-muted">depuis {{ formatDuration(incident.durationSeconds) }}</span>
      </li>
    </ul>
  </section>

  <dl class="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
    <div v-for="tile in tiles" :key="tile.id" class="rounded-lg border border-line bg-surface p-4">
      <dt class="text-sm text-muted">{{ tile.label }}</dt>
      <dd class="mt-1 text-3xl font-bold tabular-nums" :class="tile.tone" :data-testid="`tile-${tile.id}`">{{ tile.value }}</dd>
    </div>
  </dl>

  <div v-if="overview.isSuccess.value && services.length === 0" class="rounded-lg border border-dashed border-line p-10 text-center">
    <p class="mb-4 text-muted">Aucun service surveillé pour le moment.</p>
    <RouterLink to="/services" class="btn-primary inline-block">Ajouter un service</RouterLink>
  </div>

  <div v-else-if="services.length" class="overflow-x-auto rounded-lg border border-line bg-surface">
    <table class="w-full min-w-[640px] text-sm">
      <thead class="border-b border-line text-left text-muted">
        <tr>
          <th scope="col" class="px-4 py-2 font-medium">Service</th>
          <th scope="col" class="px-4 py-2 font-medium">État</th>
          <th scope="col" class="px-4 py-2 text-right font-medium">Temps de réponse</th>
          <th scope="col" class="px-4 py-2 font-medium">Dernière vérification</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="service in services" :key="service.serviceId" class="border-b border-line last:border-0" data-testid="service-row">
          <td class="px-4 py-3">
            <RouterLink :to="`/services/${service.serviceId}`" class="font-medium hover:text-accent">{{ service.name }}</RouterLink>
            <span class="ml-2 text-xs text-muted">{{ service.environment }}</span>
            <p v-if="service.lastFailure" class="mt-0.5 text-xs text-bad">
              {{ failureLabel(service.lastFailure) }}<template v-if="service.lastHttpStatusCode"> ({{ service.lastHttpStatusCode }})</template>
              · {{ service.consecutiveFailures }} échec(s) consécutif(s)
            </p>
          </td>
          <td class="px-4 py-3"><StatusBadge :status="service.status" /></td>
          <td class="px-4 py-3 text-right font-mono tabular-nums">{{ service.status === 'Down' ? '–' : formatMs(service.lastResponseTimeMs) }}</td>
          <td class="px-4 py-3 text-muted">{{ formatRelative(service.lastCheckedAt) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
