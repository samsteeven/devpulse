import { createRouter, createWebHistory } from 'vue-router'
import AppLayout from '@/layouts/AppLayout.vue'
import { useAuthStore } from '@/stores/auth'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/modules/auth/LoginPage.vue'), meta: { public: true } },
    { path: '/register', name: 'register', component: () => import('@/modules/auth/RegisterPage.vue'), meta: { public: true } },
    {
      path: '/',
      component: AppLayout,
      children: [
        { path: '', name: 'dashboard', component: () => import('@/modules/dashboard/DashboardPage.vue') },
        { path: 'services', name: 'services', component: () => import('@/modules/services/ServicesPage.vue') },
        { path: 'services/:id', name: 'service-details', component: () => import('@/modules/services/ServiceDetailsPage.vue'), props: true },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (!to.meta.public && !auth.isAuthenticated) {
    auth.close()
    return { name: 'login', query: to.fullPath === '/' ? {} : { redirect: to.fullPath } }
  }
  if (to.meta.public && auth.isAuthenticated) return { name: 'dashboard' }
  return true
})
