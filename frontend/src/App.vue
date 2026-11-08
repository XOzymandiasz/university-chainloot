<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue'
import History from './components/History.vue'
import {
  BrowserProvider,
  Contract,
  JsonRpcProvider,
  ZeroAddress,
  Wallet as EthersWallet,
  parseEther,
  type HDNodeWallet,
  getAddress,
  isAddress,
  type Eip1193Provider,
} from 'ethers'

type Wallet = Eip1193Provider & {
  on?: (event: string, listener: (...args: any[]) => void) => void
  removeListener?: (event: string, listener: (...args: any[]) => void) => void
}
const abi = [
  'function issue(address recipient, uint128 uuid) returns (uint256)',
  'function ownerOf(uint256 tokenId) view returns (address)',
  'function getStatus(uint256 tokenId) view returns (uint8)',
  'function transferFrom(address from, address to, uint256 tokenId)',
  'function redeem(uint256 tokenId)',
  'error ItemAlreadyExists(uint256 tokenId)',
  'error NotItemOwner(uint256 tokenId, address caller)',
  'error ItemAlreadyRedeemed(uint256 tokenId)',
  'error ERC721NonexistentToken(uint256 tokenId)',
]
const wallet = () => (window as Window & { ethereum?: Wallet }).ethereum
const account = ref('')
const mode = ref<'wallet' | 'local' | 'signed'>('wallet')
const localAccounts = ref<string[]>([])
const signedAccounts = shallowRef<HDNodeWallet[]>([])
const chainId = ref('')
const contractAddress = ref(import.meta.env.VITE_CONTRACT_ADDRESS || '')
const tokenId = ref('')
const recipient = ref('')
const result = ref<{ id: string; owner: string; status: string } | null>(null)
const message = ref('')
const error = ref('')
const busy = ref(false)
const operationStatus = ref<'' | 'pending' | 'success' | 'failed'>('')
const expectedChainId = '0x7a69'
const correctNetwork = computed(
  () => chainId.value.toLowerCase() === expectedChainId,
)
const shortAccount = computed(() =>
  account.value
    ? `${account.value.slice(0, 6)}…${account.value.slice(-4)}`
    : '',
)

