import { createRouter, createWebHistory } from 'vue-router'
import AppLayout from '@/layouts/AppLayout.vue'
import { useAuthStore } from '@/stores/auth'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/modules/auth/LoginPage.vue'), meta: { public: true } },
    { path: '/register', name: 'register', component: () => import('@/modules/auth/RegisterPage.vue'), meta: { public: true } },
    // Accessible connecté ou non : on peut accepter une invitation depuis une autre session.
    { path: '/invitations/accept', name: 'accept-invitation', component: () => import('@/modules/team/AcceptInvitationPage.vue'), meta: { public: true, anyone: true } },
    {
      path: '/',
      component: AppLayout,
      children: [
        { path: '', name: 'dashboard', component: () => import('@/modules/dashboard/DashboardPage.vue') },
        { path: 'services', name: 'services', component: () => import('@/modules/services/ServicesPage.vue') },
        { path: 'services/:id', name: 'service-details', component: () => import('@/modules/services/ServiceDetailsPage.vue'), props: true },
        { path: 'incidents', name: 'incidents', component: () => import('@/modules/incidents/IncidentsPage.vue') },
        { path: 'incidents/:id', name: 'incident-details', component: () => import('@/modules/incidents/IncidentDetailsPage.vue'), props: true },
        { path: 'alerts', name: 'alerts', component: () => import('@/modules/alerts/AlertsPage.vue') },
        { path: 'agents', name: 'agents', component: () => import('@/modules/agents/AgentsPage.vue') },
        { path: 'team', name: 'team', component: () => import('@/modules/team/TeamPage.vue') },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

let restored = false

router.beforeEach(async (to) => {
  const auth = useAuthStore()

  // Premier affichage : la session éventuelle est reprise à partir du cookie de renouvellement.
  if (!restored) {
    restored = true
    if (!auth.isAuthenticated) await auth.renew()
  }

  if (to.meta.anyone) return true
  if (!to.meta.public && !auth.isAuthenticated) {
    return { name: 'login', query: to.fullPath === '/' ? {} : { redirect: to.fullPath } }
  }
  if (to.meta.public && auth.isAuthenticated) return { name: 'dashboard' }
  return true
})
