<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { isAddress } from 'ethers'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'

interface HistoryEvent {
  timestamp: string
  type: 'issue' | 'transfer' | 'redeem'
  tokenId: string
  sender: string
  recipient: string
  operationId?: string
  txHash: string
  logIndex: number
  blockNumber: number
}
interface Summary {
  type: HistoryEvent['type']
  count: number
}
interface HistoryResult {
  rows: HistoryEvent[]
  summary: Summary[]
  daily: { day: string; count: number }[]
  total: number
  page: number
  pageSize: number
}

const props = defineProps<{ contractAddress: string }>()
const rows = ref<HistoryEvent[]>([])
const summary = ref<Summary[]>([])
const daily = ref<{ day: string; count: number }[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const filterType = ref('')
const filterToken = ref('')
const filterFrom = ref('')
const filterTo = ref('')
const applied = ref({ type: '', token: '', from: '', to: '' })
const error = ref('')
const loading = ref(false)
const initialized = ref(false)
let controller: AbortController | undefined
let timer: ReturnType<typeof setInterval> | undefined
let debounce: ReturnType<typeof setTimeout> | undefined
const connection = new HubConnectionBuilder()
  .withUrl('/hubs/history')
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Error)
  .build()
connection.on('HistoryUpdated', (contract: string) => {
  if (contract.toLowerCase() === props.contractAddress.trim().toLowerCase()) {
    void load(true)
  }
})
async function connectUpdates() {
  if (connection.state !== HubConnectionState.Disconnected) return
  try {
    await connection.start()
  } catch {
    // Polling every five seconds remains available if MQTT or SignalR is unavailable.
  }
}

const labels: Record<HistoryEvent['type'], string> = {
  issue: 'Issue',
  transfer: 'Transfer',
  redeem: 'Redeem',
}
const dailyMax = computed(() =>
  Math.max(1, ...daily.value.map((item) => item.count)),
)
const totalPages = computed(() =>
  Math.max(1, Math.ceil(total.value / pageSize.value)),
)
const counts = computed(() => {
  const result = { issue: 0, transfer: 0, redeem: 0 }
  for (const item of summary.value) result[item.type] = item.count
  return result
})

function query() {
  const q = new URLSearchParams({
    contract: props.contractAddress.trim(),
    page: String(page.value),
    pageSize: String(pageSize.value),
  })
  for (const [key, value] of Object.entries(applied.value)) {
    if (value) q.set(key, value)
  }
  return q
}

async function load(silent = false) {
  controller?.abort()
  if (!isAddress(props.contractAddress.trim())) {
    rows.value = []
    summary.value = []
    daily.value = []
    total.value = 0
    error.value = ''
    initialized.value = false
    loading.value = false
    return
  }
  const request = new AbortController()
  controller = request
  if (!silent) loading.value = true
  try {
    const response = await fetch('/api/events?' + query(), {
      signal: request.signal,
    })
    if (!response.ok) {
      let detail =
        'Could not load history. Check that the backend and Anvil are running.'
      if (response.headers.get('content-type')?.includes('application/json')) {
        const problem = await response.json()
        detail = problem.error || detail
      }
      throw new Error(detail)
    }
    const data = await response.json()
    const result = data as HistoryResult
    rows.value = result.rows
    summary.value = result.summary
    daily.value = result.daily
    total.value = result.total
    initialized.value = true
    error.value = ''
  } catch (problem) {
    if (request.signal.aborted) return
    rows.value = []
    summary.value = []
    daily.value = []
    total.value = 0
    initialized.value = false
    error.value =
      problem instanceof TypeError
        ? 'Cannot connect to the backend. Start the backend and Anvil.'
        : problem instanceof Error
          ? problem.message
          : 'Could not load history.'
  } finally {
    if (controller === request) loading.value = false
  }
}

function apply() {
  const from = filterFrom.value ? new Date(filterFrom.value).toISOString() : ''
  const to = filterTo.value ? new Date(filterTo.value).toISOString() : ''
  if (from && to && from > to) {
    error.value = 'The start date must be before the end date.'
    return
  }
  applied.value = {
    type: filterType.value,
    token: filterToken.value.trim(),
    from,
    to,
  }
  page.value = 1
  void load()
}

function changePage(next: number) {
  page.value = next
  void load()
}

function exportUrl(format: 'csv' | 'json') {
  const q = query()
  q.set('format', format)
  return '/api/events/export?' + q
}

