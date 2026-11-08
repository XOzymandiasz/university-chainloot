import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { ContractFactory, JsonRpcProvider } from 'ethers'

function localUrl(name) {
  const value = process.env[name]
  if (!value)
    throw new Error(
      'Set ' + name + ' to the address of a separate test environment.',
    )
  const url = new URL(value)
  if (
    url.protocol !== 'http:' ||
    url.hostname !== '127.0.0.1' ||
    url.port === '8545'
  ) {
    throw new Error(
      name + ' must point to a separate test on 127.0.0.1, not port 8545.',
    )
  }
  return url.origin
}

test(
  'T9–T10: backend history and a read independent of the database',
  { timeout: 60000 },
  async () => {
    const rpcUrl = localUrl('CHAINLOOT_TEST_RPC_URL')
    const apiUrl = localUrl('CHAINLOOT_TEST_API_URL')
    const provider = new JsonRpcProvider(rpcUrl, undefined, {
      staticNetwork: true,
      cacheTimeout: -1,
    })
    const independent = new JsonRpcProvider(rpcUrl, undefined, {
      staticNetwork: true,
      cacheTimeout: -1,
    })
    try {
      assert.equal(BigInt(await provider.send('eth_chainId', [])), 31337n)
      assert.equal(
        BigInt(await provider.send('eth_blockNumber', [])),
        0n,
        'The test requires a fresh, empty Anvil instance.',
      )
      const [alice, bob, charlie] = await Promise.all(
        [0, 1, 2].map((i) => provider.getSigner(i)),
      )
      const [a, b, c] = await Promise.all(
        [alice, bob, charlie].map((user) => user.getAddress()),
      )
      const artifact = JSON.parse(
        await readFile(
          new URL(
            '../../contracts/out/GameItems.sol/GameItems.json',
            import.meta.url,
          ),
          'utf8',
        ),
      )
      const items = await new ContractFactory(
        artifact.abi,
        artifact.bytecode.object,
        alice,
      ).deploy()
      await items.waitForDeployment()
      const receipts = []
      receipts.push(await (await items.issue(a, 3n)).wait())
      receipts.push(
        await (await items.connect(alice).transferFrom(a, b, 3n)).wait(),
      )
      receipts.push(
        await (await items.connect(bob).transferFrom(b, c, 3n)).wait(),
      )
      receipts.push(await (await items.connect(charlie).redeem(3n)).wait())

      const address = await items.getAddress()
      const query = '?contract=' + address + '&token=3'
      const response = await fetch(apiUrl + '/api/events' + query)
      assert.equal(response.status, 200)
      const history = await response.json()
      assert.equal(history.total, 4)
      assert.deepEqual(
        history.rows.map((row) => row.type),
        ['issue', 'transfer', 'transfer', 'redeem'],
      )
      assert.deepEqual(
        history.rows.map((row) => row.txHash),
        receipts.map((receipt) => receipt.hash),
      )
      assert.equal(new Set(history.rows.map((row) => row.operationId)).size, 4)
      assert.ok(
        history.rows.every(
          (row) =>
            row.operationId === row.txHash + ':' + row.logIndex &&
            row.tokenId === '3',
        ),
      )
      assert.equal(history.rows[0].recipient.toLowerCase(), a.toLowerCase())
      assert.equal(history.rows[1].sender.toLowerCase(), a.toLowerCase())
      assert.equal(history.rows[1].recipient.toLowerCase(), b.toLowerCase())
      assert.equal(history.rows[2].sender.toLowerCase(), b.toLowerCase())
      assert.equal(history.rows[2].recipient.toLowerCase(), c.toLowerCase())
      assert.equal(history.rows[3].sender.toLowerCase(), c.toLowerCase())
      assert.equal(history.rows[3].recipient, '')
      const csv = await fetch(
        apiUrl + '/api/events/export' + query + '&format=csv',
      )
      assert.equal(csv.status, 200)
      assert.match(await csv.text(), /operationId/)
      const json = await fetch(
        apiUrl + '/api/events/export' + query + '&format=json',
      )
      assert.equal((await json.json()).length, 4)
      console.log(
        'T9: SUCCESS — backend returns four operations and IDs in the correct order',
      )

      const ownerRaw = await independent.send('eth_call', [
        {
          to: address,
          data: items.interface.encodeFunctionData('ownerOf', [3n]),
        },
        'latest',
      ])
      const statusRaw = await independent.send('eth_call', [
        {
          to: address,
          data: items.interface.encodeFunctionData('getStatus', [3n]),
        },
        'latest',
      ])
      assert.equal(
        items.interface.decodeFunctionResult('ownerOf', ownerRaw)[0],
        c,
      )
      assert.equal(
        items.interface.decodeFunctionResult('getStatus', statusRaw)[0],
        1n,
      )
      console.log(
        'T10: SUCCESS — independent RPC read confirms Charlie and REDEEMED',
      )
    } finally {
      provider.destroy()
      independent.destroy()
    }
  },
)
