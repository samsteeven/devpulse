<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { errorMessage, incidentsApi, isNotFound } from '@/api/client'
import IncidentBadges from '@/components/IncidentBadges.vue'
import { failureLabel, formatDateTime, formatDuration, formatMs } from '@/composables/format'
import { useAuthStore } from '@/stores/auth'
import type { IncidentDetails, TimelineEntry } from '@/types/api'

const props = defineProps<{ id: string }>()

const auth = useAuthStore()
const queries = useQueryClient()
const note = ref('')
const resolution = ref('')
const resolving = ref(false)

const details = useQuery({
  queryKey: computed(() => ['incident', props.id]),
  queryFn: () => incidentsApi.get(props.id),
  refetchInterval: 30_000,
})
const incident = computed(() => details.data.value?.incident)

function updated(next: IncidentDetails) {
  queries.setQueryData(['incident', props.id], next)
  void queries.invalidateQueries({ queryKey: ['incidents'] })
  void queries.invalidateQueries({ queryKey: ['incident-stats'] })
}

const acknowledge = useMutation({ mutationFn: () => incidentsApi.acknowledge(props.id), onSuccess: updated })
const resolve = useMutation({
  mutationFn: () => incidentsApi.resolve(props.id, resolution.value),
  onSuccess: (next) => {
    resolving.value = false
    updated(next)
  },
})
const addNote = useMutation({
  mutationFn: () => incidentsApi.addNote(props.id, note.value),
  onSuccess: (next) => {
    note.value = ''
    updated(next)
  },
})

const actionError = computed(() => acknowledge.error.value ?? resolve.error.value ?? addNote.error.value)

const kindLabel: Record<TimelineEntry['kind'], string> = {
  Detected: 'Détection',
  Acknowledged: 'Prise en charge',
  Note: 'Note',
  Analysis: 'Analyse',
  Recovered: 'Retour à la normale',
  Resolved: 'Résolution',
}
</script>

<template>
  <p v-if="details.isError.value" class="rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">
    {{ isNotFound(details.error.value) ? "Cet incident n'existe pas." : errorMessage(details.error.value) }}
  </p>

  <template v-else-if="incident && details.data.value">
    <div class="mb-5 flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 class="text-2xl font-bold">{{ incident.title }}</h1>
        <p class="mt-1 flex flex-wrap items-center gap-3 text-sm text-muted">
          <IncidentBadges :severity="incident.severity" :status="incident.status" data-testid="incident-badges" />
          <span>{{ incident.environment }}</span>
          <RouterLink v-if="incident.source === 'Probe'" :to="`/services/${incident.serviceId}`" class="hover:text-accent">Voir le service</RouterLink>
        </p>
      </div>
      <div v-if="auth.canRespond && incident.status !== 'Resolved'" class="flex gap-2">
        <button v-if="incident.status === 'Open'" class="btn-primary" :disabled="acknowledge.isPending.value" @click="acknowledge.mutate()">
          Prendre en charge
        </button>
        <button class="btn-ghost" @click="resolving = !resolving">Résoudre…</button>
      </div>
    </div>

    <p v-if="actionError" class="mb-4 rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">{{ errorMessage(actionError) }}</p>

    <form v-if="resolving" class="mb-6 space-y-3 rounded-lg border border-line bg-surface p-4" @submit.prevent="resolve.mutate()">
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Note de résolution (facultative)</span>
        <textarea v-model="resolution" class="field" rows="2" maxlength="2000" placeholder="Cause, correctif appliqué…" />
      </label>
      <button class="btn-primary" type="submit" :disabled="resolve.isPending.value">Marquer comme résolu</button>
    </form>

    <dl class="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
      <div class="rounded-lg border border-line bg-surface p-4">
        <dt class="text-sm text-muted" title="Depuis la panne confirmée (deuxième échec consécutif)">Durée depuis la détection</dt>
        <dd class="mt-1 text-xl font-bold tabular-nums">{{ formatDuration(incident.durationSeconds) }}</dd>
      </div>
      <div class="rounded-lg border border-line bg-surface p-4">
        <dt class="text-sm text-muted">Cause constatée</dt>
        <dd class="mt-1 text-sm font-semibold">
          {{ failureLabel(incident.failure) }}<template v-if="details.data.value.httpStatusCode"> ({{ details.data.value.httpStatusCode }})</template>
        </dd>
      </div>
      <div class="rounded-lg border border-line bg-surface p-4">
        <dt class="text-sm text-muted">Échecs avant ouverture</dt>
        <dd class="mt-1 text-xl font-bold tabular-nums">{{ details.data.value.failureCount }}</dd>
      </div>
      <div class="rounded-lg border border-line bg-surface p-4">
        <dt class="text-sm text-muted">Dernier temps mesuré</dt>
        <dd class="mt-1 text-xl font-bold tabular-nums">{{ formatMs(details.data.value.responseTimeMs) }}</dd>
      </div>
    </dl>

    <section class="mb-6 rounded-lg border border-line bg-surface p-5" data-testid="analysis">
      <h2 class="mb-1 text-lg font-semibold">Analyse</h2>
      <template v-if="details.data.value.analysis">
        <p class="mb-3 text-xs text-muted">
          {{ details.data.value.analysis.provider === 'Llm' ? `Proposée par le modèle ${details.data.value.analysis.model ?? ''}` : 'Proposée par les règles locales' }}
          · une piste à vérifier, aucune action n'est lancée automatiquement.
        </p>
        <p class="mb-3">{{ details.data.value.analysis.summary }}</p>
        <div class="grid gap-4 md:grid-cols-2">
          <div>
            <h3 class="mb-1 text-sm font-semibold text-muted">Causes probables</h3>
            <ul class="list-disc space-y-1 pl-5 text-sm">
              <li v-for="cause in details.data.value.analysis.probableCauses" :key="cause">{{ cause }}</li>
            </ul>
          </div>
          <div>
            <h3 class="mb-1 text-sm font-semibold text-muted">Vérifications suggérées</h3>
            <ul class="list-disc space-y-1 pl-5 text-sm">
              <li v-for="action in details.data.value.analysis.suggestedActions" :key="action">{{ action }}</li>
            </ul>
          </div>
        </div>
      </template>
      <p v-else class="text-sm text-muted">En cours de préparation…</p>
    </section>

    <section class="rounded-lg border border-line bg-surface p-5">
      <h2 class="mb-3 text-lg font-semibold">Chronologie</h2>
      <ol class="space-y-3 border-l border-line pl-4" data-testid="timeline">
        <li v-for="entry in details.data.value.timeline" :key="entry.at + entry.kind" class="text-sm" :data-kind="entry.kind">
          <p class="text-xs text-muted">
            {{ formatDateTime(entry.at) }} · {{ kindLabel[entry.kind] }}<template v-if="entry.author"> · {{ entry.author }}</template>
          </p>
          <p class="whitespace-pre-line">{{ entry.message }}</p>
        </li>
      </ol>

      <form v-if="auth.canRespond" class="mt-4 flex gap-2" @submit.prevent="addNote.mutate()">
        <input v-model="note" class="field" maxlength="2000" required placeholder="Ajouter une note à la chronologie" aria-label="Note" />
        <button class="btn-ghost" type="submit" :disabled="addNote.isPending.value || !note.trim()">Ajouter</button>
      </form>
    </section>
  </template>
</template>
