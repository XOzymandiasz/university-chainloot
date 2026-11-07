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
        'Nie znaleziono portfela. Zainstaluj MetaMask lub inny portfel Ethereum.',
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
        'Pod adresem 127.0.0.1:8545 musi działać Anvil z chain ID 31337.',
      )
    const accounts = (await provider.send('eth_accounts', [])) as string[]
    if (!accounts.length)
      throw new Error('Anvil nie udostępnia kont testowych.')
    mode.value = 'local'
    localAccounts.value = accounts
    account.value = accounts[0]!
    chainId.value = expectedChainId
    result.value = null
    message.value = 'Połączono z lokalnym Anvil. Wybierz konto testowe poniżej.'
  } catch (e) {
    const problem = e as Error
    error.value =
      problem.message.startsWith('Pod adresem') ||
      problem.message.startsWith('Anvil nie')
        ? problem.message
        : 'Nie można połączyć się z Anvil. Uruchom polecenie anvil w terminalu i pozostaw go otwartym.'
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
      throw new Error('Wymagana jest lokalna sieć Anvil (chain ID 31337).')
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
    message.value = 'Konta są gotowe. Klucze są tylko w pamięci tej karty.'
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
      ? 'Operacja została odrzucona w portfelu.'
      : problem.reason ||
        problem.shortMessage ||
        problem.message ||
        'Nie udało się wykonać operacji.'
}
function idValue(): bigint {
  if (!/^\d+$/.test(tokenId.value.trim()))
    throw new Error('Podaj identyfikator jako nieujemną liczbę całkowitą.')
  const id = BigInt(tokenId.value.trim())
  if (id > (1n << 128n) - 1n)
    throw new Error('Identyfikator musi mieścić się w zakresie uint128.')
  return id
}
function recipientValue(): string {
  const value = recipient.value.trim()
  if (!isAddress(value) || getAddress(value) === ZeroAddress)
    throw new Error('Podaj poprawny, niezerowy adres odbiorcy.')
  return getAddress(value)
}
async function getContract(write = false): Promise<Contract> {
  if (!account.value)
    throw new Error('Najpierw połącz konto lokalne lub portfel.')
  if (!isAddress(contractAddress.value.trim()))
    throw new Error('Podaj poprawny adres wdrożonego kontraktu.')
  const address = getAddress(contractAddress.value.trim())
  if (mode.value === 'local' || mode.value === 'signed') {
    const provider = new JsonRpcProvider('http://127.0.0.1:8545', undefined, {
      staticNetwork: true,
    })
    const currentChain = (await provider.send('eth_chainId', [])) as string
    if (BigInt(currentChain) !== 31337n)
      throw new Error(
        'Pod adresem 127.0.0.1:8545 musi działać Anvil z chain ID 31337.',
      )
    if ((await provider.getCode(address)) === '0x')
      throw new Error('Pod tym adresem w wybranej sieci nie ma kontraktu.')
    if (!write) return new Contract(address, abi, provider)
    if (mode.value === 'signed') {
      const signer = signedAccounts.value.find(
        (user) => user.address === account.value,
      )
      if (!signer)
        throw new Error('Nie znaleziono klucza aktywnego konta w tej karcie.')
      return new Contract(address, abi, signer.connect(provider))
    }
    return new Contract(address, abi, await provider.getSigner(account.value))
  }
  const eth = wallet()
  if (!eth) throw new Error('Najpierw połącz portfel.')
  const currentChain = (await eth.request({ method: 'eth_chainId' })) as string
  if (currentChain.toLowerCase() !== chainId.value.toLowerCase())
    handleChain(currentChain)
  if (currentChain.toLowerCase() !== expectedChainId)
    throw new Error('Przełącz portfel na lokalną sieć Anvil (chain ID 31337).')
  const provider = new BrowserProvider(eth)
  if ((await provider.getCode(address)) === '0x')
    throw new Error('Pod tym adresem w wybranej sieci nie ma kontraktu.')
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
    status: Number(status) === 0 ? 'Aktywny' : 'Wykorzystany',
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
      message.value = 'Dane przedmiotu zostały odczytane z blockchaina.'
    } else {
      message.value =
        mode.value === 'local' || mode.value === 'signed'
          ? 'Wysyłanie transakcji z konta testowego…'
          : 'Potwierdź transakcję w portfelu.'
      const tx =
        action === 'issue'
          ? await contract.issue!(destination, id)
          : action === 'transfer'
            ? await contract.transferFrom!(account.value, destination, id)
            : await contract.redeem!(id)
      message.value = 'Transakcja wysłana. Oczekiwanie na potwierdzenie…'
      await tx.wait()
      operationStatus.value = 'success'
      message.value = `Transakcja potwierdzona: ${tx.hash}`
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
          {{ account && correctNetwork ? 'Anvil: połączono' : 'Nie połączono' }}
        </span>
        <button class="wallet" type="button" :disabled="busy" @click="connect">
          {{ account && mode === 'wallet' ? shortAccount : 'Połącz portfel' }}
        </button>
      </div>
    </header>

    <main id="top">
      <div class="page-heading">
        <div>
          <h1>Przedmioty</h1>
          <p>Wystawiaj, sprawdzaj i przekazuj przedmioty w sieci lokalnej.</p>
        </div>
        <details class="help">
          <summary>Jak korzystać?</summary>
          <ol>
            <li>Uruchom Anvil i wdróż kontrakt.</li>
            <li>Połącz konto testowe lub portfel.</li>
            <li>Wpisz adres kontraktu i identyfikator przedmiotu.</li>
            <li>
              Wybierz operację. Adres odbiorcy jest potrzebny do wystawienia i
              przekazania.
            </li>
          </ol>
        </details>
      </div>

      <div
        v-if="account && !correctNetwork"
        class="notice warning"
        role="alert"
      >
        Przełącz portfel na lokalną sieć Anvil (chain ID 31337).
      </div>

      <section class="workspace" aria-labelledby="workspace-title">
        <div class="connection">
          <div>
            <h2>Połączenie</h2>
            <p v-if="account && mode === 'local'">
              Korzystasz z testowego konta Anvil.
            </p>
            <p v-else-if="account && mode === 'signed'">
              Podpisujesz transakcje kluczem w tej karcie.
            </p>
            <p v-else-if="account">
              Połączony portfel:
              <span class="mono" :title="account">{{ shortAccount }}</span>
            </p>
            <p v-else>
              Połącz konto testowe lub portfel, aby zarządzać przedmiotami.
            </p>
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
                  ? 'Połącz ponownie'
                  : 'Użyj konta testowego Anvil'
              }}
            </button>
            <button
              class="signed-button"
              type="button"
              :disabled="busy"
              @click="connectSignedLocal"
            >
              Użyj kont podpisywanych lokalnie
            </button>
          </div>
          <p class="signed-help">
            Konta podpisywane powstają na tym komputerze. Ich klucze pozostają w
            pamięci tej karty i znikną po jej odświeżeniu lub zamknięciu.
          </p>
        </div>

        <div
          v-if="mode !== 'wallet' && localAccounts.length"
          class="local-account"
        >
          <label for="local-account">Aktywne konto testowe</label>
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
              Konto {{ index + 1 }} · {{ address }}
            </option>
          </select>
          <p id="local-account-help">
            {{
              mode === 'signed'
                ? 'Podpis powstaje w przeglądarce; klucz nie jest wysyłany do backendu.'
                : 'W trybie testowym transakcje są wysyłane bez potwierdzenia w portfelu.'
            }}
          </p>
        </div>

        <div class="section-heading">
          <h2 id="workspace-title">Zarządzaj przedmiotem</h2>
          <p>Wpisz dane, a następnie wybierz operację.</p>
        </div>

        <div class="grid">
          <div class="form-panel">
            <div class="field">
              <label for="contract">Adres kontraktu</label>
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
                Adres wyświetlony po wdrożeniu kontraktu.
              </p>
            </div>

            <div class="field">
              <label for="token">ID przedmiotu</label>
              <input
                id="token"
                v-model.trim="tokenId"
                inputmode="numeric"
                placeholder="np. 1"
                autocomplete="off"
                aria-describedby="token-help"
                @input="result = null"
              />
              <p id="token-help" class="hint">
                Podaj numer istniejącego lub nowego przedmiotu.
              </p>
            </div>

            <div class="field">
              <label for="recipient"
                >Adres odbiorcy
                <span class="optional"
                  >tylko przy wystawieniu lub przekazaniu</span
                ></label
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
                Wstaw adres aktywnego konta
              </button>
            </div>
          </div>

          <div class="action-panel">
            <h3>Wybierz operację</h3>
            <button type="button" :disabled="busy" @click="run('inspect')">
              Sprawdź przedmiot
            </button>
            <button
              class="action-primary"
              type="button"
              :disabled="busy"
              @click="run('issue')"
            >
              Wystaw przedmiot
            </button>
            <button type="button" :disabled="busy" @click="run('transfer')">
              Przekaż przedmiot
            </button>
            <button type="button" :disabled="busy" @click="run('redeem')">
              Wykorzystaj przedmiot
            </button>
            <p>
              {{
                mode === 'local' || mode === 'signed'
                  ? 'Transakcje wykonuje aktywne konto testowe.'
                  : 'Transakcje wymagają potwierdzenia w portfelu.'
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
            <span>Przedmiot</span><strong>#{{ result.id }}</strong>
          </div>
          <div>
            <span>Status</span
            ><strong :class="{ active: result.status === 'Aktywny' }">{{
              result.status
            }}</strong>
          </div>
          <div class="owner">
            <span>Właściciel</span><strong>{{ result.owner }}</strong>
          </div>
        </div>
      </section>

      <History :contract-address="contractAddress" />
      <p class="footnote">
        Wykorzystany przedmiot zostaje u właściciela. Nie można go ponownie
        wykorzystać ani przekazać.
      </p>
    </main>
    <footer><span>ChainLoot</span><span>Środowisko lokalne</span></footer>
  </div>
</template>
