<script setup lang="ts">
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { reactive } from 'vue'
import { errorMessage, servicesApi } from '@/api/client'
import type { ServiceRequest } from '@/types/api'

const emit = defineEmits<{ close: [] }>()
const queries = useQueryClient()

const form = reactive<ServiceRequest>({
  name: '',
  environment: 'Production',
  baseUrl: '',
  healthCheckPath: '/health',
  checkIntervalSeconds: 60,
  timeoutSeconds: 10,
})

const create = useMutation({
  mutationFn: () => servicesApi.create(form),
  onSuccess: () => {
    void queries.invalidateQueries({ queryKey: ['services'] })
    void queries.invalidateQueries({ queryKey: ['overview'] })
    emit('close')
  },
})
</script>

<template>
  <form class="mb-6 grid gap-4 rounded-lg border border-line bg-surface p-5 md:grid-cols-2" @submit.prevent="create.mutate()">
    <h2 class="text-lg font-semibold md:col-span-2">Nouveau service</h2>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Nom</span>
      <input v-model="form.name" class="field" name="name" required minlength="2" maxlength="100" placeholder="Payment API" />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Environnement</span>
      <select v-model="form.environment" class="field" name="environment">
        <option value="Production">Production</option>
        <option value="Staging">Staging</option>
        <option value="Development">Development</option>
      </select>
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">URL de base</span>
      <input v-model="form.baseUrl" class="field" name="baseUrl" type="url" required placeholder="https://api.example.com" />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Chemin de vérification</span>
      <input v-model="form.healthCheckPath" class="field" name="healthCheckPath" required />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Intervalle (secondes, 30 minimum)</span>
      <input v-model.number="form.checkIntervalSeconds" class="field" name="interval" type="number" min="30" max="3600" required />
    </label>
    <label class="block space-y-1 text-sm">
      <span class="text-muted">Délai d'attente (secondes)</span>
      <input v-model.number="form.timeoutSeconds" class="field" name="timeout" type="number" min="1" max="30" required />
    </label>
    <p v-if="create.isError.value" class="text-sm text-bad md:col-span-2" role="alert">{{ errorMessage(create.error.value) }}</p>
    <div class="flex gap-2 md:col-span-2">
      <button class="btn-primary" type="submit" :disabled="create.isPending.value">
        {{ create.isPending.value ? 'Enregistrement…' : 'Enregistrer' }}
      </button>
      <button class="btn-ghost" type="button" @click="emit('close')">Annuler</button>
    </div>
  </form>
</template>
