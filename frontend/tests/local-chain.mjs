import { spawn } from 'node:child_process'
import { readFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { ContractFactory, JsonRpcProvider } from 'ethers'

async function unusedPort() {
  const server = createServer()
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  const port = server.address().port
  await new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  )
  return port
}

async function waitForRpc(url, anvil) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (anvil.exitCode !== null)
      throw new Error('Anvil zakończył pracę przed rozpoczęciem testu.')
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          jsonrpc: '2.0',
          id: 1,
          method: 'eth_chainId',
          params: [],
        }),
        signal: AbortSignal.timeout(1000),
      })
      if (response.ok && (await response.json()).result === '0x7a69') return
    } catch {
      // The isolated node may still be starting.
    }
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  throw new Error('Nie udało się uruchomić osobnego Anvil do testów.')
}

export async function withIsolatedChain(run) {
  const artifactUrl = new URL(
    '../../contracts/out/GameItems.sol/GameItems.json',
    import.meta.url,
  )
  const artifact = JSON.parse(await readFile(artifactUrl, 'utf8'))
  const port = await unusedPort()
  const url = 'http://127.0.0.1:' + port
  const anvil = spawn(
    'anvil',
    ['--host', '127.0.0.1', '--port', String(port), '--silent'],
    { stdio: 'ignore' },
  )
  let provider
  try {
    await waitForRpc(url, anvil)
    provider = new JsonRpcProvider(url, undefined, {
      staticNetwork: true,
      cacheTimeout: -1,
    })
    const deployer = await provider.getSigner(0)
    const items = await new ContractFactory(
      artifact.abi,
      artifact.bytecode.object,
      deployer,
    ).deploy()
    await items.waitForDeployment()
    await run({ provider, deployer, items, url })
  } finally {
    provider?.destroy()
    anvil.kill('SIGTERM')
  }
}
