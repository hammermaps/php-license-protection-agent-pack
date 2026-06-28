import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import LoginView        from './views/LoginView.vue'
import DashboardView    from './views/DashboardView.vue'
import LicensesView     from './views/LicensesView.vue'
import ActivationsView  from './views/ActivationsView.vue'
import AuditLogView     from './views/AuditLogView.vue'
import ApiClientsView   from './views/ApiClientsView.vue'
import TelemetryView    from './views/TelemetryView.vue'
import ErrorReportsView from './views/ErrorReportsView.vue'

export const router = createRouter({
  history: createWebHistory('/admin/'),
  routes: [
    { path: '/',              component: DashboardView,    meta: { auth: true  } },
    { path: '/login',         component: LoginView,        meta: { auth: false } },
    { path: '/licenses',      component: LicensesView,     meta: { auth: true  } },
    { path: '/activations',   component: ActivationsView,  meta: { auth: true  } },
    { path: '/audit-log',     component: AuditLogView,     meta: { auth: true  } },
    { path: '/api-clients',   component: ApiClientsView,   meta: { auth: true  } },
    { path: '/telemetry',     component: TelemetryView,    meta: { auth: true  } },
    { path: '/error-reports', component: ErrorReportsView, meta: { auth: true  } },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (to.meta.auth && !auth.isLoggedIn) return '/login'
  if (!to.meta.auth && auth.isLoggedIn) return '/'
})
