<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { agentsApi, errorMessage } from '@/api/client'
import StatusBadge from '@/components/StatusBadge.vue'
import { formatDuration, formatPercent, formatRelative } from '@/composables/format'
import { useAuthStore } from '@/stores/auth'
import type { AgentCreated, ServiceEnvironment } from '@/types/api'

const auth = useAuthStore()
const queries = useQueryClient()
const adding = ref(false)
const name = ref('')
const environment = ref<ServiceEnvironment>('Production')
const created = ref<AgentCreated | null>(null)

const agents = useQuery({ queryKey: ['agents'], queryFn: agentsApi.list, refetchInterval: 15_000 })

const register = useMutation({
  mutationFn: () => agentsApi.register(name.value, environment.value),
  onSuccess: (result) => {
    created.value = result
    adding.value = false
    name.value = ''
    void queries.invalidateQueries({ queryKey: ['agents'] })
  },
})
const revoke = useMutation({ mutationFn: agentsApi.revoke, onSuccess: () => void queries.invalidateQueries({ queryKey: ['agents'] }) })

const command = computed(() =>
  created.value
    ? `DevPulse__Url=${window.location.origin} DevPulse__AgentKey=${created.value.key} dotnet DevPulse.Agent.dll`
    : '',
)

function gauge(value: number | null) {
  if (value === null) return 'bg-idle/40'
  return value >= 90 ? 'bg-bad' : value >= 75 ? 'bg-warn' : 'bg-ok'
}
</script>

<template>
  <div class="mb-2 flex flex-wrap items-center justify-between gap-3">
    <h1 class="text-2xl font-bold">Agents</h1>
    <button v-if="auth.canManageServices && !adding" class="btn-primary" @click="adding = true">Installer un agent</button>
  </div>
  <p class="mb-5 max-w-3xl text-sm text-muted">
    Un agent tourne sur vos machines et envoie un battement avec la charge du processeur, la mémoire et le disque. Il voit ce que les
    sondes de DevPulse ne peuvent pas atteindre (réseau privé). S'il se tait pendant trois battements, un incident s'ouvre.
  </p>

  <form v-if="adding" class="mb-6 flex flex-wrap items-end gap-3 rounded-lg border border-line bg-surface p-5" @submit.prevent="register.mutate()">
    <label class="block min-w-64 flex-1 space-y-1 text-sm">
      <span class="text-muted">Nom de la machine ou du rôle</span>
      <input v-model="name" class="field" required maxlength="100" placeholder="Serveur de paie" />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Environnement</span>
      <select v-model="environment" class="field">
        <option>Production</option>
        <option>Staging</option>
        <option>Development</option>
      </select>
    </label>
    <button class="btn-primary" type="submit" :disabled="register.isPending.value">Créer la clé</button>
    <button class="btn-ghost" type="button" @click="adding = false">Annuler</button>
    <p v-if="register.isError.value" class="w-full text-sm text-bad" role="alert">{{ errorMessage(register.error.value) }}</p>
  </form>

  <div v-if="created" class="mb-6 rounded-lg border border-warn/50 bg-warn/10 p-4 text-sm" data-testid="agent-key">
    <p class="mb-2 font-semibold">Clé de l'agent « {{ created.agent.name }} » : elle ne sera plus affichée.</p>
    <code class="block break-all rounded bg-canvas p-2 font-mono text-xs">{{ created.key }}</code>
    <p class="mt-3 text-muted">Lancement :</p>
    <code class="block break-all rounded bg-canvas p-2 font-mono text-xs">{{ command }}</code>
    <button class="btn-ghost mt-3 !py-1" @click="created = null">J'ai noté la clé</button>
  </div>

  <p v-if="agents.isError.value" class="rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">{{ errorMessage(agents.error.value) }}</p>
  <p v-else-if="agents.isSuccess.value && agents.data.value?.length === 0 && !adding" class="rounded-lg border border-dashed border-line p-10 text-center text-muted">
    Aucun agent installé.
  </p>

  <div v-else-if="agents.data.value?.length" class="grid gap-4 md:grid-cols-2">
    <article v-for="agent in agents.data.value" :key="agent.id" class="rounded-lg border border-line bg-surface p-4" data-testid="agent-card">
      <div class="mb-3 flex items-start justify-between gap-2">
        <div>
          <h2 class="font-semibold">{{ agent.name }}</h2>
          <p class="text-xs text-muted">
            {{ agent.environment }} · {{ agent.hostname ?? 'jamais connecté' }}<template v-if="agent.operatingSystem"> · {{ agent.operatingSystem }}</template>
          </p>
        </div>
        <StatusBadge :status="agent.status" />
      </div>
      <dl class="space-y-2 text-sm">
        <div v-for="metric in [
          { label: 'Processeur', value: agent.cpuPercent },
          { label: 'Mémoire', value: agent.memoryPercent },
          { label: 'Disque', value: agent.diskPercent },
        ]" :key="metric.label">
          <div class="flex justify-between"><dt class="text-muted">{{ metric.label }}</dt><dd class="tabular-nums">{{ formatPercent(metric.value) }}</dd></div>
          <div class="mt-1 h-1.5 rounded bg-raised">
            <div class="h-1.5 rounded" :class="gauge(metric.value)" :style="{ width: `${metric.value ?? 0}%` }" />
          </div>
        </div>
      </dl>
      <p class="mt-3 text-xs text-muted">
        Dernier battement {{ formatRelative(agent.lastSeenAt) }} · toutes les {{ agent.intervalSeconds }} s
        <template v-if="agent.uptimeSeconds"> · machine allumée depuis {{ formatDuration(agent.uptimeSeconds) }}</template>
      </p>
      <button v-if="auth.canManageServices" class="btn-ghost mt-3 !py-1 text-bad" @click="revoke.mutate(agent.id)">Révoquer la clé</button>
    </article>
  </div>
</template>