function handleAccounts(accounts: string[]) {
  if (mode.value !== 'wallet') return
  account.value = accounts[0] || ''
  result.value = null
}
function handleChain(chain: string) {
  if (mode.value !== 'wallet') return
  chainId.value = chain
  result.value = null
}
async function connect() {
  operationStatus.value = ''
  error.value = ''
  try {
    const eth = wallet()
    if (!eth)
      throw new Error(
        'No wallet found. Install MetaMask or another Ethereum wallet.',
      )
    const accounts = (await eth.request({
      method: 'eth_requestAccounts',
    })) as string[]
    mode.value = 'wallet'
    localAccounts.value = []
    handleAccounts(accounts)
    handleChain((await eth.request({ method: 'eth_chainId' })) as string)
  } catch (e) {
    showError(e)
  }
}
async function connectLocal() {
  operationStatus.value = ''
  error.value = ''
  message.value = ''
  busy.value = true
  const provider = new JsonRpcProvider('http://127.0.0.1:8545', undefined, {
    staticNetwork: true,
  })
  try {
    const chain = (await provider.send('eth_chainId', [])) as string
    if (BigInt(chain) !== 31337n)
      throw new Error(
        'Anvil must be running at 127.0.0.1:8545 with chain ID 31337.',
      )
    const accounts = (await provider.send('eth_accounts', [])) as string[]
    if (!accounts.length)
      throw new Error('Anvil does not provide any test accounts.')
    mode.value = 'local'
    localAccounts.value = accounts
    account.value = accounts[0]!
    chainId.value = expectedChainId
    result.value = null
    message.value = 'Connected to local Anvil. Select a test account below.'
  } catch (e) {
    const problem = e as Error
    error.value =
      problem.message.startsWith('Anvil must') ||
      problem.message.startsWith('Anvil does not')
        ? problem.message
        : 'Cannot connect to Anvil. Run anvil in a terminal and leave it open.'
  } finally {
    provider.destroy()
    busy.value = false
  }
}
async function connectSignedLocal() {
  operationStatus.value = ''
  error.value = ''
  message.value = ''
  busy.value = true
  const provider = new JsonRpcProvider('http://127.0.0.1:8545', undefined, {
    staticNetwork: true,
  })
  try {
    const chain = (await provider.send('eth_chainId', [])) as string
    if (BigInt(chain) !== 31337n)
      throw new Error('The local Anvil network is required (chain ID 31337).')
    if (signedAccounts.value.length === 0) {
      const users = Array.from({ length: 3 }, () => EthersWallet.createRandom())
      const funder = await provider.getSigner(0)
      for (const user of users) {
        const transaction = await funder.sendTransaction({
          to: user.address,
          value: parseEther('0.1'),
        })
        await transaction.wait()
      }
      signedAccounts.value = users
    }
    mode.value = 'signed'
    localAccounts.value = signedAccounts.value.map((user) => user.address)
    account.value = localAccounts.value[0]!
    chainId.value = expectedChainId
    result.value = null
    message.value = 'Accounts are ready. Keys remain in memory in this tab.'
  } catch (e) {
    showError(e)
  } finally {
    provider.destroy()
    busy.value = false
  }
}
function showError(e: unknown) {
  const problem = e as {
    shortMessage?: string
    reason?: string
    message?: string
    code?: string | number
  }
  error.value =
    problem.code === 4001 || problem.code === 'ACTION_REJECTED'
      ? 'The operation was rejected in the wallet.'
      : problem.reason ||
        problem.shortMessage ||
        problem.message ||
        'Could not complete the operation.'
}
function idValue(): bigint {
  if (!/^\d+$/.test(tokenId.value.trim()))
    throw new Error('Enter the ID as a non-negative integer.')
  const id = BigInt(tokenId.value.trim())
  if (id > (1n << 128n) - 1n) throw new Error('The ID must fit within uint128.')
  return id
}
function recipientValue(): string {
  const value = recipient.value.trim()
  if (!isAddress(value) || getAddress(value) === ZeroAddress)
    throw new Error('Enter a valid, non-zero recipient address.')
  return getAddress(value)
}
async function getContract(write = false): Promise<Contract> {
  if (!account.value)
    throw new Error('Connect a local account or wallet first.')
  if (!isAddress(contractAddress.value.trim()))
    throw new Error('Enter a valid deployed contract address.')
  const address = getAddress(contractAddress.value.trim())
  if (mode.value === 'local' || mode.value === 'signed') {
    const provider = new JsonRpcProvider('http://127.0.0.1:8545', undefined, {
      staticNetwork: true,
    })
    const currentChain = (await provider.send('eth_chainId', [])) as string
    if (BigInt(currentChain) !== 31337n)
      throw new Error(
        'Anvil must be running at 127.0.0.1:8545 with chain ID 31337.',
      )
    if ((await provider.getCode(address)) === '0x')
      throw new Error(
        'No contract exists at this address on the selected network.',
      )
    if (!write) return new Contract(address, abi, provider)
    if (mode.value === 'signed') {
      const signer = signedAccounts.value.find(
        (user) => user.address === account.value,
      )
      if (!signer)
        throw new Error('The active account key was not found in this tab.')
      return new Contract(address, abi, signer.connect(provider))
    }
    return new Contract(address, abi, await provider.getSigner(account.value))
  }
  const eth = wallet()
  if (!eth) throw new Error('Connect a wallet first.')
  const currentChain = (await eth.request({ method: 'eth_chainId' })) as string
  if (currentChain.toLowerCase() !== chainId.value.toLowerCase())
    handleChain(currentChain)
  if (currentChain.toLowerCase() !== expectedChainId)
    throw new Error(
      'Switch your wallet to the local Anvil network (chain ID 31337).',
    )
  const provider = new BrowserProvider(eth)
  if ((await provider.getCode(address)) === '0x')
    throw new Error(
      'No contract exists at this address on the selected network.',
    )
  return new Contract(
    address,
    abi,
    write ? await provider.getSigner() : provider,
  )
}
async function inspect(contract: Contract, id: bigint) {
  const [owner, status] = await Promise.all([
    contract.ownerOf!(id),
    contract.getStatus!(id),
  ])
  result.value = {
    id: id.toString(),
    owner,
    status: Number(status) === 0 ? 'Active' : 'Redeemed',
  }
}
async function run(action: 'inspect' | 'issue' | 'transfer' | 'redeem') {
  operationStatus.value = 'pending'
  busy.value = true
  error.value = ''
  message.value = ''
  result.value = null
  try {
    const id = idValue()
    const destination =
      action === 'issue' || action === 'transfer' ? recipientValue() : ''
    const contract = await getContract(action !== 'inspect')
    if (action === 'inspect') {
      await inspect(contract, id)
      operationStatus.value = 'success'
      message.value = 'Asset details were read from the blockchain.'
    } else {
      message.value =
        mode.value === 'local' || mode.value === 'signed'
          ? 'Sending the transaction from the test account…'
          : 'Confirm the transaction in your wallet.'
      const tx =
        action === 'issue'
          ? await contract.issue!(destination, id)
          : action === 'transfer'
            ? await contract.transferFrom!(account.value, destination, id)
            : await contract.redeem!(id)
      message.value = 'Transaction sent. Waiting for confirmation…'
      await tx.wait()
      operationStatus.value = 'success'
      message.value = `Transaction confirmed: ${tx.hash}`
      try {
        await inspect(contract, id)
      } catch {
        /* The successful transaction can be inspected manually. */
      }
    }
  } catch (e) {
    operationStatus.value = 'failed'
    message.value = ''
    showError(e)
  } finally {
    busy.value = false
  }
}
onMounted(async () => {
  const eth = wallet()
  if (!eth) return
  eth.on?.('accountsChanged', handleAccounts)
  eth.on?.('chainChanged', handleChain)
  try {
    handleAccounts((await eth.request({ method: 'eth_accounts' })) as string[])
    handleChain((await eth.request({ method: 'eth_chainId' })) as string)
  } catch {
    /* The wallet may be locked until the user connects it. */
  }
})
onUnmounted(() => {
  wallet()?.removeListener?.('accountsChanged', handleAccounts)
  wallet()?.removeListener?.('chainChanged', handleChain)
})
</script>

