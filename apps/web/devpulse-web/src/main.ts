import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { AxiosError } from 'axios'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import { http } from '@/api/client'
import App from '@/App.vue'
import '@/assets/main.css'
import { router } from '@/router'
import { useAuthStore } from '@/stores/auth'

const app = createApp(App)
app.use(createPinia())

const auth = useAuthStore()

http.interceptors.request.use((config) => {
  if (auth.session) config.headers.Authorization = `Bearer ${auth.session.accessToken}`
  return config
})

http.interceptors.response.use(undefined, (error: unknown) => {
  // Un 401 sur la connexion veut dire « mauvais mot de passe », pas « session expirée ».
  const isLogin = error instanceof AxiosError && error.config?.url?.startsWith('/auth/')
  if (error instanceof AxiosError && error.response?.status === 401 && !isLogin) {
    auth.close()
    void router.push({ name: 'login' })
  }
  return Promise.reject(error)
})

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      // Une erreur 4xx ne se corrige pas en réessayant.
      retry: (failures, error) =>
        failures < 2 && !(error instanceof AxiosError && (error.response?.status ?? 500) < 500),
    },
  },
})

app.use(VueQueryPlugin, { queryClient })
app.use(router)
app.mount('#app')
