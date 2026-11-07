<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import {
  createIdentity,
  listIdentities,
  type LocalIdentity,
} from '../cluster-identity'
import {
  signOperation,
  type ClusterAction,
  type SignedOperation,
} from '../cluster-signing'

type NodeId = 'a' | 'b' | 'c'
type Action = ClusterAction
interface NodeStatus {
  id: NodeId
  role: string
  leader: string | null
  term: number
  commitIndex: number
  ready: boolean
}
interface Asset {
  id: string
  owner: string
  status: 'ACTIVE' | 'REDEEMED'
}
interface OperationResult {
  status: 'SUCCESS' | 'REJECTED' | 'PENDING'
  message: string
}

const nodes: NodeId[] = ['a', 'b', 'c']
const statuses = ref<Partial<Record<NodeId, NodeStatus>>>({})
const selectedNode = ref<NodeId>('a')
const identities = ref<LocalIdentity[]>([])
const selectedIdentity = ref('')
const name = ref('')
const assetId = ref('')
const recipient = ref('')
const asset = ref<Asset | null>(null)
const history = ref<SignedOperation[]>([])
const message = ref('')
const messageKind = ref<'success' | 'error' | 'pending' | ''>('')
const busy = ref(false)
const lastSigned = ref<SignedOperation | null>(null)
let timer: ReturnType<typeof setInterval> | undefined
let disposed = false

const activeIdentity = computed(() =>
  identities.value.find((user) => user.address === selectedIdentity.value),
)
const labels: Record<Action, string> = {
  issue: 'Wystawienie',
  transfer: 'Przekazanie',
  redeem: 'Wykorzystanie',
}