<template>
  <div class="shell">
    <header class="topbar">
      <a class="brand" href="#top">ChainLoot</a>
      <div class="top-actions">
        <span class="network">
          <i
            :class="{ online: account && correctNetwork }"
            aria-hidden="true"
          ></i>
          {{ account && correctNetwork ? 'Anvil: connected' : 'Not connected' }}
        </span>
        <button class="wallet" type="button" :disabled="busy" @click="connect">
          {{ account && mode === 'wallet' ? shortAccount : 'Connect wallet' }}
        </button>
      </div>
    </header>

    <main id="top">
      <div class="page-heading">
        <div>
          <h1>Assets</h1>
          <p>Issue, inspect and transfer assets on the local network.</p>
        </div>
        <details class="help">
          <summary>How to use</summary>
          <ol>
            <li>Start Anvil and deploy the contract.</li>
            <li>Connect a test account or wallet.</li>
            <li>Enter the contract address and asset ID.</li>
            <li>
              Choose an operation. A recipient address is required to issue or
              transfer an asset.
            </li>
          </ol>
        </details>
      </div>

      <div
        v-if="account && !correctNetwork"
        class="notice warning"
        role="alert"
      >
        Switch your wallet to the local Anvil network (chain ID 31337).
      </div>

      <section class="workspace" aria-labelledby="workspace-title">
        <div class="connection">
          <div>
            <h2>Connection</h2>
            <p v-if="account && mode === 'local'">
              You are using an Anvil test account.
            </p>
            <p v-else-if="account && mode === 'signed'">
              Transactions are signed with a key in this tab.
            </p>
            <p v-else-if="account">
              Connected wallet:
              <span class="mono" :title="account">{{ shortAccount }}</span>
            </p>
            <p v-else>Connect a test account or wallet to manage assets.</p>
          </div>
          <div class="connection-actions">
            <button
              class="connect-button"
              type="button"
              :disabled="busy"
              @click="connectLocal"
            >
              {{
                account && mode === 'local'
                  ? 'Reconnect'
                  : 'Use Anvil test account'
              }}
            </button>
            <button
              class="signed-button"
              type="button"
              :disabled="busy"
              @click="connectSignedLocal"
            >
              Use locally signed accounts
            </button>
          </div>
          <p class="signed-help">
            Locally signed accounts are created on this computer. Their keys
            remain in this tab and are lost when it is refreshed or closed.
          </p>
        </div>

        <div
          v-if="mode !== 'wallet' && localAccounts.length"
          class="local-account"
        >
          <label for="local-account">Active test account</label>
          <select
            id="local-account"
            v-model="account"
            :disabled="busy"
            aria-describedby="local-account-help"
            @change="result = null"
          >
            <option
              v-for="(address, index) in localAccounts"
              :key="address"
              :value="address"
            >
              Account {{ index + 1 }} · {{ address }}
            </option>
          </select>
          <p id="local-account-help">
            {{
              mode === 'signed'
                ? 'The browser signs transactions; the key is not sent to the backend.'
                : 'In test mode, transactions are sent without wallet confirmation.'
            }}
          </p>
        </div>

        <div class="section-heading">
          <h2 id="workspace-title">Manage asset</h2>
          <p>Enter the details and choose an operation.</p>
        </div>

        <div class="grid">
          <div class="form-panel">
            <div class="field">
              <label for="contract">Contract address</label>
              <input
                id="contract"
                v-model.trim="contractAddress"
                placeholder="0x..."
                autocomplete="off"
                spellcheck="false"
                aria-describedby="contract-help"
                @input="result = null"
              />
              <p id="contract-help" class="hint">
                The address shown after deploying the contract.
              </p>
            </div>

            <div class="field">
              <label for="token">Asset ID</label>
              <input
                id="token"
                v-model.trim="tokenId"
                inputmode="numeric"
                placeholder="e.g. 1"
                autocomplete="off"
                aria-describedby="token-help"
                @input="result = null"
              />
              <p id="token-help" class="hint">
                Enter the ID of an existing or new asset.
              </p>
            </div>

            <div class="field">
              <label for="recipient"
                >Recipient address
                <span class="optional">only for issue or transfer</span></label
              >
              <input
                id="recipient"
                v-model.trim="recipient"
                placeholder="0x..."
                autocomplete="off"
                spellcheck="false"
              />
              <button
                v-if="mode !== 'wallet' && account"
                class="use-account"
                type="button"
                @click="recipient = account"
              >
                Use active account address
              </button>
            </div>
          </div>

          <div class="action-panel">
            <h3>Choose an operation</h3>
            <button type="button" :disabled="busy" @click="run('inspect')">
              Inspect asset
            </button>
            <button
              class="action-primary"
              type="button"
              :disabled="busy"
              @click="run('issue')"
            >
              Issue asset
            </button>
            <button type="button" :disabled="busy" @click="run('transfer')">
              Transfer asset
            </button>
            <button type="button" :disabled="busy" @click="run('redeem')">
              Redeem asset
            </button>
            <p>
              {{
                mode === 'local' || mode === 'signed'
                  ? 'Transactions use the active test account.'
                  : 'Transactions require wallet confirmation.'
              }}
            </p>
          </div>
        </div>

        <div v-if="message" class="notice success" role="status">
          <strong v-if="operationStatus === 'success'">SUCCESS: </strong
          >{{ message }}
        </div>
        <div v-if="error" class="notice warning" role="alert">
          <strong v-if="operationStatus === 'failed'"
            >REJECTED / FAILED: </strong
          >{{ error }}
        </div>
        <div v-if="result" class="result" aria-live="polite">
          <div>
            <span>Asset</span><strong>#{{ result.id }}</strong>
          </div>
          <div>
            <span>Status</span
            ><strong :class="{ active: result.status === 'Active' }">{{
              result.status
            }}</strong>
          </div>
          <div class="owner">
            <span>Owner</span><strong>{{ result.owner }}</strong>
          </div>
        </div>
      </section>

      <History :contract-address="contractAddress" />
      <p class="footnote">
        A redeemed asset remains with its owner and cannot be redeemed or
        transferred again.
      </p>
    </main>
    <footer><span>ChainLoot</span><span>Local environment</span></footer>
  </div>
</template>
