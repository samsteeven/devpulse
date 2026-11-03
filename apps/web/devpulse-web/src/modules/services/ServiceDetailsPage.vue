<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { errorMessage, isNotFound, monitoringApi, servicesApi } from '@/api/client'
import StatusBadge from '@/components/StatusBadge.vue'
import { failureLabel, formatMs, formatPercent, formatRelative } from '@/composables/format'
import LatencyChart from '@/modules/services/LatencyChart.vue'
import { useAuthStore } from '@/stores/auth'

const props = defineProps<{ id: string }>()

const auth = useAuthStore()
const router = useRouter()
const queries = useQueryClient()
const hours = ref(1)
const confirmingDelete = ref(false)
const periods = [
  { hours: 1, label: '1 h' },
  { hours: 24, label: '24 h' },
  { hours: 168, label: '7 j' },
]

const service = useQuery({ queryKey: computed(() => ['services', props.id]), queryFn: () => servicesApi.get(props.id) })

// Un service tout juste créé n'est pas encore connu de Monitoring : le 404 est normal, le temps d'une synchronisation.
const health = useQuery({
  queryKey: computed(() => ['health', props.id]),
  queryFn: () => monitoringApi.health(props.id),
  refetchInterval: 5000,
})
const metrics = useQuery({
  queryKey: computed(() => ['metrics', props.id, hours.value]),
  queryFn: () => monitoringApi.metrics(props.id, hours.value),
  refetchInterval: 15000,
})

const status = computed(() => health.data.value?.status ?? 'Unknown')
const waitingForMonitoring = computed(() => health.isError.value && isNotFound(health.error.value))

const remove = useMutation({
  mutationFn: () => servicesApi.remove(props.id),
  onSuccess: () => {
    void queries.invalidateQueries({ queryKey: ['services'] })
    void queries.invalidateQueries({ queryKey: ['overview'] })
    void router.push({ name: 'services' })
  },
})
</script>

<template>
  <p v-if="service.isError.value" class="rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ isNotFound(service.error.value) ? 'Ce service n\'existe pas.' : errorMessage(service.error.value) }}
  </p>

  <template v-else-if="service.data.value">
    <div class="mb-5 flex flex-wrap items-start justify-between gap-3">
      <div>
        <div class="flex flex-wrap items-center gap-3">
          <h1 class="text-2xl font-bold">{{ service.data.value.name }}</h1>
          <StatusBadge :status="status" data-testid="service-status" />
        </div>
        <p class="mt-1 break-all font-mono text-xs text-muted">{{ service.data.value.baseUrl }}{{ service.data.value.healthCheckPath }}</p>
        <p class="mt-1 text-sm text-muted">
          {{ service.data.value.environment }} · toutes les {{ service.data.value.checkIntervalSeconds }} s ·
          dernière vérification {{ formatRelative(health.data.value?.lastCheckedAt ?? null) }}
        </p>
      </div>
      <div v-if="auth.canManageServices" class="flex items-center gap-2">
        <template v-if="confirmingDelete">
          <span class="text-sm text-muted">Supprimer ce service ?</span>
          <button class="btn-ghost text-bad" :disabled="remove.isPending.value" @click="remove.mutate()">Confirmer</button>
          <button class="btn-ghost" @click="confirmingDelete = false">Annuler</button>
        </template>
        <button v-else class="btn-ghost" @click="confirmingDelete = true">Supprimer</button>
      </div>
    </div>

    <p v-if="remove.isError.value" class="mb-4 text-sm text-bad" role="alert">{{ errorMessage(remove.error.value) }}</p>

    <p v-if="waitingForMonitoring" class="mb-4 rounded-md border border-line bg-surface p-3 text-sm text-muted">
      Service enregistré. La première vérification démarre à la prochaine synchronisation, dans quelques secondes.
    </p>

    <p v-if="health.data.value?.lastFailure" class="mb-4 rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" data-testid="last-failure">
      {{ failureLabel(health.data.value.lastFailure) }}<template v-if="health.data.value.lastHttpStatusCode"> (HTTP {{ health.data.value.lastHttpStatusCode }})</template>
      · {{ health.data.value.consecutiveFailures }} échec(s) consécutif(s)
    </p>

    <section class="rounded-lg border border-line bg-surface p-4">
      <div class="mb-3 flex items-center justify-between">
        <h2 class="font-semibold">Performance</h2>
        <div class="flex gap-1" role="group" aria-label="Période">
          <button
            v-for="period in periods"
            :key="period.hours"
            class="rounded-md px-2.5 py-1 text-sm"
            :class="hours === period.hours ? 'bg-raised text-ink' : 'text-muted hover:text-ink'"
            :aria-pressed="hours === period.hours"
            @click="hours = period.hours"
          >
            {{ period.label }}
          </button>
        </div>
      </div>

      <dl class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        <div>
          <dt class="text-sm text-muted">Disponibilité</dt>
          <dd class="text-xl font-bold tabular-nums" data-testid="uptime">{{ formatPercent(metrics.data.value?.uptimePercent) }}</dd>
        </div>
        <div>
          <dt class="text-sm text-muted">Moyenne</dt>
          <dd class="text-xl font-bold tabular-nums">{{ formatMs(metrics.data.value?.averageResponseTimeMs) }}</dd>
        </div>
        <div>
          <dt class="text-sm text-muted">P95</dt>
          <dd class="text-xl font-bold tabular-nums">{{ formatMs(metrics.data.value?.p95ResponseTimeMs) }}</dd>
        </div>
        <div>
          <dt class="text-sm text-muted">Vérifications</dt>
          <dd class="text-xl font-bold tabular-nums" data-testid="checks">{{ metrics.data.value?.checks ?? 0 }}</dd>
        </div>
      </dl>

      <LatencyChart v-if="metrics.data.value?.series.length" :points="metrics.data.value.series" />
      <p v-else class="py-10 text-center text-sm text-muted">Pas encore de mesure sur cette période.</p>
    </section>
  </template>
</template>
