import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { AxiosError } from 'axios'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import { installSessionRenewal } from '@/api/client'
import App from '@/App.vue'
import '@/assets/main.css'
import { router } from '@/router'
import { useAuthStore } from '@/stores/auth'

const app = createApp(App)
app.use(createPinia())

const auth = useAuthStore()

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      // Une erreur 4xx ne se corrige pas en réessayant.
      retry: (failures, error) => failures < 2 && !(error instanceof AxiosError && (error.response?.status ?? 500) < 500),
    },
  },
})

installSessionRenewal({
  token: () => auth.session?.accessToken ?? null,
  renew: auth.renew,
  expired: () => {
    auth.close()
    queryClient.clear()
    const current = router.currentRoute.value
    if (!current.meta.public) void router.push({ name: 'login', query: { redirect: current.fullPath } })
  },
})

app.use(VueQueryPlugin, { queryClient })
app.use(router)
app.mount('#app')
