<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { reactive, ref } from 'vue'
import { alertsApi, errorMessage } from '@/api/client'
import { EVENT_LABELS, formatDateTime } from '@/composables/format'
import { useAuthStore } from '@/stores/auth'
import type { AlertEvent, ChannelRequest, ChannelResponse, ServiceEnvironment, TestResult } from '@/types/api'

const auth = useAuthStore()
const queries = useQueryClient()
const adding = ref(false)
const revealed = ref<ChannelResponse | null>(null)
const tests = ref<Record<string, TestResult | 'pending'>>({})

const allEvents: AlertEvent[] = ['IncidentCreated', 'IncidentAcknowledged', 'IncidentResolved']
const allEnvironments: ServiceEnvironment[] = ['Production', 'Staging', 'Development']

const form = reactive<ChannelRequest>({ name: '', kind: 'Webhook', url: '', events: ['IncidentCreated', 'IncidentResolved'], environments: [] })

const channels = useQuery({ queryKey: ['channels'], queryFn: alertsApi.channels })
const deliveries = useQuery({ queryKey: ['deliveries'], queryFn: alertsApi.deliveries, refetchInterval: 15_000 })

const create = useMutation({
  mutationFn: () => alertsApi.create(form),
  onSuccess: (channel) => {
    revealed.value = channel
    adding.value = false
    Object.assign(form, { name: '', url: '' })
    void queries.invalidateQueries({ queryKey: ['channels'] })
  },
})

const remove = useMutation({
  mutationFn: (id: string) => alertsApi.remove(id),
  onSuccess: () => void queries.invalidateQueries({ queryKey: ['channels'] }),
})

async function test(id: string) {
  tests.value = { ...tests.value, [id]: 'pending' }
  try {
    tests.value = { ...tests.value, [id]: await alertsApi.test(id) }
  } catch (error) {
    tests.value = { ...tests.value, [id]: { success: false, statusCode: null, error: errorMessage(error) } }
  }
}

const statusTone = { Sent: 'text-ok', Pending: 'text-warn', Failed: 'text-bad' }
const statusLabel = { Sent: 'Envoyée', Pending: 'En attente', Failed: 'Abandonnée' }
</script>