watch(
  () => props.contractAddress,
  () => {
    controller?.abort()
    page.value = 1
    initialized.value = false
    rows.value = []
    summary.value = []
    total.value = 0
    error.value = ''
    clearTimeout(debounce)
    debounce = setTimeout(() => {
      void load()
    }, 400)
  },
)

onMounted(() => {
  void load()
  void connectUpdates()
  timer = setInterval(() => {
    void load(true)
    void connectUpdates()
  }, 5000)
})
onUnmounted(() => {
  controller?.abort()
  void connection.stop()
  clearInterval(timer)
  clearTimeout(debounce)
})
</script>

<template>
  <section class="history" aria-labelledby="history-title">
    <h2 id="history-title">Asset history</h2>
    <p>Contract events update automatically.</p>

    <form class="history-filters" @submit.prevent="apply">
      <label>
        Operation
        <select v-model="filterType">
          <option value="">All</option>
          <option value="issue">Issue</option>
          <option value="transfer">Transfer</option>
          <option value="redeem">Redeem</option>
        </select>
      </label>
      <label>
        Asset ID
        <input
          v-model.trim="filterToken"
          inputmode="numeric"
          placeholder="All"
        />
      </label>
      <label>
        From
        <input v-model="filterFrom" type="datetime-local" />
      </label>
      <label>
        To
        <input v-model="filterTo" type="datetime-local" />
      </label>
      <button type="submit">Filter</button>
      <button type="button" @click="load()">Refresh</button>
    </form>

    <div class="history-summary">
      <span
        >All: <strong>{{ total }}</strong></span
      >
      <span
        >Issues: <strong>{{ counts.issue }}</strong></span
      >
      <span
        >Transfers: <strong>{{ counts.transfer }}</strong></span
      >
      <span
        >Redemptions: <strong>{{ counts.redeem }}</strong></span
      >
    </div>

    <p v-if="!isAddress(contractAddress.trim())" class="history-help">
      Enter a valid contract address above to see its history.
    </p>
    <p v-else-if="error" class="history-error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !initialized" class="history-help">
      Loading history…
    </p>

    <div
      v-if="daily.length"
      class="history-chart"
      aria-label="Number of events by day"
    >
      <h3>Events by day (UTC, up to 14 days with data)</h3>
      <ul>
        <li v-for="item in daily" :key="item.day">
          <span>{{ item.day }}</span>
          <span class="history-chart-track" aria-hidden="true">
            <span
              :style="{ width: (item.count / dailyMax) * 100 + '%' }"
            ></span>
          </span>
          <strong>{{ item.count }}</strong>
        </li>
      </ul>
    </div>

    <div class="history-table-wrap">
      <table class="history-table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Operation</th>
            <th>ID</th>
            <th>From</th>
            <th>Recipient</th>
            <th>Operation ID</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in rows" :key="item.txHash + ':' + item.logIndex">
            <td>{{ new Date(item.timestamp).toLocaleString('en-GB') }}</td>
            <td>{{ labels[item.type] }}</td>
            <td>{{ item.tokenId }}</td>
            <td class="address">
              {{ item.type === 'issue' ? '—' : item.sender }}
            </td>
            <td class="address">{{ item.recipient || '—' }}</td>
            <td class="address">
              {{ item.operationId || item.txHash + ':' + item.logIndex }}
            </td>
          </tr>
          <tr v-if="!rows.length && initialized">
            <td colspan="6">No events match the selected filters.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="history-footer">
      <div class="pagination">
        <button
          type="button"
          :disabled="page <= 1 || loading"
          @click="changePage(page - 1)"
        >
          Previous
        </button>
        <span>Page {{ page }} of {{ totalPages }}</span>
        <label class="page-size">
          Rows
          <select v-model.number="pageSize" @change="changePage(1)">
            <option :value="10">10</option>
            <option :value="20">20</option>
            <option :value="50">50</option>
            <option :value="100">100</option>
          </select>
        </label>
        <button
          type="button"
          :disabled="page >= totalPages || loading"
          @click="changePage(page + 1)"
        >
          Next
        </button>
      </div>
      <div class="history-export">
        <span>Export:</span>
        <a v-if="initialized" :href="exportUrl('csv')">CSV</a>
        <a v-if="initialized" :href="exportUrl('json')">JSON</a>
      </div>
    </div>
  </section>
</template>
