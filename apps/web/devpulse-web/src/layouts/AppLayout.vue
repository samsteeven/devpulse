<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '@/api/client'
import { dismiss, realtime, startRealtime, stopRealtime } from '@/realtime/notifications'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const queries = useQueryClient()

const links = [
  { to: '/', label: 'Tableau de bord', exact: true },
  { to: '/services', label: 'Services' },
  { to: '/incidents', label: 'Incidents' },
  { to: '/agents', label: 'Agents' },
  { to: '/alerts', label: 'Alertes' },
  { to: '/team', label: 'Équipe' },
]

const organizations = useQuery({ queryKey: ['organizations'], queryFn: authApi.organizations, staleTime: 60_000 })

const switchOrganization = useMutation({
  mutationFn: (id: string) => authApi.switch(id),
  onSuccess: async (session) => {
    await stopRealtime()
    queries.clear()
    auth.open(session)
    connect()
    void router.push({ name: 'dashboard' })
  },
})

/** Jeton à jour pour le hub : renouvelé s'il expire dans moins de 30 s (le hub ne vérifie le jeton qu'à la connexion). */
async function hubToken() {
  if (!auth.session || auth.session.expiresAt - Date.now() < 30_000) await auth.renew()
  return auth.session?.accessToken ?? null
}

function connect() {
  startRealtime(queries, hubToken)
}

async function logout() {
  await stopRealtime()
  await auth.logout()
  // Les données de l'organisation ne doivent pas survivre à la session dans le cache.
  queries.clear()
  void router.push({ name: 'login' })
}

const liveLabel = computed(() => ({
  live: 'Temps réel',
  connecting: 'Connexion…',
  reconnecting: 'Reconnexion…',
  off: 'Hors ligne (rafraîchissement périodique)',
})[realtime.state.value])

const toneClass = { bad: 'border-bad/50', warn: 'border-warn/50', ok: 'border-ok/50', info: 'border-accent/50' }

onMounted(connect)
onBeforeUnmount(() => void stopRealtime())
</script>

<template>
  <div class="min-h-screen">
    <header class="border-b border-line bg-surface">
      <div class="mx-auto flex max-w-6xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
        <RouterLink to="/" class="text-base font-bold tracking-tight">
          Dev<span class="text-accent">Pulse</span>
        </RouterLink>
        <nav class="flex flex-wrap gap-1 text-sm" aria-label="Navigation principale">
          <RouterLink
            v-for="link in links"
            :key="link.to"
            :to="link.to"
            v-bind="link.exact ? { 'exact-active-class': 'bg-raised text-ink' } : { 'active-class': 'bg-raised text-ink' }"
            class="rounded-md px-3 py-1.5 text-muted hover:text-ink"
          >
            {{ link.label }}
          </RouterLink>
        </nav>
        <div class="ml-auto flex flex-wrap items-center gap-3 text-sm">
          <span class="flex items-center gap-1.5 text-xs text-muted" data-testid="realtime" :data-state="realtime.state.value">
            <span aria-hidden="true" :class="realtime.state.value === 'live' ? 'text-ok' : 'text-idle'">●</span>
            {{ liveLabel }}
          </span>
          <select
            v-if="(organizations.data.value?.length ?? 0) > 1"
            class="field !w-auto !py-1"
            aria-label="Organisation"
            :value="auth.session?.organization.id"
            @change="switchOrganization.mutate(($event.target as HTMLSelectElement).value)"
          >
            <option v-for="org in organizations.data.value" :key="org.id" :value="org.id">{{ org.name }}</option>
          </select>
          <span v-else class="text-muted" data-testid="organization">{{ auth.session?.organization.name }}</span>
          <button class="btn-ghost !py-1" @click="logout">Se déconnecter</button>
        </div>
      </div>
    </header>
    <main class="mx-auto max-w-6xl px-4 py-6">
      <RouterView />
    </main>

    <div class="fixed bottom-4 right-4 z-50 flex w-80 flex-col gap-2" aria-live="polite">
      <div
        v-for="item in realtime.toasts.value"
        :key="item.id"
        class="rounded-lg border bg-raised p-3 text-sm shadow-lg"
        :class="toneClass[item.tone]"
        data-testid="toast"
      >
        <div class="flex items-start justify-between gap-2">
          <RouterLink v-if="item.link" :to="item.link" class="hover:text-accent" @click="dismiss(item.id)">{{ item.text }}</RouterLink>
          <span v-else>{{ item.text }}</span>
          <button class="text-muted hover:text-ink" aria-label="Fermer" @click="dismiss(item.id)">×</button>
        </div>
      </div>
    </div>
  </div>
</template>