function nodeUrl(node: NodeId, path: string) {
  return '/nodes/' + node + path
}
async function getJson<T>(node: NodeId, path: string): Promise<T> {
  const response = await fetch(nodeUrl(node, path), {
    signal: AbortSignal.timeout(5000),
  })
  if (!response.ok)
    throw new Error(
      response.status === 404
        ? 'Nie znaleziono przedmiotu.'
        : 'Węzeł jest niedostępny.',
    )
  return response.json() as Promise<T>
}
async function refreshNodes() {
  const results = await Promise.all(
    nodes.map(async (node) => {
      try {
        return [
          node,
          await getJson<NodeStatus>(node, '/cluster/status'),
        ] as const
      } catch {
        return [node, undefined] as const
      }
    }),
  )
  statuses.value = Object.fromEntries(results) as Partial<
    Record<NodeId, NodeStatus>
  >
}
function canonicalId() {
  const value = assetId.value.trim()
  if (!/^[0-9]{1,39}$/.test(value))
    throw new Error('ID musi być liczbą całkowitą o maksymalnie 39 cyfrach.')
  return BigInt(value).toString()
}
function setMessage(kind: 'success' | 'error' | 'pending', text: string) {
  messageKind.value = kind
  message.value = text
}
async function addUser() {
  busy.value = true
  message.value = ''
  try {
    const user = await createIdentity(name.value)
    identities.value = await listIdentities()
    selectedIdentity.value = user.address
    name.value = ''
    setMessage(
      'success',
      'Użytkownik został utworzony lokalnie w tej przeglądarce.',
    )
  } catch (error) {
    setMessage(
      'error',
      error instanceof Error
        ? error.message
        : 'Nie udało się zapisać użytkownika.',
    )
  } finally {
    busy.value = false
  }
}
async function inspect() {
  busy.value = true
  message.value = ''
  asset.value = null
  history.value = []
  try {
    const id = canonicalId()
    const [current, events] = await Promise.all([
      getJson<Asset>(selectedNode.value, '/cluster/assets/' + id),
      getJson<SignedOperation[]>(selectedNode.value, '/cluster/history/' + id),
    ])
    asset.value = current
    history.value = events
    setMessage(
      'success',
      'SUCCESS: odczytano stan z węzła ' +
        selectedNode.value.toUpperCase() +
        '.',
    )
  } catch (error) {
    setMessage(
      'error',
      'FAILED: ' +
        (error instanceof Error
          ? error.message
          : 'Nie udało się odczytać stanu.'),
    )
  } finally {
    busy.value = false
  }
}
async function send(operation: SignedOperation) {
  const response = await fetch(
    nodeUrl(selectedNode.value, '/cluster/operations'),
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(operation),
      signal: AbortSignal.timeout(12000),
    },
  )
  const result = (await response.json()) as OperationResult
  if (!['SUCCESS', 'REJECTED', 'PENDING'].includes(result.status))
    throw new Error('Nieznany wynik operacji.')
  setMessage(
    result.status === 'SUCCESS'
      ? 'success'
      : result.status === 'REJECTED'
        ? 'error'
        : 'pending',
    result.status + ': ' + result.message,
  )
  await refreshNodes()
  if (result.status === 'SUCCESS') {
    try {
      const id = operation.assetId
      asset.value = await getJson<Asset>(
        selectedNode.value,
        '/cluster/assets/' + id,
      )
      history.value = await getJson<SignedOperation[]>(
        selectedNode.value,
        '/cluster/history/' + id,
      )
    } catch {
      // A follower may need another heartbeat before its local view catches up.
    }
  }
}
async function operate(type: Action) {
  let sent = false
  busy.value = true
  message.value = ''
  try {
    const id = canonicalId()
    const user = activeIdentity.value
    if (!user) throw new Error('Wybierz lokalnego użytkownika.')
    const to = type === 'redeem' ? '' : recipient.value.trim().toLowerCase()
    if (type !== 'redeem' && !/^0x[0-9a-f]{40}$/.test(to))
      throw new Error('Wpisz poprawny adres odbiorcy.')
    await refreshNodes()
    const leader = nodes.find(
      (node) =>
        statuses.value[node]?.role === 'leader' && statuses.value[node]?.ready,
    )
    if (!leader) throw new Error('Brak dostępnego lidera. Sprawdź stan węzłów.')
    const { nonce } = await getJson<{ nonce: number }>(
      leader,
      '/cluster/nonces/' + user.address,
    )
    const operation = await signOperation(user, type, id, to, nonce)
    lastSigned.value = operation
    sent = true
    await send(operation)
  } catch (error) {
    if (sent)
      setMessage(
        'pending',
        'PENDING: brak odpowiedzi. Sprawdź stan przed ponowieniem operacji.',
      )
    else
      setMessage(
        'error',
        'FAILED: ' +
          (error instanceof Error
            ? error.message
            : 'Nie udało się wykonać operacji.'),
      )
  } finally {
    busy.value = false
  }
}
async function replay() {
  if (!lastSigned.value) return
  busy.value = true
  message.value = ''
  try {
    await send(lastSigned.value)
  } catch {
    setMessage(
      'pending',
      'PENDING: nie otrzymano wyniku. Sprawdź stan przed ponowieniem.',
    )
  } finally {
    busy.value = false
  }
}
onMounted(async () => {
  try {
    identities.value = await listIdentities()
    selectedIdentity.value = identities.value[0]?.address || ''
  } catch {
    setMessage(
      'error',
      'Nie można odczytać lokalnych użytkowników przeglądarki.',
    )
  }
  await refreshNodes()
  if (!disposed)
    timer = setInterval(() => {
      void refreshNodes()
    }, 3000)
})
onUnmounted(() => {
  disposed = true
  clearInterval(timer)
})
</script>

