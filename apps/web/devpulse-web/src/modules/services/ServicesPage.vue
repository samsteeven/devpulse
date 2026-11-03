<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { errorMessage, monitoringApi, servicesApi } from '@/api/client'
import StatusBadge from '@/components/StatusBadge.vue'
import ServiceForm from '@/modules/services/ServiceForm.vue'
import { useAuthStore } from '@/stores/auth'
import type { HealthStatus } from '@/types/api'

const auth = useAuthStore()
const adding = ref(false)

const services = useQuery({ queryKey: ['services'], queryFn: servicesApi.list })
const overview = useQuery({ queryKey: ['overview'], queryFn: monitoringApi.overview, refetchInterval: 5000 })

const statusById = computed(() => new Map(overview.data.value?.services.map((s) => [s.serviceId, s.status])))

function statusOf(id: string): HealthStatus {
  return statusById.value.get(id) ?? 'Unknown'
}
</script>

<template>
  <div class="mb-5 flex flex-wrap items-center justify-between gap-3">
    <h1 class="text-2xl font-bold">Services</h1>
    <button v-if="auth.canManageServices && !adding" class="btn-primary" @click="adding = true">Ajouter un service</button>
  </div>

  <ServiceForm v-if="adding" @close="adding = false" />

  <p v-if="services.isError.value" class="rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ errorMessage(services.error.value) }}
  </p>

  <p v-if="services.isSuccess.value && services.data.value?.length === 0 && !adding" class="rounded-lg border border-dashed border-line p-10 text-center text-muted">
    Aucun service enregistré. Ajoutez une API pour lancer sa surveillance.
  </p>

  <div v-else-if="services.data.value?.length" class="overflow-x-auto rounded-lg border border-line bg-surface">
    <table class="w-full min-w-[640px] text-sm">
      <thead class="border-b border-line text-left text-muted">
        <tr>
          <th scope="col" class="px-4 py-2 font-medium">Nom</th>
          <th scope="col" class="px-4 py-2 font-medium">Environnement</th>
          <th scope="col" class="px-4 py-2 font-medium">Cible</th>
          <th scope="col" class="px-4 py-2 text-right font-medium">Intervalle</th>
          <th scope="col" class="px-4 py-2 font-medium">État</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="service in services.data.value" :key="service.id" class="border-b border-line last:border-0" data-testid="service-row">
          <td class="px-4 py-3">
            <RouterLink :to="`/services/${service.id}`" class="font-medium hover:text-accent">{{ service.name }}</RouterLink>
          </td>
          <td class="px-4 py-3 text-muted">{{ service.environment }}</td>
          <td class="break-all px-4 py-3 font-mono text-xs text-muted">{{ service.baseUrl }}{{ service.healthCheckPath }}</td>
          <td class="px-4 py-3 text-right tabular-nums">{{ service.checkIntervalSeconds }} s</td>
          <td class="px-4 py-3"><StatusBadge :status="statusOf(service.id)" /></td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
