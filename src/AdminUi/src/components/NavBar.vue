<script setup lang="ts">
import { useRouter, useRoute } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = useRouter()
const route  = useRoute()
const auth   = useAuthStore()

function logout() {
  auth.logout()
  router.push('/login')
}

const links = [
  { to: '/',             label: 'Dashboard',     icon: 'grid'    },
  { to: '/licenses',     label: 'Licenses',      icon: 'key'     },
  { to: '/activations',  label: 'Activations',   icon: 'monitor' },
  { to: '/api-clients',  label: 'API Clients',   icon: 'terminal'},
  { to: '/audit-log',    label: 'Audit Log',     icon: 'list'    },
  { to: '/telemetry',    label: 'Telemetry',     icon: 'chart'   },
  { to: '/error-reports',label: 'Error Reports', icon: 'alert'   },
]

function isActive(to: string) {
  if (to === '/') return route.path === '/'
  return route.path.startsWith(to)
}
</script>

<template>
  <nav class="sidebar">
    <div class="sidebar-header">
      <div class="sidebar-title">MMProtect</div>
      <div class="sidebar-sub">License Admin</div>
    </div>

    <div class="sidebar-nav">
      <div class="nav-section">Navigation</div>
      <RouterLink
        v-for="link in links"
        :key="link.to"
        :to="link.to"
        class="nav-link"
        :class="{ active: isActive(link.to) }"
      >
        <svg class="nav-icon" viewBox="0 0 20 20" fill="currentColor">
          <!-- grid -->
          <template v-if="link.icon === 'grid'">
            <path d="M5 3a2 2 0 00-2 2v2a2 2 0 002 2h2a2 2 0 002-2V5a2 2 0 00-2-2H5zM5 11a2 2 0 00-2 2v2a2 2 0 002 2h2a2 2 0 002-2v-2a2 2 0 00-2-2H5zM11 5a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2V5zM11 13a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2v-2z"/>
          </template>
          <!-- key -->
          <template v-else-if="link.icon === 'key'">
            <path fill-rule="evenodd" d="M18 8a6 6 0 01-7.743 5.743L10 14l-1 1-1 1H6v2H2v-4l4.257-4.257A6 6 0 1118 8zm-6-4a1 1 0 100 2 2 2 0 012 2 1 1 0 102 0 4 4 0 00-4-4z" clip-rule="evenodd"/>
          </template>
          <!-- monitor -->
          <template v-else-if="link.icon === 'monitor'">
            <path fill-rule="evenodd" d="M3 5a2 2 0 012-2h10a2 2 0 012 2v8a2 2 0 01-2 2h-2.22l.123.489.804.804A1 1 0 0113 17H7a1 1 0 01-.707-1.707l.804-.804L7.22 14H5a2 2 0 01-2-2V5zm5.771 7H5V5h10v7H8.771z" clip-rule="evenodd"/>
          </template>
          <!-- terminal -->
          <template v-else-if="link.icon === 'terminal'">
            <path fill-rule="evenodd" d="M2 5a2 2 0 012-2h12a2 2 0 012 2v10a2 2 0 01-2 2H4a2 2 0 01-2-2V5zm3.293 1.293a1 1 0 011.414 0l3 3a1 1 0 010 1.414l-3 3a1 1 0 01-1.414-1.414L7.586 10 5.293 7.707a1 1 0 010-1.414zM11 12a1 1 0 100 2h3a1 1 0 100-2h-3z" clip-rule="evenodd"/>
          </template>
          <!-- list -->
          <template v-else-if="link.icon === 'list'">
            <path fill-rule="evenodd" d="M3 4a1 1 0 000 2h.01a1 1 0 000-2H3zm3 0a1 1 0 000 2h8a1 1 0 100-2H6zM3 8a1 1 0 000 2h.01a1 1 0 000-2H3zm3 0a1 1 0 000 2h8a1 1 0 100-2H6zM3 12a1 1 0 000 2h.01a1 1 0 000-2H3zm3 0a1 1 0 000 2h8a1 1 0 100-2H6z" clip-rule="evenodd"/>
          </template>
          <!-- chart (telemetry) -->
          <template v-else-if="link.icon === 'chart'">
            <path d="M2 11a1 1 0 011-1h2a1 1 0 011 1v5a1 1 0 01-1 1H3a1 1 0 01-1-1v-5zM8 7a1 1 0 011-1h2a1 1 0 011 1v9a1 1 0 01-1 1H9a1 1 0 01-1-1V7zM14 4a1 1 0 011-1h2a1 1 0 011 1v12a1 1 0 01-1 1h-2a1 1 0 01-1-1V4z"/>
          </template>
          <!-- alert (error reports) -->
          <template v-else-if="link.icon === 'alert'">
            <path fill-rule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clip-rule="evenodd"/>
          </template>
        </svg>
        {{ link.label }}
      </RouterLink>

      <div class="nav-section" style="margin-top:8px">Account</div>
      <button class="nav-link" @click="logout">
        <svg class="nav-icon" viewBox="0 0 20 20" fill="currentColor">
          <path fill-rule="evenodd" d="M3 3a1 1 0 00-1 1v12a1 1 0 001 1h7a1 1 0 100-2H4V5h6a1 1 0 100-2H3zm9.293 3.293a1 1 0 011.414 0l3 3a1 1 0 010 1.414l-3 3a1 1 0 01-1.414-1.414L14.586 11H8a1 1 0 110-2h6.586l-1.293-1.293a1 1 0 010-1.414z" clip-rule="evenodd"/>
        </svg>
        Logout
      </button>
    </div>

    <div class="sidebar-footer">
      <div class="sidebar-url">{{ auth.serverUrl }}</div>
    </div>
  </nav>
</template>