<template>
  <div class="mb-5 flex flex-wrap items-center justify-between gap-3">
    <h1 class="text-2xl font-bold">Alertes</h1>
    <button v-if="auth.canManageAlerts && !adding" class="btn-primary" @click="adding = true">Ajouter un canal</button>
  </div>

  <div v-if="revealed?.signingSecret" class="mb-6 rounded-lg border border-warn/50 bg-warn/10 p-4 text-sm" role="status" data-testid="signing-secret">
    <p class="mb-2 font-semibold">Clé de signature du canal « {{ revealed.name }} » : elle ne sera plus affichée.</p>
    <code class="block break-all rounded bg-canvas p-2 font-mono text-xs">{{ revealed.signingSecret }}</code>
    <p class="mt-2 text-muted">
      Chaque envoi porte <code>X-DevPulse-Signature: sha256=…</code>, le HMAC-SHA256 de <code>horodatage.corps</code> avec cette clé
      (horodatage dans <code>X-DevPulse-Timestamp</code>). Rejetez un message dont la signature ne correspond pas ou qui date de plus de cinq minutes.
    </p>
    <button class="btn-ghost mt-3 !py-1" @click="revealed = null">J'ai noté la clé</button>
  </div>

  <form v-if="adding" class="mb-6 grid gap-4 rounded-lg border border-line bg-surface p-5 md:grid-cols-2" @submit.prevent="create.mutate()">
    <h2 class="text-lg font-semibold md:col-span-2">Nouveau canal</h2>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Nom du canal</span>
      <input v-model="form.name" class="field" required maxlength="100" placeholder="Astreinte production" />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Type</span>
      <select v-model="form.kind" class="field">
        <option value="Webhook">Webhook signé (JSON)</option>
        <option value="Slack">Slack (webhook entrant)</option>
      </select>
    </label>
    <label class="block space-y-1 text-sm md:col-span-2">
      <span class="text-muted">URL de destination (https)</span>
      <input v-model="form.url" class="field" type="url" required placeholder="https://hooks.example.com/devpulse" />
    </label>
    <fieldset class="space-y-1 text-sm">
      <legend class="mb-1 text-muted">Événements</legend>
      <label v-for="event in allEvents" :key="event" class="flex items-center gap-2">
        <input v-model="form.events" type="checkbox" :value="event" /> {{ EVENT_LABELS[event] }}
      </label>
    </fieldset>
    <fieldset class="space-y-1 text-sm">
      <legend class="mb-1 text-muted">Environnements (aucun coché : tous)</legend>
      <label v-for="environment in allEnvironments" :key="environment" class="flex items-center gap-2">
        <input v-model="form.environments" type="checkbox" :value="environment" /> {{ environment }}
      </label>
    </fieldset>
    <p v-if="create.isError.value" class="text-sm text-bad md:col-span-2" role="alert">{{ errorMessage(create.error.value) }}</p>
    <div class="flex gap-2 md:col-span-2">
      <button class="btn-primary" type="submit" :disabled="create.isPending.value || form.events.length === 0">Enregistrer</button>
      <button class="btn-ghost" type="button" @click="adding = false">Annuler</button>
    </div>
  </form>

  <p v-if="channels.isError.value" class="mb-4 rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ errorMessage(channels.error.value) }}
  </p>
  <p v-else-if="channels.isSuccess.value && channels.data.value?.length === 0 && !adding" class="mb-6 rounded-lg border border-dashed border-line p-8 text-center text-muted">
    Aucun canal : les incidents restent visibles ici, mais personne n'est prévenu.
  </p>

  <ul v-if="channels.data.value?.length" class="mb-8 space-y-3">
    <li v-for="channel in channels.data.value" :key="channel.id" class="rounded-lg border border-line bg-surface p-4" data-testid="channel">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p class="font-semibold">{{ channel.name }} <span class="ml-1 text-xs font-normal text-muted">{{ channel.kind }}</span></p>
          <p class="break-all font-mono text-xs text-muted">{{ channel.url }}</p>
          <p class="mt-1 text-xs text-muted">
            {{ channel.events.map((e) => EVENT_LABELS[e]).join(', ') }} ·
            {{ channel.environments.length ? channel.environments.join(', ') : 'tous les environnements' }}
          </p>
        </div>
        <div v-if="auth.canManageAlerts" class="flex gap-2">
          <button class="btn-ghost !py-1" :disabled="tests[channel.id] === 'pending'" @click="test(channel.id)">Envoyer un essai</button>
          <button class="btn-ghost !py-1 text-bad" @click="remove.mutate(channel.id)">Supprimer</button>
        </div>
      </div>
      <p v-if="tests[channel.id] && tests[channel.id] !== 'pending'" class="mt-2 text-sm" role="status"
        :class="(tests[channel.id] as TestResult).success ? 'text-ok' : 'text-bad'">
        {{ (tests[channel.id] as TestResult).success
          ? `Reçu (HTTP ${(tests[channel.id] as TestResult).statusCode})`
          : `Échec : ${(tests[channel.id] as TestResult).error}` }}
      </p>
    </li>
  </ul>

  <h2 class="mb-3 text-lg font-semibold">Derniers envois</h2>
  <p v-if="deliveries.isSuccess.value && deliveries.data.value?.length === 0" class="text-sm text-muted">Aucun envoi pour le moment.</p>
  <div v-else-if="deliveries.data.value?.length" class="overflow-x-auto rounded-lg border border-line bg-surface">
    <table class="w-full min-w-[720px] text-sm">
      <thead class="border-b border-line text-left text-muted">
        <tr>
          <th scope="col" class="px-4 py-2 font-medium">Événement</th>
          <th scope="col" class="px-4 py-2 font-medium">Canal</th>
          <th scope="col" class="px-4 py-2 font-medium">État</th>
          <th scope="col" class="px-4 py-2 text-right font-medium" title="Entre le fait signalé et la réception par le destinataire">Délai</th>
          <th scope="col" class="px-4 py-2 font-medium">Date</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="delivery in deliveries.data.value" :key="delivery.id" class="border-b border-line last:border-0" data-testid="delivery-row">
          <td class="px-4 py-3">{{ EVENT_LABELS[delivery.event] }} · {{ delivery.serviceName }}</td>
          <td class="px-4 py-3 text-muted">{{ delivery.channelName }}</td>
          <td class="px-4 py-3" :class="statusTone[delivery.status]">
            {{ statusLabel[delivery.status] }}<template v-if="delivery.attempts > 1"> ({{ delivery.attempts }} essais)</template>
            <span v-if="delivery.lastError" class="block text-xs text-muted">{{ delivery.lastError }}</span>
          </td>
          <td class="px-4 py-3 text-right font-mono tabular-nums">{{ delivery.latencyMs === null ? '–' : `${(delivery.latencyMs / 1000).toFixed(1)} s` }}</td>
          <td class="px-4 py-3 text-muted">{{ formatDateTime(delivery.sentAt ?? delivery.occurredAt) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
