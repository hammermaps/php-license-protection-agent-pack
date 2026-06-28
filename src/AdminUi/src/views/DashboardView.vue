<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { fetchStats, type StatsDto } from '../api'

const stats   = ref<StatsDto | null>(null)
const error   = ref('')
const loading = ref(true)

onMounted(async () => {
  try   { stats.value = await fetchStats() }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : 'Failed to load stats.' }
  finally { loading.value = false }
})

function fmt(n: number) { return n.toLocaleString() }
</script>

<template>
  <div>
    <div class="page-header">
      <div>
        <div class="page-title">Dashboard</div>
        <div class="page-subtitle">System overview</div>
      </div>
    </div>

    <div v-if="loading" class="loading"><div class="spinner"></div> Loading…</div>
    <div v-else-if="error" class="alert alert-error">{{ error }}</div>
    <template v-else-if="stats">
      <div class="cards">
        <div class="card">
          <div class="card-label">Total Licenses</div>
          <div class="card-value">{{ fmt(stats.licenses.total) }}</div>
          <div class="card-detail">{{ fmt(stats.licenses.active) }} active · {{ fmt(stats.licenses.revoked) }} revoked</div>
        </div>
        <div class="card">
          <div class="card-label">Builds</div>
          <div class="card-value">{{ fmt(stats.builds.total) }}</div>
          <div class="card-detail">{{ fmt(stats.builds.signed) }} signed · {{ fmt(stats.builds.revoked) }} revoked</div>
        </div>
        <div class="card">
          <div class="card-label">Activations</div>
          <div class="card-value">{{ fmt(stats.activations.total) }}</div>
          <div class="card-detail">{{ fmt(stats.activations.active) }} active · {{ fmt(stats.activations.revoked) }} revoked</div>
        </div>
        <div class="card">
          <div class="card-label">Leases (24h)</div>
          <div class="card-value">{{ fmt(stats.leases.issued24h) }}</div>
          <div class="card-detail">Database: {{ stats.database }}</div>
        </div>
      </div>

      <div class="panel">
        <div class="panel-header">
          <div class="panel-title">Quick links</div>
        </div>
        <div style="padding:20px 24px;display:flex;gap:12px;flex-wrap:wrap">
          <RouterLink to="/licenses"      class="btn btn-outline">View Licenses</RouterLink>
          <RouterLink to="/activations"   class="btn btn-outline">View Activations</RouterLink>
          <RouterLink to="/api-clients"   class="btn btn-outline">Manage API Clients</RouterLink>
          <RouterLink to="/audit-log"     class="btn btn-outline">Audit Log</RouterLink>
          <RouterLink to="/telemetry"     class="btn btn-outline">Telemetry</RouterLink>
          <RouterLink to="/error-reports" class="btn btn-outline">Error Reports</RouterLink>
        </div>
      </div>
    </template>
  </div>
</template>
