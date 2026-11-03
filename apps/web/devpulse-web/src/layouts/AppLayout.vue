<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const queries = useQueryClient()

function logout() {
  auth.close()
  // Les données de l'organisation ne doivent pas survivre à la session dans le cache.
  queries.clear()
  void router.push({ name: 'login' })
}
</script>

<template>
  <div class="min-h-screen">
    <header class="border-b border-line bg-surface">
      <div class="mx-auto flex max-w-6xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
        <RouterLink to="/" class="text-base font-bold tracking-tight">
          Dev<span class="text-accent">Pulse</span>
        </RouterLink>
        <nav class="flex gap-1 text-sm" aria-label="Navigation principale">
          <RouterLink
            to="/"
            exact-active-class="bg-raised text-ink"
            class="rounded-md px-3 py-1.5 text-muted hover:text-ink"
          >
            Tableau de bord
          </RouterLink>
          <RouterLink
            to="/services"
            active-class="bg-raised text-ink"
            class="rounded-md px-3 py-1.5 text-muted hover:text-ink"
          >
            Services
          </RouterLink>
        </nav>
        <div class="ml-auto flex items-center gap-3 text-sm">
          <span class="text-muted" data-testid="organization">{{ auth.session?.organization.name }}</span>
          <button class="btn-ghost !py-1" @click="logout">Se déconnecter</button>
        </div>
      </div>
    </header>
    <main class="mx-auto max-w-6xl px-4 py-6">
      <RouterView />
    </main>
  </div>
</template>
