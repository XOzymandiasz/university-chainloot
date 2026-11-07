import assert from 'node:assert/strict'
import test from 'node:test'
import { JsonRpcProvider } from 'ethers'
import { withIsolatedChain } from './local-chain.mjs'

test(
  'T9–T10: pełna historia A3 i niezależny odczyt stanu z RPC',
  { timeout: 60000 },
  async () => {
    await withIsolatedChain(async ({ provider, items, url }) => {
      const [alice, bob, charlie] = await Promise.all(
        [0, 1, 2].map((i) => provider.getSigner(i)),
      )
      const [aliceAddress, bobAddress, charlieAddress] = await Promise.all(
        [alice, bob, charlie].map((user) => user.getAddress()),
      )

      const receipts = []
      receipts.push(await (await items.issue(aliceAddress, 3n)).wait())
      receipts.push(
        await (
          await items.connect(alice).transferFrom(aliceAddress, bobAddress, 3n)
        ).wait(),
      )
      receipts.push(
        await (
          await items.connect(bob).transferFrom(bobAddress, charlieAddress, 3n)
        ).wait(),
      )
      receipts.push(await (await items.connect(charlie).redeem(3n)).wait())

      const transfers = await items.queryFilter(
        items.filters.Transfer(null, null, 3n),
        0,
        'latest',
      )
      const redemptions = await items.queryFilter(
        items.filters.ItemRedeemed(3n, null),
        0,
        'latest',
      )
      const events = [...transfers, ...redemptions].sort(
        (a, b) => a.blockNumber - b.blockNumber || a.index - b.index,
      )
      assert.equal(events.length, 4)
      assert.deepEqual(
        events.map((event) => event.transactionHash),
        receipts.map((receipt) => receipt.hash),
      )
      assert.equal(
        new Set(
          events.map((event) => event.transactionHash + ':' + event.index),
        ).size,
        4,
      )
      assert.deepEqual(
        events.map((event) => event.fragment.name),
        ['Transfer', 'Transfer', 'Transfer', 'ItemRedeemed'],
      )
      assert.equal(events[0].args.to, aliceAddress)
      assert.equal(events[1].args.from, aliceAddress)
      assert.equal(events[1].args.to, bobAddress)
      assert.equal(events[2].args.from, bobAddress)
      assert.equal(events[2].args.to, charlieAddress)
      assert.equal(events[3].args.owner, charlieAddress)
      assert.ok(events.every((event) => event.args.tokenId === 3n))
      console.log(
        'T9 (kontrakt): SUCCESS — ISSUE, Alice→Bob, Bob→Charlie, REDEEM w kolejności chronologicznej',
      )

      // Independent raw RPC reads bypass both the frontend and application database.
      const independent = new JsonRpcProvider(url, undefined, {
        staticNetwork: true,
        cacheTimeout: -1,
      })
      try {
        const address = await items.getAddress()
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
          charlieAddress,
        )
        assert.equal(
          items.interface.decodeFunctionResult('getStatus', statusRaw)[0],
          1n,
        )
        console.log(
          'T10: SUCCESS — bezpośredni RPC potwierdza Charlie i REDEEMED',
        )
      } finally {
        independent.destroy()
      }
    })
  },
)
