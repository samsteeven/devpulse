<script setup lang="ts">
import { useMutation } from '@tanstack/vue-query'
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, errorMessage } from '@/api/client'
import { useAuthStore } from '@/stores/auth'

const email = ref('')
const password = ref('')
const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const login = useMutation({
  mutationFn: () => authApi.login(email.value, password.value),
  onSuccess: (session) => {
    auth.open(session)
    // Uniquement un chemin interne : un paramètre « //site.exemple » ne doit pas faire sortir de l'application.
    const redirect = route.query.redirect
    const target = typeof redirect === 'string' && /^\/(?![/\\])/.test(redirect) ? redirect : '/'
    void router.push(target)
  },
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center p-4">
    <form class="w-full max-w-sm space-y-4 rounded-lg border border-line bg-surface p-6" @submit.prevent="login.mutate()">
      <h1 class="text-xl font-bold">Connexion à Dev<span class="text-accent">Pulse</span></h1>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Adresse e-mail</span>
        <input v-model="email" class="field" type="email" name="email" autocomplete="username" required />
      </label>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Mot de passe</span>
        <input v-model="password" class="field" type="password" name="password" autocomplete="current-password" required />
      </label>
      <p v-if="login.isError.value" class="text-sm text-bad" role="alert">{{ errorMessage(login.error.value) }}</p>
      <button class="btn-primary w-full" type="submit" :disabled="login.isPending.value">
        {{ login.isPending.value ? 'Connexion…' : 'Se connecter' }}
      </button>
      <p class="text-center text-sm text-muted">
        Pas encore de compte ?
        <RouterLink to="/register" class="text-accent hover:underline">Créer une organisation</RouterLink>
      </p>
    </form>
  </div>
</template>
