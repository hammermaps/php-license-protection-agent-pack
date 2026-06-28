import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

const LS_KEY = 'mm_api_key'

export const useAuthStore = defineStore('auth', () => {
  const apiKey = ref(localStorage.getItem(LS_KEY) ?? '')

  const isLoggedIn = computed(() => !!apiKey.value)

  // Always the same origin — the UI is served from the license server itself
  const serverUrl = computed(() => window.location.origin)

  function login(key: string) {
    apiKey.value = key
    localStorage.setItem(LS_KEY, key)
  }

  function logout() {
    apiKey.value = ''
    localStorage.removeItem(LS_KEY)
  }

  return { serverUrl, apiKey, isLoggedIn, login, logout }
})
