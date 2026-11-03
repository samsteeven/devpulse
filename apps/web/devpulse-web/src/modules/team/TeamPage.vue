<script setup lang="ts">
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { errorMessage, teamApi } from '@/api/client'
import { formatDateTime, ROLE_LABELS } from '@/composables/format'
import { useAuthStore } from '@/stores/auth'
import type { InvitationCreated, Role } from '@/types/api'

const auth = useAuthStore()
const queries = useQueryClient()
const email = ref('')
const role = ref<Role>('Developer')
const created = ref<InvitationCreated | null>(null)
const copied = ref(false)

// Un administrateur n'attribue pas le rôle de propriétaire.
const assignable = computed<Role[]>(() =>
  auth.session?.organization.role === 'Owner' ? ['Owner', 'Admin', 'DevOps', 'Developer', 'Viewer'] : ['Admin', 'DevOps', 'Developer', 'Viewer'],
)

const members = useQuery({ queryKey: ['members'], queryFn: teamApi.members })
const invitations = useQuery({ queryKey: ['invitations'], queryFn: teamApi.invitations, enabled: computed(() => auth.canManageTeam) })

const invite = useMutation({
  mutationFn: () => teamApi.invite(email.value, role.value),
  onSuccess: (invitation) => {
    created.value = invitation
    copied.value = false
    email.value = ''
    void queries.invalidateQueries({ queryKey: ['invitations'] })
  },
})
const revoke = useMutation({ mutationFn: teamApi.revoke, onSuccess: () => void queries.invalidateQueries({ queryKey: ['invitations'] }) })
const changeRole = useMutation({
  mutationFn: (args: { userId: string; role: Role }) => teamApi.changeRole(args.userId, args.role),
  onSettled: () => void queries.invalidateQueries({ queryKey: ['members'] }),
})
const remove = useMutation({ mutationFn: teamApi.remove, onSuccess: () => void queries.invalidateQueries({ queryKey: ['members'] }) })

const link = computed(() => (created.value ? `${window.location.origin}/invitations/accept?token=${created.value.token}` : ''))
const memberError = computed(() => changeRole.error.value ?? remove.error.value)

async function copy() {
  try {
    await navigator.clipboard.writeText(link.value)
    copied.value = true
  } catch {
    copied.value = false
  }
}
</script>

<template>
  <h1 class="mb-5 text-2xl font-bold">Équipe</h1>

  <section v-if="auth.canManageTeam" class="mb-8 rounded-lg border border-line bg-surface p-5">
    <h2 class="mb-3 text-lg font-semibold">Inviter quelqu'un</h2>
    <form class="flex flex-wrap items-end gap-3" @submit.prevent="invite.mutate()">
      <label class="block min-w-64 flex-1 space-y-1 text-sm">
        <span class="text-muted">Adresse e-mail</span>
        <input v-model="email" class="field" type="email" required maxlength="254" />
      </label>
      <label class="block space-y-1 text-sm">
        <span class="text-muted">Rôle</span>
        <select v-model="role" class="field" aria-label="Rôle de la personne invitée">
          <option v-for="r in assignable" :key="r" :value="r">{{ ROLE_LABELS[r] }}</option>
        </select>
      </label>
      <button class="btn-primary" type="submit" :disabled="invite.isPending.value">Créer le lien</button>
    </form>
    <p v-if="invite.isError.value" class="mt-2 text-sm text-bad" role="alert">{{ errorMessage(invite.error.value) }}</p>

    <div v-if="created" class="mt-4 rounded-md border border-warn/50 bg-warn/10 p-3 text-sm" data-testid="invitation-link">
      <p class="mb-2">
        Lien pour {{ created.email }} ({{ ROLE_LABELS[created.role] }}), valable jusqu'au {{ formatDateTime(created.expiresAt) }}.
        DevPulse n'envoie pas d'e-mail : transmettez-le vous-même. Il ne sera plus affiché.
      </p>
      <code class="block break-all rounded bg-canvas p-2 font-mono text-xs">{{ link }}</code>
      <button class="btn-ghost mt-2 !py-1" @click="copy">{{ copied ? 'Copié' : 'Copier' }}</button>
    </div>

    <div v-if="invitations.data.value?.length" class="mt-5">
      <h3 class="mb-2 text-sm font-semibold text-muted">Invitations en attente</h3>
      <ul class="divide-y divide-line text-sm">
        <li v-for="invitation in invitations.data.value" :key="invitation.id" class="flex items-center justify-between py-2">
          <span>{{ invitation.email }} · {{ ROLE_LABELS[invitation.role] }} · expire le {{ formatDateTime(invitation.expiresAt) }}</span>
          <button class="btn-ghost !py-1" @click="revoke.mutate(invitation.id)">Annuler</button>
        </li>
      </ul>
    </div>
  </section>

  <p v-if="memberError" class="mb-4 rounded-md border border-bad/40 bg-bad/10 p-3 text-sm text-bad" role="alert">{{ errorMessage(memberError) }}</p>

  <div class="overflow-x-auto rounded-lg border border-line bg-surface">
    <table class="w-full min-w-[560px] text-sm">
      <thead class="border-b border-line text-left text-muted">
        <tr>
          <th scope="col" class="px-4 py-2 font-medium">Membre</th>
          <th scope="col" class="px-4 py-2 font-medium">Rôle</th>
          <th scope="col" class="px-4 py-2 font-medium">Depuis</th>
          <th v-if="auth.canManageTeam" scope="col" class="px-4 py-2"><span class="sr-only">Actions</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="member in members.data.value" :key="member.userId" class="border-b border-line last:border-0" data-testid="member-row">
          <td class="px-4 py-3">
            {{ member.email }}<span v-if="member.userId === auth.session?.userId" class="ml-2 text-xs text-muted">(vous)</span>
          </td>
          <td class="px-4 py-3">
            <select
              v-if="auth.canManageTeam && member.userId !== auth.session?.userId && (member.role !== 'Owner' || auth.session?.organization.role === 'Owner')"
              class="field !w-auto !py-1"
              :value="member.role"
              :aria-label="`Rôle de ${member.email}`"
              @change="changeRole.mutate({ userId: member.userId, role: ($event.target as HTMLSelectElement).value as Role })"
            >
              <option v-for="r in assignable" :key="r" :value="r">{{ ROLE_LABELS[r] }}</option>
            </select>
            <span v-else>{{ ROLE_LABELS[member.role] }}</span>
          </td>
          <td class="px-4 py-3 text-muted">{{ formatDateTime(member.joinedAt) }}</td>
          <td v-if="auth.canManageTeam" class="px-4 py-3 text-right">
            <button v-if="member.userId !== auth.session?.userId" class="btn-ghost !py-1 text-bad" @click="remove.mutate(member.userId)">Retirer</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
