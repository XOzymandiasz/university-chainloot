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
  issue: 'Issue',
  transfer: 'Transfer',
  redeem: 'Redeem',
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
      response.status === 404 ? 'Asset not found.' : 'Node is unavailable.',
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
    throw new Error('The ID must be an integer with at most 39 digits.')
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
    setMessage('success', 'User created locally in this browser.')
  } catch (error) {
    setMessage(
      'error',
      error instanceof Error ? error.message : 'Could not save the user.',
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
      'SUCCESS: read state from node ' + selectedNode.value.toUpperCase() + '.',
    )
  } catch (error) {
    setMessage(
      'error',
      'FAILED: ' +
        (error instanceof Error ? error.message : 'Could not read the state.'),
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
    throw new Error('Unknown operation result.')
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
    if (!user) throw new Error('Select a local user.')
    const to = type === 'redeem' ? '' : recipient.value.trim().toLowerCase()
    if (type !== 'redeem' && !/^0x[0-9a-f]{40}$/.test(to))
      throw new Error('Enter a valid recipient address.')
    await refreshNodes()
    const leader = nodes.find(
      (node) =>
        statuses.value[node]?.role === 'leader' && statuses.value[node]?.ready,
    )
    if (!leader)
      throw new Error('No leader is available. Check the node status.')
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
        'PENDING: no response. Check the state before retrying.',
      )
    else
      setMessage(
        'error',
        'FAILED: ' +
          (error instanceof Error
            ? error.message
            : 'Could not complete the operation.'),
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
      'PENDING: no result received. Check the state before retrying.',
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
    setMessage('error', 'Could not read local browser users.')
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
      <a class="brand" href="/">ChainLoot</a>
      <span class="local-label">Local network · 3 nodes</span>
    </header>

    <main>
      <div class="distributed-heading">
        <h1>Distributed system</h1>
        <p>
          Operations are signed on this computer and confirmed by a majority of
          nodes.
        </p>
      </div>

      <section class="workspace node-section" aria-labelledby="nodes-heading">
        <div class="section-title">
          <div>
            <h2 id="nodes-heading">Nodes</h2>
            <p>Choose a node to read the state or submit an operation.</p>
          </div>
          <button type="button" class="secondary-button" @click="refreshNodes">
            Refresh
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
              <strong>Node {{ node.toUpperCase() }}</strong>
              <small v-if="statuses[node]">
                {{
                  statuses[node]?.role === 'leader'
                    ? 'Leader'
                    : statuses[node]?.role === 'candidate'
                      ? 'Leader election'
                      : 'Follower'
                }}
                · committed: {{ statuses[node]?.commitIndex }}
              </small>
              <small v-else>Unavailable</small>
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
            <h2 id="users-heading">Local users</h2>
            <p>
              The private key is created and kept in this browser. It is not
              sent to the nodes.
            </p>
          </div>
        </div>
        <form class="identity-form" @submit.prevent="addUser">
          <label for="user-name">Username</label>
          <div>
            <input
              id="user-name"
              v-model.trim="name"
              maxlength="40"
              placeholder="e.g. Alice"
            />
            <button class="connect-button" type="submit" :disabled="busy">
              Add locally
            </button>
          </div>
        </form>
        <p class="identity-note">
          Each user should use a separate browser profile. Clearing browser data
          removes local keys.
        </p>
        <label for="active-user">Active user</label>
        <select id="active-user" v-model="selectedIdentity" :disabled="busy">
          <option value="">Select a user</option>
          <option
            v-for="user in identities"
            :key="user.address"
            :value="user.address"
          >
            {{ user.name }} · {{ user.address }}
          </option>
        </select>
        <p v-if="activeIdentity" class="current-address">
          Address: <code>{{ activeIdentity.address }}</code>
        </p>
      </section>

      <section
        class="workspace distributed-work"
        aria-labelledby="asset-heading"
      >
        <div class="section-title">
          <div>
            <h2 id="asset-heading">Asset</h2>
            <p>Read the state or sign and submit an operation.</p>
          </div>
        </div>
        <div class="distributed-grid">
          <div class="form-panel">
            <div class="field">
              <label for="asset-id">Asset ID</label>
              <input
                id="asset-id"
                v-model.trim="assetId"
                inputmode="numeric"
                placeholder="e.g. 1"
              />
            </div>
            <div class="field">
              <label for="cluster-recipient"
                >Recipient address
                <span class="optional">for issue and transfer</span></label
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
                Use active user address
              </button>
            </div>
          </div>
          <div class="action-panel">
            <h3>Operations</h3>
            <button type="button" :disabled="busy" @click="inspect">
              Inspect asset
            </button>
            <button type="button" :disabled="busy" @click="operate('issue')">
              Issue asset
            </button>
            <button type="button" :disabled="busy" @click="operate('transfer')">
              Transfer asset
            </button>
            <button type="button" :disabled="busy" @click="operate('redeem')">
              Redeem asset
            </button>
            <button
              v-if="lastSigned"
              type="button"
              :disabled="busy"
              @click="replay"
            >
              Resend last operation
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
            <span>Asset</span><strong>#{{ asset.id }}</strong>
          </div>
          <div>
            <span>Status</span><strong>{{ asset.status }}</strong>
          </div>
          <div class="owner">
            <span>Owner</span><strong>{{ asset.owner }}</strong>
          </div>
        </div>
      </section>

      <section v-if="asset" class="history" aria-labelledby="cluster-history">
        <h2 id="cluster-history">Asset history #{{ asset.id }}</h2>
        <p>Committed operations in order, according to the selected node.</p>
        <div class="history-table-wrap">
          <table class="history-table">
            <thead>
              <tr>
                <th>Operation</th>
                <th>Sender</th>
                <th>Recipient</th>
                <th>Operation ID</th>
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
        State is read from the selected node; choose another if it fails.
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