<template>
  <div class="shell distributed">
    <header class="topbar">
      <a class="brand" href="/cluster.html">ChainLoot</a>
      <span class="local-label">Sieć lokalna · 3 węzły</span>
    </header>

    <main>
      <div class="distributed-heading">
        <h1>System rozproszony</h1>
        <p>
          Operacje są podpisywane na tym komputerze i zatwierdzane przez
          większość węzłów.
        </p>
      </div>

      <section class="workspace node-section" aria-labelledby="nodes-heading">
        <div class="section-title">
          <div>
            <h2 id="nodes-heading">Węzły</h2>
            <p>
              Wybierz węzeł, przez który chcesz odczytać stan lub wysłać
              operację.
            </p>
          </div>
          <button type="button" class="secondary-button" @click="refreshNodes">
            Odśwież
          </button>
        </div>
        <div class="node-list">
          <label
            v-for="node in nodes"
            :key="node"
            class="node-choice"
            :class="{ selected: selectedNode === node }"
          >
            <input
              v-model="selectedNode"
              type="radio"
              name="node"
              :value="node"
            />
            <span>
              <strong>Węzeł {{ node.toUpperCase() }}</strong>
              <small v-if="statuses[node]">
                {{
                  statuses[node]?.role === 'leader'
                    ? 'Lider'
                    : statuses[node]?.role === 'candidate'
                      ? 'Wybór lidera'
                      : 'Węzeł zapasowy'
                }}
                · zatwierdzono: {{ statuses[node]?.commitIndex }}
              </small>
              <small v-else>Niedostępny</small>
            </span>
          </label>
        </div>
      </section>

      <section
        class="workspace identity-section"
        aria-labelledby="users-heading"
      >
        <div class="section-title">
          <div>
            <h2 id="users-heading">Użytkownicy lokalni</h2>
            <p>
              Klucz prywatny powstaje i pozostaje w tej przeglądarce. Nie jest
              wysyłany do węzłów.
            </p>
          </div>
        </div>
        <form class="identity-form" @submit.prevent="addUser">
          <label for="user-name">Nazwa użytkownika</label>
          <div>
            <input
              id="user-name"
              v-model.trim="name"
              maxlength="40"
              placeholder="np. Alice"
            />
            <button class="connect-button" type="submit" :disabled="busy">
              Dodaj lokalnie
            </button>
          </div>
        </form>
        <p class="identity-note">
          Każdy użytkownik powinien używać osobnego profilu przeglądarki.
          Wyczyszczenie danych przeglądarki usuwa lokalne klucze.
        </p>
        <label for="active-user">Aktywny użytkownik</label>
        <select id="active-user" v-model="selectedIdentity" :disabled="busy">
          <option value="">Wybierz użytkownika</option>
          <option
            v-for="user in identities"
            :key="user.address"
            :value="user.address"
          >
            {{ user.name }} · {{ user.address }}
          </option>
        </select>
        <p v-if="activeIdentity" class="current-address">
          Adres: <code>{{ activeIdentity.address }}</code>
        </p>
      </section>

      <section
        class="workspace distributed-work"
        aria-labelledby="asset-heading"
      >
        <div class="section-title">
          <div>
            <h2 id="asset-heading">Przedmiot</h2>
            <p>Odczytaj stan albo podpisz i wyślij operację.</p>
          </div>
        </div>
        <div class="distributed-grid">
          <div class="form-panel">
            <div class="field">
              <label for="asset-id">ID przedmiotu</label>
              <input
                id="asset-id"
                v-model.trim="assetId"
                inputmode="numeric"
                placeholder="np. 1"
              />
            </div>
            <div class="field">
              <label for="cluster-recipient"
                >Adres odbiorcy
                <span class="optional"
                  >do wystawienia i przekazania</span
                ></label
              >
              <input
                id="cluster-recipient"
                v-model.trim="recipient"
                placeholder="0x..."
                spellcheck="false"
              />
              <button
                v-if="activeIdentity"
                class="use-account"
                type="button"
                @click="recipient = activeIdentity.address"
              >
                Wstaw adres aktywnego użytkownika
              </button>
            </div>
          </div>
          <div class="action-panel">
            <h3>Operacje</h3>
            <button type="button" :disabled="busy" @click="inspect">
              Sprawdź przedmiot
            </button>
            <button type="button" :disabled="busy" @click="operate('issue')">
              Wystaw przedmiot
            </button>
            <button type="button" :disabled="busy" @click="operate('transfer')">
              Przekaż przedmiot
            </button>
            <button type="button" :disabled="busy" @click="operate('redeem')">
              Wykorzystaj przedmiot
            </button>
            <button
              v-if="lastSigned"
              type="button"
              :disabled="busy"
              @click="replay"
            >
              Wyślij ostatnią operację ponownie
            </button>
          </div>
        </div>
        <div
          v-if="message"
          class="notice"
          :class="messageKind === 'success' ? 'success' : 'warning'"
          role="status"
        >
          {{ message }}
        </div>
        <div v-if="asset" class="result">
          <div>
            <span>Przedmiot</span><strong>#{{ asset.id }}</strong>
          </div>
          <div>
            <span>Status</span><strong>{{ asset.status }}</strong>
          </div>
          <div class="owner">
            <span>Właściciel</span><strong>{{ asset.owner }}</strong>
          </div>
        </div>
      </section>

      <section v-if="asset" class="history" aria-labelledby="cluster-history">
        <h2 id="cluster-history">Historia przedmiotu #{{ asset.id }}</h2>
        <p>Kolejność zatwierdzonych operacji według wybranego węzła.</p>
        <div class="history-table-wrap">
          <table class="history-table">
            <thead>
              <tr>
                <th>Operacja</th>
                <th>Nadawca</th>
                <th>Odbiorca</th>
                <th>ID operacji</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="entry in history" :key="entry.operationId">
                <td>{{ labels[entry.type] }}</td>
                <td class="address">{{ entry.sender }}</td>
                <td class="address">{{ entry.recipient || '—' }}</td>
                <td class="address">{{ entry.operationId }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <p class="footnote">
        Stan odczytujesz z wybranego węzła; po awarii możesz wybrać inny.
      </p>
    </main>
  </div>
</template>

<style scoped>
.distributed-heading {
  padding: 36px 0 26px;
}
.distributed-heading h1 {
  margin: 0;
  font-size: 30px;
  line-height: 1.2;
}
.distributed-heading p,
.section-title p,
.identity-note,
.current-address {
  margin: 7px 0 0;
  color: var(--muted);
  font-size: 14px;
}
.section-title {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 16px;
}
.section-title h2 {
  margin: 0;
  font-size: 20px;
}
.secondary-button {
  min-height: 40px;
  padding: 8px 14px;
  color: #eee;
  background: #292929;
  border: 1px solid #666;
  border-radius: 4px;
}
.node-list {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 12px;
  margin-top: 20px;
}
.node-choice {
  display: flex;
  gap: 10px;
  align-items: start;
  padding: 14px;
  border: 1px solid #555;
  border-radius: 4px;
  cursor: pointer;
}
.node-choice.selected {
  border-color: var(--accent);
}
.node-choice input {
  margin-top: 5px;
  accent-color: var(--accent);
}
.node-choice strong,
.node-choice small {
  display: block;
}
.node-choice small {
  margin-top: 3px;
  color: var(--muted);
}
.identity-section,
.distributed-work {
  margin-top: 20px;
}
.identity-section > label,
.identity-form label {
  display: block;
  margin: 18px 0 7px;
  font-size: 14px;
  font-weight: 600;
}
.identity-form > div {
  display: flex;
  gap: 10px;
  max-width: 620px;
}
.identity-form input,
.identity-section select {
  width: 100%;
  min-height: 42px;
  padding: 9px 11px;
  color: #eee;
  background: var(--field);
  border: 1px solid #747474;
  border-radius: 4px;
}
.identity-form button {
  flex-shrink: 0;
}
.current-address code {
  overflow-wrap: anywhere;
}
.distributed-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(240px, 0.7fr);
  gap: 36px;
  margin-top: 22px;
}
.history-table-wrap {
  margin-top: 16px;
}
@media (max-width: 700px) {
  .distributed-heading {
    padding: 28px 0 22px;
  }
  .node-list {
    grid-template-columns: 1fr;
  }
  .distributed-grid {
    grid-template-columns: 1fr;
    gap: 24px;
  }
  .identity-form > div {
    flex-direction: column;
  }
}
</style>
