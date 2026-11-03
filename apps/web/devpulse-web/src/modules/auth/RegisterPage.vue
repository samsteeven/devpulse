<script setup lang="ts">
import { useMutation } from '@tanstack/vue-query'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { authApi, errorMessage } from '@/api/client'
import { useAuthStore } from '@/stores/auth'

const organizationName = ref('')
const email = ref('')
const password = ref('')
const auth = useAuthStore()
const router = useRouter()

const register = useMutation({
  mutationFn: () => authApi.register(email.value, password.value, organizationName.value),
  onSuccess: (session) => {
    auth.open(session)
    void router.push({ name: 'services' })
  },
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center p-4">
    <form class="w-full max-w-sm space-y-4 rounded-lg border border-line bg-surface p-6" @submit.prevent="register.mutate()">
      <h1 class="text-xl font-bold">Créer une organisation</h1>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Nom de l'organisation</span>
        <input v-model="organizationName" class="field" name="organization" autocomplete="organization" required minlength="2" maxlength="100" />
      </label>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Adresse e-mail</span>
        <input v-model="email" class="field" type="email" name="email" autocomplete="username" required />
      </label>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Mot de passe (12 caractères minimum)</span>
        <input v-model="password" class="field" type="password" name="password" autocomplete="new-password" required minlength="12" />
      </label>
      <p v-if="register.isError.value" class="text-sm text-bad" role="alert">{{ errorMessage(register.error.value) }}</p>
      <button class="btn-primary w-full" type="submit" :disabled="register.isPending.value">
        {{ register.isPending.value ? 'Création…' : 'Créer mon organisation' }}
      </button>
      <p class="text-center text-sm text-muted">
        Déjà un compte ?
        <RouterLink to="/login" class="text-accent hover:underline">Se connecter</RouterLink>
      </p>
    </form>
  </div>
</template>
