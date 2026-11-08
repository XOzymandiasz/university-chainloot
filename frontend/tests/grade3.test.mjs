import assert from 'node:assert/strict'
import test from 'node:test'
import { withIsolatedChain } from './local-chain.mjs'

test(
  'T1–T6: full asset lifecycle on a clean local Anvil',
  { timeout: 60000 },
  async () => {
    await withIsolatedChain(async ({ provider, items }) => {
      const [alice, bob, charlie] = await Promise.all(
        [0, 1, 2].map((i) => provider.getSigner(i)),
      )
      const [aliceAddress, bobAddress, charlieAddress] = await Promise.all(
        [alice, bob, charlie].map((user) => user.getAddress()),
      )
      assert.equal(new Set([aliceAddress, bobAddress, charlieAddress]).size, 3)

      const state = async (owner, status) => {
        assert.equal(await items.ownerOf(1n), owner)
        assert.equal(await items.getStatus(1n), BigInt(status))
      }

      await (await items.issue(aliceAddress, 1n)).wait()
      await state(aliceAddress, 0)
      console.log('T1: SUCCESS — A1 belongs to Alice and is ACTIVE')
      await state(aliceAddress, 0)
      console.log('T2: SUCCESS — owner lookup returns Alice')

      await (
        await items.connect(alice).transferFrom(aliceAddress, bobAddress, 1n)
      ).wait()
      await state(bobAddress, 0)
      console.log('T3: SUCCESS — Bob owns A1 and it is ACTIVE')

      await assert.rejects(async () => {
        const tx = await items
          .connect(charlie)
          .transferFrom(bobAddress, charlieAddress, 1n)
        await tx.wait()
      })
      await state(bobAddress, 0)
      console.log('T4: REJECTED — Charlie cannot transfer A1')

      await (await items.connect(bob).redeem(1n)).wait()
      await state(bobAddress, 1)
      console.log('T5: SUCCESS — A1 remains with Bob and is REDEEMED')

      await assert.rejects(async () => {
        const tx = await items.connect(bob).redeem(1n)
        await tx.wait()
      })
      await state(bobAddress, 1)
      await assert.rejects(async () => {
        const tx = await items
          .connect(bob)
          .transferFrom(bobAddress, charlieAddress, 1n)
        await tx.wait()
      })
      await state(bobAddress, 1)
      console.log(
        'T6: REJECTED — repeated REDEEM and TRANSFER do not change the state',
      )
    })
  },
)
