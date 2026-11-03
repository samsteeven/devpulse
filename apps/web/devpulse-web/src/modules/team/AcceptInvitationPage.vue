<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, errorMessage } from '@/api/client'
import { ROLE_LABELS } from '@/composables/format'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const queries = useQueryClient()
const password = ref('')

const token = computed(() => (typeof route.query.token === 'string' ? route.query.token : ''))
const preview = useQuery({
  queryKey: computed(() => ['invitation', token.value]),
  queryFn: () => authApi.previewInvitation(token.value),
  enabled: computed(() => token.value.length > 0),
  retry: false,
})

const accept = useMutation({
  mutationFn: () => authApi.acceptInvitation(token.value, password.value),
  onSuccess: (session) => {
    // Une autre session était peut-être ouverte dans cet onglet : on repart d'un cache vide.
    queries.clear()
    auth.open(session)
    void router.push({ name: 'dashboard' })
  },
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center p-4">
    <div class="w-full max-w-sm rounded-lg border border-line bg-surface p-6">
      <h1 class="mb-4 text-xl font-bold">Rejoindre une organisation</h1>

      <p v-if="!token || preview.isError.value" class="text-sm text-bad" role="alert">
        Ce lien d'invitation n'est pas valable : il a expiré, a déjà servi ou a été annulé. Demandez-en un nouveau.
      </p>

      <form v-else-if="preview.data.value" class="space-y-4" @submit.prevent="accept.mutate()">
        <p class="text-sm">
          <strong>{{ preview.data.value.organizationName }}</strong> vous invite comme
          <strong>{{ ROLE_LABELS[preview.data.value.role] }}</strong>.
        </p>
        <p class="text-sm text-muted">Compte : {{ preview.data.value.email }}</p>
        <label class="block space-y-1 text-sm">
          <span class="text-muted">
            {{ preview.data.value.accountExists ? 'Mot de passe de votre compte' : 'Choisissez un mot de passe (12 caractères minimum)' }}
          </span>
          <input
            v-model="password"
            class="field"
            type="password"
            required
            :minlength="preview.data.value.accountExists ? 1 : 12"
            :autocomplete="preview.data.value.accountExists ? 'current-password' : 'new-password'"
          />
        </label>
        <p v-if="accept.isError.value" class="text-sm text-bad" role="alert">{{ errorMessage(accept.error.value) }}</p>
        <button class="btn-primary w-full" type="submit" :disabled="accept.isPending.value">
          {{ preview.data.value.accountExists ? 'Rejoindre' : 'Créer mon compte et rejoindre' }}
        </button>
      </form>

      <p v-else class="text-sm text-muted">Vérification du lien…</p>
    </div>
  </div>
</template>
